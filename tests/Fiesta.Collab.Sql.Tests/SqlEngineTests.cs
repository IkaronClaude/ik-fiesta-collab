using Microsoft.Extensions.Logging.Abstractions;
using Fiesta.Collab.Core.Models;
using Shouldly;
using Xunit;

namespace Fiesta.Collab.Sql.Tests;

public class SqlEngineTests : IDisposable
{
    private readonly SqlEngine _engine = new(NullLogger<SqlEngine>.Instance);

    public void Dispose() => _engine.Dispose();

    private static TableEntry MakeTable(string name, IReadOnlyList<ColumnDefinition> columns,
        params Dictionary<string, object?>[] rows) => new()
    {
        Schema = new TableSchema
        {
            TableName = name,
            SourceFormat = "test",
            Columns = columns,
            Metadata = null
        },
        Rows = rows
    };

    private static readonly IReadOnlyList<ColumnDefinition> ItemColumns =
    [
        new() { Name = "ID", Type = ColumnType.UInt32, Length = 4 },
        new() { Name = "Name", Type = ColumnType.String, Length = 64 },
        new() { Name = "Level", Type = ColumnType.UInt16, Length = 2 },
        new() { Name = "AC", Type = ColumnType.UInt32, Length = 4 },
        new() { Name = "Rate", Type = ColumnType.Float, Length = 4 },
    ];

    [Fact]
    public void LoadAndQuery_BasicTypes()
    {
        var table = MakeTable("Items", ItemColumns,
            new Dictionary<string, object?>
            {
                ["ID"] = (uint)1, ["Name"] = "Sword", ["Level"] = (ushort)10,
                ["AC"] = (uint)50, ["Rate"] = 1.5f
            },
            new Dictionary<string, object?>
            {
                ["ID"] = (uint)2, ["Name"] = "Shield", ["Level"] = (ushort)5,
                ["AC"] = (uint)100, ["Rate"] = 0.8f
            });

        _engine.LoadTable(table);

        var results = _engine.Query("SELECT * FROM Items WHERE AC > 60");
        results.Count.ShouldBe(1);
        results[0]["Name"].ShouldBe("Shield");
    }

    [Fact]
    public void LoadAndExtract_PreservesTypes()
    {
        var table = MakeTable("Items", ItemColumns,
            new Dictionary<string, object?>
            {
                ["ID"] = (uint)42, ["Name"] = "TestItem", ["Level"] = (ushort)99,
                ["AC"] = (uint)1337, ["Rate"] = 2.5f
            });

        _engine.LoadTable(table);
        var extracted = _engine.ExtractTable(table.Schema);

        extracted.Rows.Count.ShouldBe(1);
        var row = extracted.Rows[0];
        row["ID"].ShouldBe((uint)42);
        row["Name"].ShouldBe("TestItem");
        row["Level"].ShouldBe((ushort)99);
        row["AC"].ShouldBe((uint)1337);
        row["Rate"].ShouldBe(2.5f);
    }

    [Fact]
    public void LoadAndExtract_UInt32MaxValue()
    {
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "Flags", Type = ColumnType.UInt32, Length = 4 }
        };

        var table = MakeTable("FlagTest", columns,
            new Dictionary<string, object?> { ["Flags"] = uint.MaxValue });

        _engine.LoadTable(table);
        var extracted = _engine.ExtractTable(table.Schema);

        extracted.Rows[0]["Flags"].ShouldBe(uint.MaxValue);
    }

    [Fact]
    public void LoadAndExtract_UInt64Values()
    {
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "BigFlags", Type = ColumnType.UInt64, Length = 8 }
        };

        var table = MakeTable("BigTest", columns,
            new Dictionary<string, object?> { ["BigFlags"] = (ulong)0xFFFF_FFFF_FFFF_FFFF });

        _engine.LoadTable(table);
        var extracted = _engine.ExtractTable(table.Schema);

        extracted.Rows[0]["BigFlags"].ShouldBe(ulong.MaxValue);
    }

    [Fact]
    public void EditAndExtract_UpdateReflected()
    {
        var table = MakeTable("Items", ItemColumns,
            new Dictionary<string, object?>
            {
                ["ID"] = (uint)1, ["Name"] = "Sword", ["Level"] = (ushort)10,
                ["AC"] = (uint)50, ["Rate"] = 1.5f
            });

        _engine.LoadTable(table);
        _engine.Execute("UPDATE Items SET AC = 9999 WHERE ID = 1");

        var extracted = _engine.ExtractTable(table.Schema);
        extracted.Rows[0]["AC"].ShouldBe((uint)9999);
    }

    [Fact]
    public void EditAndExtract_InsertNewRow()
    {
        var table = MakeTable("Items", ItemColumns,
            new Dictionary<string, object?>
            {
                ["ID"] = (uint)1, ["Name"] = "Sword", ["Level"] = (ushort)10,
                ["AC"] = (uint)50, ["Rate"] = 1.5f
            });

        _engine.LoadTable(table);
        _engine.Execute("INSERT INTO Items (ID, Name, Level, AC, Rate) VALUES (99, 'NewItem', 1, 0, 0)");

        var extracted = _engine.ExtractTable(table.Schema);
        extracted.Rows.Count.ShouldBe(2);
        extracted.Rows[1]["Name"].ShouldBe("NewItem");
    }

    [Fact]
    public void LoadAndExtract_NullValues()
    {
        var table = MakeTable("NullTest", ItemColumns,
            new Dictionary<string, object?>
            {
                ["ID"] = (uint)1, ["Name"] = null, ["Level"] = null,
                ["AC"] = null, ["Rate"] = null
            });

        _engine.LoadTable(table);
        var extracted = _engine.ExtractTable(table.Schema);

        var row = extracted.Rows[0];
        row["ID"].ShouldBe((uint)1);
        row["Name"].ShouldBeNull();
        row["Level"].ShouldBeNull();
        row["AC"].ShouldBeNull();
        row["Rate"].ShouldBeNull();
    }

    [Fact]
    public void ListTables_ReturnsLoadedTables()
    {
        var cols = new List<ColumnDefinition>
        {
            new() { Name = "X", Type = ColumnType.Int32, Length = 4 }
        };

        _engine.LoadTable(MakeTable("Alpha", cols));
        _engine.LoadTable(MakeTable("Beta", cols));

        var tables = _engine.ListTables();
        tables.ShouldContain("Alpha");
        tables.ShouldContain("Beta");
    }

    [Fact]
    public void LoadAndExtract_SignedTypes()
    {
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "S8", Type = ColumnType.SByte, Length = 1 },
            new() { Name = "S16", Type = ColumnType.Int16, Length = 2 },
            new() { Name = "S32", Type = ColumnType.Int32, Length = 4 },
        };

        var table = MakeTable("SignedTest", columns,
            new Dictionary<string, object?>
            {
                ["S8"] = (sbyte)-50,
                ["S16"] = (short)-1000,
                ["S32"] = -99999
            });

        _engine.LoadTable(table);
        var extracted = _engine.ExtractTable(table.Schema);

        extracted.Rows[0]["S8"].ShouldBe((sbyte)-50);
        extracted.Rows[0]["S16"].ShouldBe((short)-1000);
        extracted.Rows[0]["S32"].ShouldBe(-99999);
    }

    /// <summary>
    /// A negative value against an UNSIGNED column survives the round trip.
    ///
    /// The shine .txt tables are written with negatives where the declared type is WORD or BYTE -
    /// ExpRecalculation's ByLevelDiff starts at -150, every NPC.txt facing is one - and the 2016 loader
    /// wraps them itself. Wrapping on the way out of SQLite threw the sign away as permanently as
    /// wrapping on the way in did: -150 came back 65386 and was written back 65386, turning a
    /// level-difference penalty into a huge positive number and re-facing every affected NPC.
    /// Genuinely unsigned values above the signed range must still come back unsigned, so both are here.
    /// </summary>
    [Fact]
    public void LoadAndExtract_NegativeAgainstUnsignedColumn_KeepsItsSign()
    {
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "LevelDiff", Type = ColumnType.UInt16, Length = 2 },
            new() { Name = "Facing", Type = ColumnType.UInt16, Length = 2 },
            new() { Name = "Flag", Type = ColumnType.Byte, Length = 1 },
            new() { Name = "Big", Type = ColumnType.UInt32, Length = 4 },
        };

        var table = MakeTable("SignedAgainstUnsigned", columns,
            new Dictionary<string, object?>
            {
                ["LevelDiff"] = (short)-150,
                ["Facing"] = (ushort)65474,
                ["Flag"] = (sbyte)-8,
                ["Big"] = uint.MaxValue
            });

        _engine.LoadTable(table);
        var extracted = _engine.ExtractTable(table.Schema);

        Convert.ToInt64(extracted.Rows[0]["LevelDiff"]).ShouldBe(-150);
        Convert.ToInt64(extracted.Rows[0]["Facing"]).ShouldBe(65474);
        Convert.ToInt64(extracted.Rows[0]["Flag"]).ShouldBe(-8);
        Convert.ToInt64(extracted.Rows[0]["Big"]).ShouldBe(uint.MaxValue);
    }
}
