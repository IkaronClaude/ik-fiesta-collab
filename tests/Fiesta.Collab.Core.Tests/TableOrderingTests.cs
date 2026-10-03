using Fiesta.Collab.Core.Models;
using Shouldly;
using Xunit;

namespace Fiesta.Collab.Core.Tests;

public class TableOrderingTests
{
    private static TableFile T(params long[] ids) => new()
    {
        Header = new TableHeader { TableName = "T", SourceFormat = "shn" },
        Columns = [new ColumnDefinition { Name = "ID", Type = ColumnType.UInt32, Length = 4 }],
        Data = ids.Select(i => new Dictionary<string, object?> { ["ID"] = i }).ToList(),
        RowEnvironments = ids.Select(i => i % 2 == 0 ? new List<string> { "even" } : null).ToList()
    };

    [Fact]
    public void Orders_by_the_column_numerically_and_moves_row_environments_with_their_rows()
    {
        var o = TableOrdering.OrderBy(T(10, 2, 33, 4), "ID");
        o.Data.Select(r => (long)r["ID"]!).ShouldBe([2L, 4L, 10L, 33L]);
        o.RowEnvironments!.Select(e => e is null ? "-" : e[0]).ShouldBe(["even", "even", "even", "-"]);
    }

    [Fact]
    public void Descending_reverses_and_equal_values_keep_their_order()
    {
        var t = new TableFile
        {
            Header = new TableHeader { TableName = "T", SourceFormat = "shn" },
            Columns = [new ColumnDefinition { Name = "ID", Type = ColumnType.UInt32, Length = 4 },
                       new ColumnDefinition { Name = "Tag", Type = ColumnType.String, Length = 4 }],
            Data = [new() { ["ID"] = 1L, ["Tag"] = "a" }, new() { ["ID"] = 5L, ["Tag"] = "b" }, new() { ["ID"] = 1L, ["Tag"] = "c" }]
        };
        TableOrdering.OrderBy(t, "ID", descending: true).Data.Select(r => (string)r["Tag"]!).ShouldBe(["b", "a", "c"]);
    }
}
