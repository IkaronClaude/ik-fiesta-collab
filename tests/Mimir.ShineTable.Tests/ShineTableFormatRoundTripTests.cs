using Mimir.Core.Models;
using Shouldly;
using Xunit;

namespace Mimir.ShineTable.Tests;

public class ShineTableFormatRoundTripTests
{
    private static TableEntry MakeTable(string name, IReadOnlyList<ColumnDefinition> columns,
        params Dictionary<string, object?>[] rows) => new()
    {
        Schema = new TableSchema
        {
            TableName = name,
            SourceFormat = "shinetable",
            Columns = columns,
            Metadata = new Dictionary<string, object>
            {
                ["sourceFile"] = "Test.txt",
                ["tableName"] = name,
                ["format"] = "table"
            }
        },
        Rows = rows
    };

    private static readonly IReadOnlyList<ColumnDefinition> AllTypesColumns =
    [
        new() { Name = "ID", Type = ColumnType.UInt32, Length = 4 },
        new() { Name = "Name", Type = ColumnType.String, Length = 64 },
        new() { Name = "Count", Type = ColumnType.UInt16, Length = 2 },
        new() { Name = "Flag", Type = ColumnType.Byte, Length = 1 },
        new() { Name = "Rate", Type = ColumnType.Float, Length = 4 },
        new() { Name = "Key", Type = ColumnType.String, Length = 32 },
    ];

    [Fact]
    public void RoundTrip_AllColumnTypes()
    {
        var original = MakeTable("TestTable", AllTypesColumns,
            new Dictionary<string, object?>
            {
                ["ID"] = (uint)1,
                ["Name"] = "Sword of Testing",
                ["Count"] = (ushort)10,
                ["Flag"] = (byte)1,
                ["Rate"] = 1.5f,
                ["Key"] = "SwordTest"
            },
            new Dictionary<string, object?>
            {
                ["ID"] = (uint)999,
                ["Name"] = "Shield",
                ["Count"] = (ushort)0,
                ["Flag"] = (byte)0,
                ["Rate"] = 0f,
                ["Key"] = "-"
            });

        var lines = ShineTableFormatParser.Write([original]);
        var parsed = ShineTableFormatParser.Parse("Test.txt", lines.ToArray());

        parsed.Count.ShouldBe(1);
        var result = parsed[0];

        result.Schema.TableName.ShouldBe("Test_TestTable");
        result.Rows.Count.ShouldBe(2);

        // Row 0
        result.Rows[0]["ID"].ShouldBe((uint)1);
        result.Rows[0]["Name"].ShouldBe("Sword of Testing");
        result.Rows[0]["Count"].ShouldBe((ushort)10);
        result.Rows[0]["Flag"].ShouldBe((byte)1);
        Convert.ToSingle(result.Rows[0]["Rate"]).ShouldBe(1.5f);
        result.Rows[0]["Key"].ShouldBe("SwordTest");

        // Row 1 - verify zeros and dash
        result.Rows[1]["ID"].ShouldBe((uint)999);
        result.Rows[1]["Name"].ShouldBe("Shield");
        result.Rows[1]["Count"].ShouldBe((ushort)0);
        result.Rows[1]["Flag"].ShouldBe((byte)0);
    }

    [Fact]
    public void RoundTrip_NullValues_BecomeDefaults()
    {
        var original = MakeTable("NullTest", AllTypesColumns,
            new Dictionary<string, object?>
            {
                ["ID"] = null,
                ["Name"] = null,
                ["Count"] = null,
                ["Flag"] = null,
                ["Rate"] = null,
                ["Key"] = null
            });

        var lines = ShineTableFormatParser.Write([original]);
        var parsed = ShineTableFormatParser.Parse("Test.txt", lines.ToArray());

        parsed.Count.ShouldBe(1);
        var row = parsed[0].Rows[0];

        // Null numerics write as "0", null strings write as "-"
        row["ID"].ShouldBe((uint)0);
        row["Name"].ShouldBe("-");
        row["Count"].ShouldBe((ushort)0);
        row["Flag"].ShouldBe((byte)0);
    }

    [Fact]
    public void RoundTrip_MultipleTablesInOneFile()
    {
        var table1 = MakeTable("MobRegen", AllTypesColumns.Take(3).ToList(),
            new Dictionary<string, object?>
            {
                ["ID"] = (uint)100,
                ["Name"] = "Slime",
                ["Count"] = (ushort)5
            });

        var table2 = MakeTable("MobGroup", AllTypesColumns.Take(3).ToList(),
            new Dictionary<string, object?>
            {
                ["ID"] = (uint)200,
                ["Name"] = "Group1",
                ["Count"] = (ushort)3
            });

        var lines = ShineTableFormatParser.Write([table1, table2]);
        var parsed = ShineTableFormatParser.Parse("Test.txt", lines.ToArray());

        parsed.Count.ShouldBe(2);
        parsed[0].Schema.TableName.ShouldBe("Test_MobRegen");
        parsed[1].Schema.TableName.ShouldBe("Test_MobGroup");
        parsed[0].Rows[0]["Name"].ShouldBe("Slime");
        parsed[1].Rows[0]["Name"].ShouldBe("Group1");
    }

    [Fact]
    public void RoundTrip_EmptyTable()
    {
        var original = MakeTable("Empty", AllTypesColumns.Take(2).ToList());

        var lines = ShineTableFormatParser.Write([original]);
        var parsed = ShineTableFormatParser.Parse("Test.txt", lines.ToArray());

        // Empty tables must round-trip - server exes may expect the table to exist
        parsed.Count.ShouldBe(1);
        parsed[0].Rows.Count.ShouldBe(0);
        parsed[0].Schema.Columns.Count.ShouldBe(2);
    }

    [Fact]
    public void RoundTrip_LargeUInt32Values()
    {
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "Flags", Type = ColumnType.UInt32, Length = 4 }
        };

        var original = MakeTable("FlagTest", columns,
            new Dictionary<string, object?>
            {
                ["Flags"] = uint.MaxValue // 4294967295
            });

        var lines = ShineTableFormatParser.Write([original]);
        var parsed = ShineTableFormatParser.Parse("Test.txt", lines.ToArray());

        parsed[0].Rows[0]["Flags"].ShouldBe(uint.MaxValue);
    }

    [Fact]
    public void Write_ProducesCorrectFormat()
    {
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "ID", Type = ColumnType.UInt32, Length = 4 },
            new() { Name = "Name", Type = ColumnType.String, Length = 64 },
        };

        var table = MakeTable("Items", columns,
            new Dictionary<string, object?>
            {
                ["ID"] = (uint)42,
                ["Name"] = "TestItem"
            });

        var lines = ShineTableFormatParser.Write([table]);

        lines[0].ShouldBe("#table\tItems");
        lines[1].ShouldBe("#columntype\tDWRD\tSTRING[64]");
        lines[2].ShouldBe("#columnname\tID\tName");
        lines[3].ShouldBe("#record\t42\tTestItem");
    }

    [Fact]
    public void Write_EndsWithEndTag()
    {
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "ID", Type = ColumnType.UInt32, Length = 4 },
        };

        var table = MakeTable("Test", columns,
            new Dictionary<string, object?> { ["ID"] = (uint)1 });

        var lines = ShineTableFormatParser.Write([table]);

        lines[^1].ShouldBe("#End");
    }

    [Fact]
    public void Parse_HandlesEndTag_InWrittenOutput()
    {
        // Write produces #End; re-parsing that output must not lose any tables
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "ID", Type = ColumnType.UInt32, Length = 4 },
            new() { Name = "Name", Type = ColumnType.String, Length = 32 },
        };

        var table = MakeTable("Items", columns,
            new Dictionary<string, object?> { ["ID"] = (uint)5, ["Name"] = "Sword" });

        var lines = ShineTableFormatParser.Write([table]);
        // Confirm #End is present
        lines.ShouldContain("#End");

        var parsed = ShineTableFormatParser.Parse("Test.txt", lines.ToArray());
        parsed.Count.ShouldBe(1);
        parsed[0].Rows.Count.ShouldBe(1);
        parsed[0].Rows[0]["ID"].ShouldBe((uint)5);
    }
}
