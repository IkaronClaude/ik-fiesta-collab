using Mimir.Core.Models;
using Shouldly;
using Xunit;

namespace Mimir.Core.Tests;

public class TableComparerTests
{
    private static TableFile MakeFile(string name, IReadOnlyList<ColumnDefinition> columns,
        params Dictionary<string, object?>[] rows) => new()
    {
        Header = new TableHeader
        {
            TableName = name,
            SourceFormat = "shn",
            Metadata = null
        },
        Columns = columns,
        Data = rows
    };

    private static readonly IReadOnlyList<ColumnDefinition> TestColumns =
    [
        new() { Name = "ID", Type = ColumnType.UInt32, Length = 4 },
        new() { Name = "Name", Type = ColumnType.String, Length = 64 },
        new() { Name = "Level", Type = ColumnType.UInt16, Length = 2 },
    ];

    [Fact]
    public void IdenticalTables_NoDifference()
    {
        var a = MakeFile("Test", TestColumns,
            new Dictionary<string, object?> { ["ID"] = (uint)1, ["Name"] = "Sword", ["Level"] = (ushort)10 });
        var b = MakeFile("Test", TestColumns,
            new Dictionary<string, object?> { ["ID"] = (uint)1, ["Name"] = "Sword", ["Level"] = (ushort)10 });

        TableComparer.FindDifference(a, b).ShouldBeNull();
    }

    [Fact]
    public void DifferentRowCount_Detected()
    {
        var a = MakeFile("Test", TestColumns,
            new Dictionary<string, object?> { ["ID"] = (uint)1, ["Name"] = "Sword", ["Level"] = (ushort)10 });
        var b = MakeFile("Test", TestColumns);

        var diff = TableComparer.FindDifference(a, b);
        diff.ShouldNotBeNull();
        diff.ShouldContain("Row count");
    }

    [Fact]
    public void DifferentColumnCount_Detected()
    {
        var cols2 = TestColumns.Take(2).ToList();
        var a = MakeFile("Test", TestColumns);
        var b = MakeFile("Test", cols2);

        var diff = TableComparer.FindDifference(a, b);
        diff.ShouldNotBeNull();
        diff.ShouldContain("Column count");
    }

    [Fact]
    public void DifferentData_Detected()
    {
        var a = MakeFile("Test", TestColumns,
            new Dictionary<string, object?> { ["ID"] = (uint)1, ["Name"] = "Sword", ["Level"] = (ushort)10 });
        var b = MakeFile("Test", TestColumns,
            new Dictionary<string, object?> { ["ID"] = (uint)1, ["Name"] = "Shield", ["Level"] = (ushort)10 });

        var diff = TableComparer.FindDifference(a, b);
        diff.ShouldNotBeNull();
        diff.ShouldContain("Name");
        diff.ShouldContain("Sword");
        diff.ShouldContain("Shield");
    }

    [Fact]
    public void DifferentColumnType_Detected()
    {
        var cols1 = new List<ColumnDefinition>
        {
            new() { Name = "X", Type = ColumnType.UInt32, Length = 4 }
        };
        var cols2 = new List<ColumnDefinition>
        {
            new() { Name = "X", Type = ColumnType.String, Length = 4 }
        };

        var a = MakeFile("Test", cols1);
        var b = MakeFile("Test", cols2);

        var diff = TableComparer.FindDifference(a, b);
        diff.ShouldNotBeNull();
        diff.ShouldContain("type");
    }

    [Fact]
    public void EmptyTables_Match()
    {
        var a = MakeFile("Test", TestColumns);
        var b = MakeFile("Test", TestColumns);

        TableComparer.FindDifference(a, b).ShouldBeNull();
    }

    [Fact]
    public void NullValues_Match()
    {
        var a = MakeFile("Test", TestColumns,
            new Dictionary<string, object?> { ["ID"] = null, ["Name"] = null, ["Level"] = null });
        var b = MakeFile("Test", TestColumns,
            new Dictionary<string, object?> { ["ID"] = null, ["Name"] = null, ["Level"] = null });

        TableComparer.FindDifference(a, b).ShouldBeNull();
    }
}
