using Mimir.Core.Models;
using Mimir.Core.Templates;
using Shouldly;
using Xunit;

namespace Mimir.Core.Tests;

public class TemplateGeneratorTests
{
    private static ColumnDefinition Col(string name, ColumnType type, int length = 4) =>
        new() { Name = name, Type = type, Length = length };

    private static TableFile MakeTable(string name, IReadOnlyList<ColumnDefinition> columns) => new()
    {
        Header = new TableHeader { TableName = name, SourceFormat = "shn" },
        Columns = columns,
        Data = []
    };

    [Fact]
    public void SingleEnvTable_GeneratesCopyOnly()
    {
        var envTables = new Dictionary<(string table, string env), TableFile>
        {
            [("MonsterInfo", "server")] = MakeTable("MonsterInfo",
                [Col("ID", ColumnType.UInt16), Col("InxName", ColumnType.String, 64)])
        };

        var template = TemplateGenerator.Generate(envTables, ["server", "client"]);

        var copyActions = template.Actions.Where(a => a.Action == "copy" && a.To == "MonsterInfo").ToList();
        copyActions.Count.ShouldBe(1);
        copyActions[0].From!.Env.ShouldBe("server");

        // No merge action for a single-env table
        template.Actions.ShouldNotContain(a => a.Action == "merge" && a.Into == "MonsterInfo");
    }

    [Fact]
    public void MultiEnvTable_GeneratesCopyAndMerge()
    {
        var envTables = new Dictionary<(string table, string env), TableFile>
        {
            [("ItemInfo", "server")] = MakeTable("ItemInfo",
                [Col("ID", ColumnType.UInt16), Col("InxName", ColumnType.String, 64), Col("Name", ColumnType.String, 64)]),
            [("ItemInfo", "client")] = MakeTable("ItemInfo",
                [Col("ID", ColumnType.UInt16), Col("InxName", ColumnType.String, 64), Col("ViewCol", ColumnType.String, 32)])
        };

        var template = TemplateGenerator.Generate(envTables, ["server", "client"]);

        var copyActions = template.Actions.Where(a => a.Action == "copy" && a.To == "ItemInfo").ToList();
        copyActions.Count.ShouldBe(1);
        copyActions[0].From!.Env.ShouldBe("server"); // first env

        var mergeActions = template.Actions.Where(a => a.Action == "merge" && a.Into == "ItemInfo").ToList();
        mergeActions.Count.ShouldBe(1);
        mergeActions[0].From!.Env.ShouldBe("client");
        mergeActions[0].On.ShouldNotBeNull();
    }

    [Fact]
    public void JoinKeyHeuristic_PrefersID()
    {
        var envTables = new Dictionary<(string table, string env), TableFile>
        {
            [("Items", "server")] = MakeTable("Items",
                [Col("ID", ColumnType.UInt16), Col("Name", ColumnType.String, 64)]),
            [("Items", "client")] = MakeTable("Items",
                [Col("ID", ColumnType.UInt16), Col("Name", ColumnType.String, 64)])
        };

        var template = TemplateGenerator.Generate(envTables, ["server", "client"]);

        var mergeAction = template.Actions.First(a => a.Action == "merge" && a.Into == "Items");
        mergeAction.On!.Source.ShouldBe("ID");
        mergeAction.On.Target.ShouldBe("ID");
    }

    [Fact]
    public void JoinKeyHeuristic_FallsBackToInxName()
    {
        var envTables = new Dictionary<(string table, string env), TableFile>
        {
            [("Skills", "server")] = MakeTable("Skills",
                [Col("InxName", ColumnType.String, 64), Col("Level", ColumnType.UInt16)]),
            [("Skills", "client")] = MakeTable("Skills",
                [Col("InxName", ColumnType.String, 64), Col("Level", ColumnType.UInt16)])
        };

        var template = TemplateGenerator.Generate(envTables, ["server", "client"]);

        var mergeAction = template.Actions.First(a => a.Action == "merge" && a.Into == "Skills");
        mergeAction.On!.Source.ShouldBe("InxName");
        mergeAction.On.Target.ShouldBe("InxName");
    }

    [Fact]
    public void JoinKeyHeuristic_FallsBackToFirstUIntColumn()
    {
        var envTables = new Dictionary<(string table, string env), TableFile>
        {
            [("Custom", "server")] = MakeTable("Custom",
                [Col("Name", ColumnType.String, 64), Col("Index", ColumnType.UInt32), Col("Level", ColumnType.UInt16)]),
            [("Custom", "client")] = MakeTable("Custom",
                [Col("Name", ColumnType.String, 64), Col("Index", ColumnType.UInt32), Col("Level", ColumnType.UInt16)])
        };

        var template = TemplateGenerator.Generate(envTables, ["server", "client"]);

        var mergeAction = template.Actions.First(a => a.Action == "merge" && a.Into == "Custom");
        // Should pick first UInt column that exists in both: "Index"
        mergeAction.On!.Source.ShouldBe("Index");
    }

    [Fact]
    public void GeneratesPrimaryKeyForIDColumn()
    {
        var envTables = new Dictionary<(string table, string env), TableFile>
        {
            [("Items", "server")] = MakeTable("Items",
                [Col("ID", ColumnType.UInt16), Col("Name", ColumnType.String, 64)])
        };

        var template = TemplateGenerator.Generate(envTables, ["server"]);

        var pkAction = template.Actions.FirstOrDefault(a =>
            a.Action == "setPrimaryKey" && a.Table == "Items");
        pkAction.ShouldNotBeNull();
        pkAction!.Column.ShouldBe("ID");
    }

    [Fact]
    public void GeneratesUniqueKeyForInxName()
    {
        var envTables = new Dictionary<(string table, string env), TableFile>
        {
            [("Items", "server")] = MakeTable("Items",
                [Col("ID", ColumnType.UInt16), Col("InxName", ColumnType.String, 64)])
        };

        var template = TemplateGenerator.Generate(envTables, ["server"]);

        var ukAction = template.Actions.FirstOrDefault(a =>
            a.Action == "setUniqueKey" && a.Table == "Items");
        ukAction.ShouldNotBeNull();
        ukAction!.Column.ShouldBe("InxName");
    }
}
