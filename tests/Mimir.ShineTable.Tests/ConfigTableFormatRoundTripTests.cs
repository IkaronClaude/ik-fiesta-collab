using Mimir.Core.Models;
using Shouldly;
using Xunit;

namespace Mimir.ShineTable.Tests;

public class ConfigTableFormatRoundTripTests
{
    private static TableEntry MakeDefineTable(string typeName, IReadOnlyList<ColumnDefinition> columns,
        params Dictionary<string, object?>[] rows) => new()
    {
        Schema = new TableSchema
        {
            TableName = $"Config_{typeName}",
            SourceFormat = "configtable",
            Columns = columns,
            Metadata = new Dictionary<string, object>
            {
                ["sourceFile"] = "Config.txt",
                ["typeName"] = typeName,
                ["format"] = "define"
            }
        },
        Rows = rows
    };

    private static readonly IReadOnlyList<ColumnDefinition> ServerColumns =
    [
        new() { Name = "ServerName", Type = ColumnType.String, Length = 256 },
        new() { Name = "Port", Type = ColumnType.Int32, Length = 4 },
        new() { Name = "MaxPlayers", Type = ColumnType.Int32, Length = 4 },
        new() { Name = "Rate", Type = ColumnType.Float, Length = 4 },
    ];

    [Fact]
    public void RoundTrip_DefineFormat()
    {
        var original = MakeDefineTable("SERVER_INFO", ServerColumns,
            new Dictionary<string, object?>
            {
                ["ServerName"] = "TestServer",
                ["Port"] = 9010,
                ["MaxPlayers"] = 500,
                ["Rate"] = 1.5f
            },
            new Dictionary<string, object?>
            {
                ["ServerName"] = "LoginServer",
                ["Port"] = 9001,
                ["MaxPlayers"] = 100,
                ["Rate"] = 1.0f
            });

        var lines = ConfigTableFormatParser.Write([original]);
        var parsed = ConfigTableFormatParser.Parse("Config.txt", lines.ToArray());

        parsed.Count.ShouldBe(1);
        var result = parsed[0];
        result.Rows.Count.ShouldBe(2);

        result.Rows[0]["ServerName"].ShouldBe("TestServer");
        result.Rows[0]["Port"].ShouldBe(9010);
        result.Rows[0]["MaxPlayers"].ShouldBe(500);
        Convert.ToSingle(result.Rows[0]["Rate"]).ShouldBe(1.5f);

        result.Rows[1]["ServerName"].ShouldBe("LoginServer");
        result.Rows[1]["Port"].ShouldBe(9001);
    }

    [Fact]
    public void RoundTrip_MultipleDefineTypes()
    {
        var charColumns = new List<ColumnDefinition>
        {
            new() { Name = "ClassName", Type = ColumnType.String, Length = 256 },
            new() { Name = "StartLevel", Type = ColumnType.Int32, Length = 4 },
        };

        var itemColumns = new List<ColumnDefinition>
        {
            new() { Name = "ItemIndex", Type = ColumnType.String, Length = 256 },
            new() { Name = "Count", Type = ColumnType.Int32, Length = 4 },
        };

        var table1 = MakeDefineTable("CHARACTER", charColumns,
            new Dictionary<string, object?> { ["ClassName"] = "Fighter", ["StartLevel"] = 1 });

        var table2 = MakeDefineTable("ITEM", itemColumns,
            new Dictionary<string, object?> { ["ItemIndex"] = "Sword01", ["Count"] = 1 },
            new Dictionary<string, object?> { ["ItemIndex"] = "Shield01", ["Count"] = 1 });

        var lines = ConfigTableFormatParser.Write([table1, table2]);
        var parsed = ConfigTableFormatParser.Parse("Config.txt", lines.ToArray());

        parsed.Count.ShouldBe(2);
        parsed[0].Rows[0]["ClassName"].ShouldBe("Fighter");
        parsed[1].Rows.Count.ShouldBe(2);
        parsed[1].Rows[0]["ItemIndex"].ShouldBe("Sword01");
    }

    [Fact]
    public void RoundTrip_EmptyStringValues()
    {
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "Value", Type = ColumnType.String, Length = 256 },
            new() { Name = "Num", Type = ColumnType.Int32, Length = 4 },
        };

        var original = MakeDefineTable("TEST", columns,
            new Dictionary<string, object?>
            {
                ["Value"] = null,
                ["Num"] = null
            });

        var lines = ConfigTableFormatParser.Write([original]);
        var parsed = ConfigTableFormatParser.Parse("Config.txt", lines.ToArray());

        parsed.Count.ShouldBe(1);
        // Null string → "" (empty), null int → 0
        parsed[0].Rows[0]["Value"].ShouldBe("");
        parsed[0].Rows[0]["Num"].ShouldBe(0);
    }

    [Fact]
    public void Write_ProducesCorrectDefineFormat()
    {
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "Name", Type = ColumnType.String, Length = 256 },
            new() { Name = "ID", Type = ColumnType.Int32, Length = 4 },
        };

        var table = MakeDefineTable("MY_TYPE", columns,
            new Dictionary<string, object?> { ["Name"] = "Hello", ["ID"] = 42 });

        var lines = ConfigTableFormatParser.Write([table]);

        lines[0].ShouldBe("#DEFINE MY_TYPE");
        lines[1].ShouldContain("<STRING>");
        lines[1].ShouldContain("Name");
        lines[2].ShouldContain("<INTEGER>");
        lines[2].ShouldContain("ID");
        lines[3].ShouldBe("#ENDDEFINE");
        // Line 4 is blank
        lines[5].ShouldStartWith("MY_TYPE ");
        lines[5].ShouldContain("\"Hello\"");
        lines[5].ShouldContain("42");
    }

    [Fact]
    public void RoundTrip_StringWithComma()
    {
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "Desc", Type = ColumnType.String, Length = 256 },
            new() { Name = "Val", Type = ColumnType.Int32, Length = 4 },
        };

        var original = MakeDefineTable("TEST", columns,
            new Dictionary<string, object?>
            {
                ["Desc"] = "Hello, World",
                ["Val"] = 7
            });

        var lines = ConfigTableFormatParser.Write([original]);
        var parsed = ConfigTableFormatParser.Parse("Config.txt", lines.ToArray());

        parsed[0].Rows[0]["Desc"].ShouldBe("Hello, World");
        parsed[0].Rows[0]["Val"].ShouldBe(7);
    }
}
