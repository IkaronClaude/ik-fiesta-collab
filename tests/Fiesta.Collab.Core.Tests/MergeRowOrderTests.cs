using Fiesta.Collab.Core.Models;
using Fiesta.Collab.Core.Templates;
using Shouldly;
using Xunit;

namespace Fiesta.Collab.Core.Tests;

/// <summary>merge rowOrder "source": the source's rows in the source's order, then the target-only rows.</summary>
public class MergeRowOrderTests
{
    private static TableFile Rows(params (long id, string name)[] rows) => new()
    {
        Header = new TableHeader { TableName = "T", SourceFormat = "shn" },
        Columns =
        [
            new ColumnDefinition { Name = "ID", Type = ColumnType.UInt32, Length = 4 },
            new ColumnDefinition { Name = "Name", Type = ColumnType.String, Length = 16 },
        ],
        Data = rows.Select(r => new Dictionary<string, object?> { ["ID"] = r.id, ["Name"] = r.name }).ToList()
    };

    private static readonly JoinClause On = new() { Source = "ID", Target = "ID" };

    [Fact]
    public void Default_keeps_the_target_order_and_appends_source_only_rows()
    {
        var target = Rows((1, "a"), (2, "b"), (3, "c"));
        var source = Rows((3, "c"), (9, "new"), (1, "a"));
        var merged = TableMerger.Merge(target, source, On, "overlay", "auto", "split", "client");
        merged.Table.Data.Select(r => (long)r["ID"]!).ShouldBe([1L, 2L, 3L, 9L]);
    }

    [Fact]
    public void Source_order_puts_every_source_row_in_source_order_then_the_target_only_rows()
    {
        var target = Rows((1, "a"), (2, "only-target"), (3, "c"), (4, "only-target-too"));
        var source = Rows((3, "c"), (9, "new"), (1, "a"));
        var merged = TableMerger.Merge(target, source, On, "overlay", "auto", "split", "client", orderBySource: true);
        merged.Table.Data.Select(r => (long)r["ID"]!).ShouldBe([3L, 9L, 1L, 2L, 4L]);
        // environments travel with their rows: the source-only row is the overlay's, the target-only ones the client's
        merged.Table.RowEnvironments!.Select(e => e is null ? "*" : string.Join(",", e))
            .ShouldBe(["*", "overlay", "*", "client", "client"]);   // a match with no env list known = shared
    }
}
