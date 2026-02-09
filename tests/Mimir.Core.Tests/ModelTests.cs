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

    // --- MimirProject.Environments ---

    [Fact]
    public void MimirProject_Environments_SerializesCorrectly()
    {
        var project = new MimirProject
        {
            Version = 2,
            Environments = new Dictionary<string, EnvironmentConfig>
            {
                ["server"] = new() { ImportPath = "Z:/Server/9Data" },
                ["client"] = new() { ImportPath = "Z:/Client/ressystem" }
            }
        };

        var json = JsonSerializer.Serialize(project, JsonOptions);
        json.ShouldContain("\"environments\"");
        json.ShouldContain("Z:/Server/9Data");

        var deserialized = JsonSerializer.Deserialize<MimirProject>(json, JsonOptions)!;
        deserialized.Environments.ShouldNotBeNull();
        deserialized.Environments!.Count.ShouldBe(2);
        deserialized.Environments["server"].ImportPath.ShouldBe("Z:/Server/9Data");
    }

    [Fact]
    public void MimirProject_Environments_OmittedWhenNull()
    {
        var project = new MimirProject { Version = 1 };
        var json = JsonSerializer.Serialize(project, JsonOptions);
        json.ShouldNotContain("environments");
    }

    // --- EnvironmentInfo replaces SourceOrigin ---

    [Fact]
    public void EnvironmentInfo_HasExpectedConstants()
    {
        EnvironmentInfo.MetadataKey.ShouldBe("environments");
        EnvironmentInfo.MergedOrigin.ShouldBe("merged");
    }
}
