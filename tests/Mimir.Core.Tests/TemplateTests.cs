using System.Text.Json;
using System.Text.Json.Serialization;
using Mimir.Core.Templates;
using Shouldly;
using Xunit;

namespace Mimir.Core.Tests;

public class TemplateTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    // --- Model serialization ---

    [Fact]
    public void CopyAction_RoundTrips()
    {
        var action = new TemplateAction
        {
            Action = "copy",
            From = new TableRef { Table = "ItemInfo", Env = "server" },
            To = "ItemInfo"
        };

        var json = JsonSerializer.Serialize(action, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<TemplateAction>(json, JsonOptions)!;

        deserialized.Action.ShouldBe("copy");
        deserialized.From.ShouldNotBeNull();
        deserialized.From!.Table.ShouldBe("ItemInfo");
        deserialized.From.Env.ShouldBe("server");
        deserialized.To.ShouldBe("ItemInfo");
    }

    [Fact]
    public void MergeAction_RoundTrips()
    {
        var action = new TemplateAction
        {
            Action = "merge",
            From = new TableRef { Table = "ItemInfo", Env = "client" },
            Into = "ItemInfo",
            On = new JoinClause { Source = "InxName", Target = "InxName" },
            ColumnStrategy = "auto"
        };

        var json = JsonSerializer.Serialize(action, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<TemplateAction>(json, JsonOptions)!;

        deserialized.Action.ShouldBe("merge");
        deserialized.Into.ShouldBe("ItemInfo");
        deserialized.On.ShouldNotBeNull();
        deserialized.On!.Source.ShouldBe("InxName");
        deserialized.On.Target.ShouldBe("InxName");
        deserialized.ColumnStrategy.ShouldBe("auto");
    }

    [Fact]
    public void SetPrimaryKeyAction_RoundTrips()
    {
        var action = new TemplateAction
        {
            Action = "setPrimaryKey",
            Table = "ItemInfo",
            Column = "ID"
        };

        var json = JsonSerializer.Serialize(action, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<TemplateAction>(json, JsonOptions)!;

        deserialized.Action.ShouldBe("setPrimaryKey");
        deserialized.Table.ShouldBe("ItemInfo");
        deserialized.Column.ShouldBe("ID");
    }

    [Fact]
    public void SetForeignKeyAction_RoundTrips()
    {
        var action = new TemplateAction
        {
            Action = "setForeignKey",
            Table = "NPCItemList",
            Column = "Column*",
            References = new ForeignKeyRef { Table = "ItemInfo", Column = "InxName" },
            EmptyValues = ["-", ""]
        };

        var json = JsonSerializer.Serialize(action, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<TemplateAction>(json, JsonOptions)!;

        deserialized.Action.ShouldBe("setForeignKey");
        deserialized.References.ShouldNotBeNull();
        deserialized.References!.Table.ShouldBe("ItemInfo");
        deserialized.References.Column.ShouldBe("InxName");
        deserialized.EmptyValues.ShouldBe(["-", ""]);
    }

    [Fact]
    public void AnnotateColumnAction_RoundTrips()
    {
        var action = new TemplateAction
        {
            Action = "annotateColumn",
            Table = "ItemInfo",
            Column = "AC",
            DisplayName = "Armor Class",
            Description = "Defense stat, +N when equipped"
        };

        var json = JsonSerializer.Serialize(action, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<TemplateAction>(json, JsonOptions)!;

        deserialized.Action.ShouldBe("annotateColumn");
        deserialized.DisplayName.ShouldBe("Armor Class");
        deserialized.Description.ShouldBe("Defense stat, +N when equipped");
    }

    [Fact]
    public void ProjectTemplate_FullRoundTrip()
    {
        var template = new ProjectTemplate
        {
            Actions =
            [
                new TemplateAction
                {
                    Action = "copy",
                    From = new TableRef { Table = "ItemInfo", Env = "server" },
                    To = "ItemInfo"
                },
                new TemplateAction
                {
                    Action = "merge",
                    From = new TableRef { Table = "ItemInfo", Env = "client" },
                    Into = "ItemInfo",
                    On = new JoinClause { Source = "InxName", Target = "InxName" },
                    ColumnStrategy = "auto"
                },
                new TemplateAction
                {
                    Action = "setPrimaryKey",
                    Table = "ItemInfo",
                    Column = "ID"
                }
            ]
        };

        var json = JsonSerializer.Serialize(template, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ProjectTemplate>(json, JsonOptions)!;

        deserialized.Actions.Count.ShouldBe(3);
        deserialized.Actions[0].Action.ShouldBe("copy");
        deserialized.Actions[1].Action.ShouldBe("merge");
        deserialized.Actions[2].Action.ShouldBe("setPrimaryKey");
    }

    [Fact]
    public void JoinClause_DifferentColumnNames()
    {
        var join = new JoinClause { Source = "InxName", Target = "ClientInxName" };
        var json = JsonSerializer.Serialize(join, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<JoinClause>(json, JsonOptions)!;

        deserialized.Source.ShouldBe("InxName");
        deserialized.Target.ShouldBe("ClientInxName");
    }

    [Fact]
    public void SetUniqueKeyAction_RoundTrips()
    {
        var action = new TemplateAction
        {
            Action = "setUniqueKey",
            Table = "ItemInfo",
            Column = "InxName"
        };

        var json = JsonSerializer.Serialize(action, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<TemplateAction>(json, JsonOptions)!;

        deserialized.Action.ShouldBe("setUniqueKey");
        deserialized.Table.ShouldBe("ItemInfo");
        deserialized.Column.ShouldBe("InxName");
    }

    // --- TemplateResolver ---

    [Fact]
    public async Task LoadAsync_SearchesUpward()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mimir_test_{Guid.NewGuid():N}");
        var childDir = Path.Combine(tempDir, "child");
        Directory.CreateDirectory(childDir);

        try
        {
            var templatePath = Path.Combine(tempDir, TemplateResolver.TemplateFileName);
            await File.WriteAllTextAsync(templatePath, """
            {
              "actions": [
                { "action": "setPrimaryKey", "table": "TestTable", "column": "ID" }
              ]
            }
            """);

            var template = await TemplateResolver.LoadAsync(childDir);
            template.ShouldNotBeNull();
            template.Actions.Count.ShouldBe(1);
            template.Actions[0].Table.ShouldBe("TestTable");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ReturnsEmpty_WhenNotFound()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mimir_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var template = await TemplateResolver.LoadAsync(tempDir);
            template.ShouldNotBeNull();
            template.Actions.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_WritesToProjectDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mimir_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var template = new ProjectTemplate
            {
                Actions =
                [
                    new TemplateAction { Action = "setPrimaryKey", Table = "Test", Column = "ID" }
                ]
            };

            await TemplateResolver.SaveAsync(tempDir, template);

            var filePath = Path.Combine(tempDir, TemplateResolver.TemplateFileName);
            File.Exists(filePath).ShouldBeTrue();

            var loaded = await TemplateResolver.LoadAsync(tempDir);
            loaded.Actions.Count.ShouldBe(1);
            loaded.Actions[0].Action.ShouldBe("setPrimaryKey");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
