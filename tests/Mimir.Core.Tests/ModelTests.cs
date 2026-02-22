using System.Text.Json;
using System.Text.Json.Serialization;
using Mimir.Core.Models;
using Mimir.Core.Project;
using Shouldly;
using Xunit;

namespace Mimir.Core.Tests;

public class ModelTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    // --- ColumnDefinition.Environments ---

    [Fact]
    public void ColumnDefinition_Environments_NullByDefault()
    {
        var col = new ColumnDefinition { Name = "ID", Type = ColumnType.UInt32, Length = 4 };
        col.Environments.ShouldBeNull();
    }

    [Fact]
    public void ColumnDefinition_Environments_SerializesWhenSet()
    {
        var col = new ColumnDefinition
        {
            Name = "ViewCol", Type = ColumnType.String, Length = 32,
            Environments = ["client"]
        };

        var json = JsonSerializer.Serialize(col, JsonOptions);
        json.ShouldContain("\"environments\"");
        json.ShouldContain("client");
    }

    [Fact]
    public void ColumnDefinition_Environments_OmittedWhenNull()
    {
        var col = new ColumnDefinition { Name = "ID", Type = ColumnType.UInt32, Length = 4 };
        var json = JsonSerializer.Serialize(col, JsonOptions);
        json.ShouldNotContain("environments");
    }

    [Fact]
    public void ColumnDefinition_Environments_RoundTrips()
    {
        var col = new ColumnDefinition
        {
            Name = "AC", Type = ColumnType.UInt16, Length = 2,
            Environments = ["server", "client"]
        };

        var json = JsonSerializer.Serialize(col, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ColumnDefinition>(json, JsonOptions)!;

        deserialized.Environments.ShouldNotBeNull();
        deserialized.Environments.ShouldBe(["server", "client"]);
    }

    // --- TableFile.RowEnvironments ---

    [Fact]
    public void TableFile_RowEnvironments_NullByDefault()
    {
        var file = new TableFile
        {
            Header = new TableHeader { TableName = "Test", SourceFormat = "shn" },
            Columns = [],
            Data = []
        };
        file.RowEnvironments.ShouldBeNull();
    }

    [Fact]
    public void TableFile_RowEnvironments_SerializesWhenSet()
    {
        var file = new TableFile
        {
            Header = new TableHeader { TableName = "Test", SourceFormat = "shn" },
            Columns = [],
            Data = [new Dictionary<string, object?> { ["ID"] = 1 }],
            RowEnvironments = [["server"]]
        };

        var json = JsonSerializer.Serialize(file, JsonOptions);
        json.ShouldContain("\"rowEnvironments\"");
    }

    [Fact]
    public void TableFile_RowEnvironments_NullEntryMeansShared()
    {
        // null entry in the list = row present in ALL environments
        var file = new TableFile
        {
            Header = new TableHeader { TableName = "Test", SourceFormat = "shn" },
            Columns = [],
            Data = [
                new Dictionary<string, object?> { ["ID"] = 1 },
                new Dictionary<string, object?> { ["ID"] = 2 },
            ],
            RowEnvironments = [null, ["client"]]
        };

        var json = JsonSerializer.Serialize(file, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<TableFile>(json, JsonOptions)!;

        deserialized.RowEnvironments.ShouldNotBeNull();
        deserialized.RowEnvironments!.Count.ShouldBe(2);
        deserialized.RowEnvironments[0].ShouldBeNull(); // shared
        deserialized.RowEnvironments[1].ShouldNotBeNull();
        deserialized.RowEnvironments[1]!.ShouldBe(["client"]);
    }

    // --- EnvironmentConfig ---

    [Fact]
    public void EnvironmentConfig_ImportPathRequired()
    {
        var env = new EnvironmentConfig { ImportPath = "Z:/Server/9Data" };
        env.ImportPath.ShouldBe("Z:/Server/9Data");
        env.BuildPath.ShouldBeNull();
    }

    [Fact]
    public void EnvironmentConfig_BuildPathOptional()
    {
        var env = new EnvironmentConfig
        {
            ImportPath = "Z:/Server/9Data",
            BuildPath = "./build/server"
        };
        env.BuildPath.ShouldBe("./build/server");
    }

    // --- EnvironmentConfig serialization (now stored in environments/<name>.json, not mimir.json) ---

    [Fact]
    public void EnvironmentConfig_SerializesCorrectly()
    {
        var config = new EnvironmentConfig
        {
            ImportPath = "Z:/Server/9Data",
            BuildPath = "build/server",
            SeedPackBaseline = false
        };

        var json = JsonSerializer.Serialize(config, JsonOptions);
        json.ShouldContain("\"importPath\"");
        json.ShouldContain("Z:/Server/9Data");

        var deserialized = JsonSerializer.Deserialize<EnvironmentConfig>(json, JsonOptions)!;
        deserialized.ImportPath.ShouldBe("Z:/Server/9Data");
        deserialized.BuildPath.ShouldBe("build/server");
    }

    [Fact]
    public void MimirProject_DoesNotContainEnvironments()
    {
        var project = new MimirProject { Version = 1 };
        var json = JsonSerializer.Serialize(project, JsonOptions);
        // environments are now stored in environments/<name>.json, not mimir.json
        json.ShouldNotContain("\"environments\"");
    }

    // --- EnvironmentInfo replaces SourceOrigin ---

    [Fact]
    public void EnvironmentInfo_HasExpectedConstants()
    {
        EnvironmentInfo.MetadataKey.ShouldBe("environments");
        EnvironmentInfo.MergedOrigin.ShouldBe("merged");
    }

    // --- EnvMergeMetadata.SourceRelDir ---

    [Fact]
    public void EnvMergeMetadata_SourceRelDir_DefaultsToNull()
    {
        var meta = new EnvMergeMetadata
        {
            ColumnOrder = ["ID"],
            ColumnOverrides = new(),
            ColumnRenames = new()
        };
        meta.SourceRelDir.ShouldBeNull();
    }

    [Fact]
    public void EnvMergeMetadata_SourceRelDir_CanBeSet()
    {
        var meta = new EnvMergeMetadata
        {
            ColumnOrder = ["ID"],
            ColumnOverrides = new(),
            ColumnRenames = new(),
            SourceRelDir = "Shine"
        };
        meta.SourceRelDir.ShouldBe("Shine");
    }

    [Fact]
    public void EnvMergeMetadata_SourceRelDir_RoundTripsThroughJson()
    {
        // Simulate the import Phase 4 serialization → ExtractEnvMetadata deserialization
        var original = new EnvMergeMetadata
        {
            ColumnOrder = ["ID", "Name"],
            ColumnOverrides = new() { ["Name"] = new ColumnOverride { Length = 32 } },
            ColumnRenames = new() { ["Value__client"] = "Value" },
            SourceRelDir = "Shine"
        };

        // Serialize like import Phase 4 does
        var metaDict = new Dictionary<string, object?>
        {
            ["columnOrder"] = original.ColumnOrder,
            ["columnOverrides"] = original.ColumnOverrides.Count > 0 ? original.ColumnOverrides : null,
            ["columnRenames"] = original.ColumnRenames.Count > 0 ? original.ColumnRenames : null,
            ["sourceRelDir"] = original.SourceRelDir
        };

        var json = JsonSerializer.Serialize(metaDict, JsonOptions);
        var je = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);

        // Parse back using the static method
        var parsed = EnvMergeMetadata.FromJsonElement(je);

        parsed.ShouldNotBeNull();
        parsed!.ColumnOrder.ShouldBe(["ID", "Name"]);
        parsed.ColumnOverrides.ShouldContainKey("Name");
        parsed.ColumnOverrides["Name"].Length.ShouldBe(32);
        parsed.ColumnRenames.ShouldContainKey("Value__client");
        parsed.ColumnRenames["Value__client"].ShouldBe("Value");
        parsed.SourceRelDir.ShouldBe("Shine");
    }

    [Fact]
    public void EnvMergeMetadata_FromJsonElement_EmptySourceRelDir_ReturnsEmptyString()
    {
        var metaDict = new Dictionary<string, object?>
        {
            ["columnOrder"] = new List<string> { "ID" },
            ["sourceRelDir"] = ""
        };

        var json = JsonSerializer.Serialize(metaDict, JsonOptions);
        var je = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);

        var parsed = EnvMergeMetadata.FromJsonElement(je);

        parsed.ShouldNotBeNull();
        parsed!.SourceRelDir.ShouldBe("");
    }

    [Fact]
    public void EnvMergeMetadata_FromJsonElement_MissingSourceRelDir_ReturnsNull()
    {
        var metaDict = new Dictionary<string, object?>
        {
            ["columnOrder"] = new List<string> { "ID" }
        };

        var json = JsonSerializer.Serialize(metaDict, JsonOptions);
        var je = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);

        var parsed = EnvMergeMetadata.FromJsonElement(je);

        parsed.ShouldNotBeNull();
        parsed!.SourceRelDir.ShouldBeNull();
    }
}
