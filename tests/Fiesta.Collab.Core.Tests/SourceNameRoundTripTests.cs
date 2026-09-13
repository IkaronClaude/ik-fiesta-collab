using Fiesta.Collab.Core.Models;
using Fiesta.Collab.Core.Templates;
using Shouldly;
using Xunit;

namespace Fiesta.Collab.Core.Tests;

/// <summary>
/// A column's exact source-file name ("R", " ") must survive the multi-environment merge and the
/// per-environment split, or the built SHN header no longer matches the original byte for byte.
/// </summary>
public class SourceNameRoundTripTests
{
    private static ColumnDefinition Col(string name, string? sourceName = null) =>
        new() { Name = name, Type = ColumnType.UInt32, Length = 4, SourceTypeCode = 3, SourceName = sourceName };

    private static TableFile Table(params ColumnDefinition[] cols) => new()
    {
        Header = new TableHeader { TableName = "T", SourceFormat = "shn" },
        Columns = cols,
        Data = [new Dictionary<string, object?> { ["ID"] = (uint)1, ["Undefined0"] = (uint)7 }]
    };

    [Fact]
    public void Merge_ThenSplit_KeepsSourceName()
    {
        var target = Table(Col("ID"), Col("Undefined0", "R"));
        var source = Table(Col("ID"), Col("Undefined0", "R"));
        var join = new JoinClause { Source = "ID", Target = "ID" };

        var merged = TableMerger.Merge(target, source, join, "server", "auto", "split", "client");
        merged.Table.Columns.Single(c => c.Name == "Undefined0").SourceName.ShouldBe("R");

        var split = TableSplitter.Split(merged.Table, "server", merged.EnvMetadata["server"]);
        split.Columns.Single(c => c.Name == "Undefined0").SourceName.ShouldBe("R");
    }
}
