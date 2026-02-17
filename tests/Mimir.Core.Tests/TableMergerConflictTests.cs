using Mimir.Core.Models;
using Mimir.Core.Templates;
using Shouldly;
using Xunit;

namespace Mimir.Core.Tests;

public class TableMergerConflictTests
{
    private static ColumnDefinition Col(string name, ColumnType type, int length = 4, int? stc = null) =>
        new() { Name = name, Type = type, Length = length, SourceTypeCode = stc };

    private static TableFile MakeTable(string name, IReadOnlyList<ColumnDefinition> columns,
        params Dictionary<string, object?>[] rows) => new()
    {
        Header = new TableHeader { TableName = name, SourceFormat = "shn" },
        Columns = columns,
        Data = rows
    };

    private static Dictionary<string, object?> Row(params (string key, object? val)[] pairs) =>
        pairs.ToDictionary(p => p.key, p => p.val);

    // --- Helper to build a ColorInfo-like table ---
    private static (TableFile target, TableFile source, JoinClause join) MakeColorInfoScenario()
    {
        var target = MakeTable("ColorInfo",
            [Col("ID", ColumnType.UInt32), Col("ColorR", ColumnType.UInt16, 2), Col("ColorG", ColumnType.UInt16, 2), Col("ColorB", ColumnType.UInt16, 2)],
            Row(("ID", (object?)(uint)1), ("ColorR", (ushort)255), ("ColorG", (ushort)0), ("ColorB", (ushort)0)),
            Row(("ID", (uint)2), ("ColorR", (ushort)0), ("ColorG", (ushort)255), ("ColorB", (ushort)0)));

        var source = MakeTable("ColorInfo",
            [Col("ID", ColumnType.UInt32), Col("ColorR", ColumnType.UInt16, 2), Col("ColorG", ColumnType.UInt16, 2), Col("ColorB", ColumnType.UInt16, 2)],
            Row(("ID", (object?)(uint)1), ("ColorR", (ushort)200), ("ColorG", (ushort)50), ("ColorB", (ushort)50)),
            Row(("ID", (uint)2), ("ColorR", (ushort)10), ("ColorG", (ushort)200), ("ColorB", (ushort)10)));

        var join = new JoinClause { Source = "ID", Target = "ID" };
        return (target, source, join);
    }

    [Fact]
    public void Merge_ValueConflict_ReportStrategy_NoSplitColumn()
    {
        var (target, source, join) = MakeColorInfoScenario();

        var result = TableMerger.Merge(target, source, join, "client", "auto", "report");

        // Conflicts should be reported
        result.Conflicts.ShouldNotBeEmpty();

        // No split columns should exist
        result.Table.Columns.ShouldNotContain(c => c.Name.Contains("__"));

        // Target values kept
        var row1 = result.Table.Data.First(r => r["ID"]?.ToString() == "1");
        row1["ColorR"]?.ToString().ShouldBe("255");
    }

    [Fact]
    public void Merge_ValueConflict_SplitStrategy_CreatesSplitColumn()
    {
        var (target, source, join) = MakeColorInfoScenario();

        var result = TableMerger.Merge(target, source, join, "client", "auto", "split");

        // Split columns should exist for all conflicting columns
        result.Table.Columns.ShouldContain(c => c.Name == "ColorR__client");
        result.Table.Columns.ShouldContain(c => c.Name == "ColorG__client");
        result.Table.Columns.ShouldContain(c => c.Name == "ColorB__client");

        // Split columns should have env annotation
        var splitCol = result.Table.Columns.First(c => c.Name == "ColorR__client");
        splitCol.Environments.ShouldNotBeNull();
        splitCol.Environments.ShouldContain("client");

        // Split column should have same type/length as source column
        splitCol.Type.ShouldBe(ColumnType.UInt16);
        splitCol.Length.ShouldBe(2);
    }

    [Fact]
    public void Merge_ValueConflict_SplitStrategy_SplitColumnHasSourceValues()
    {
        var (target, source, join) = MakeColorInfoScenario();

        var result = TableMerger.Merge(target, source, join, "client", "auto", "split");

        // Row 1: target has 255, source (client) has 200
        var row1 = result.Table.Data.First(r => r["ID"]?.ToString() == "1");
        row1["ColorR"]?.ToString().ShouldBe("255");          // target value preserved
        row1["ColorR__client"]?.ToString().ShouldBe("200");   // source value in split column

        // Row 2: target has 0, source (client) has 10
        var row2 = result.Table.Data.First(r => r["ID"]?.ToString() == "2");
        row2["ColorR"]?.ToString().ShouldBe("0");
        row2["ColorR__client"]?.ToString().ShouldBe("10");

        // Check G and B too
        row1["ColorG__client"]?.ToString().ShouldBe("50");
        row1["ColorB__client"]?.ToString().ShouldBe("50");
    }

    [Fact]
    public void Merge_ValueConflict_SplitStrategy_SplitColumnInRenames()
    {
        var (target, source, join) = MakeColorInfoScenario();

        var result = TableMerger.Merge(target, source, join, "client", "auto", "split");

        // ColumnRenames should map split name → original name
        var meta = result.EnvMetadata["client"];
        meta.ColumnRenames.ShouldContainKey("ColorR__client");
        meta.ColumnRenames["ColorR__client"].ShouldBe("ColorR");
        meta.ColumnRenames.ShouldContainKey("ColorG__client");
        meta.ColumnRenames["ColorG__client"].ShouldBe("ColorG");
        meta.ColumnRenames.ShouldContainKey("ColorB__client");
        meta.ColumnRenames["ColorB__client"].ShouldBe("ColorB");
    }

    [Fact]
    public void Merge_ValueConflict_SplitStrategy_ColumnOrderUsesSplitName()
    {
        var (target, source, join) = MakeColorInfoScenario();

        var result = TableMerger.Merge(target, source, join, "client", "auto", "split");

        // Source env's column order should reference split names for conflict columns
        var meta = result.EnvMetadata["client"];
        meta.ColumnOrder.ShouldContain("ColorR__client");
        meta.ColumnOrder.ShouldContain("ColorG__client");
        meta.ColumnOrder.ShouldContain("ColorB__client");
        meta.ColumnOrder.ShouldNotContain("ColorR");
        meta.ColumnOrder.ShouldNotContain("ColorG");
        meta.ColumnOrder.ShouldNotContain("ColorB");

        // ID should still be in column order (it's shared, not conflicting)
        meta.ColumnOrder.ShouldContain("ID");
    }

    [Fact]
    public void Merge_NoConflict_SplitStrategy_NoSplitColumn()
    {
        // Same values in both envs — "split" strategy should NOT create split columns
        var target = MakeTable("Colors",
            [Col("ID", ColumnType.UInt32), Col("ColorR", ColumnType.UInt16, 2)],
            Row(("ID", (object?)(uint)1), ("ColorR", (ushort)255)));

        var source = MakeTable("Colors",
            [Col("ID", ColumnType.UInt32), Col("ColorR", ColumnType.UInt16, 2)],
            Row(("ID", (object?)(uint)1), ("ColorR", (ushort)255)));

        var join = new JoinClause { Source = "ID", Target = "ID" };
        var result = TableMerger.Merge(target, source, join, "client", "auto", "split");

        // No conflicts, no split columns
        result.Conflicts.ShouldBeEmpty();
        result.Table.Columns.ShouldNotContain(c => c.Name.Contains("__"));
    }

    [Fact]
    public void Merge_ValueConflict_SplitStrategy_RoundtripViaSplitter()
    {
        var (target, source, join) = MakeColorInfoScenario();

        var result = TableMerger.Merge(target, source, join, "client", "auto", "split");

        // Build the server env's column order (the target/base env)
        // Server sees: ID, ColorR, ColorG, ColorB (original columns, no splits)
        var serverMeta = new EnvMergeMetadata
        {
            ColumnOrder = ["ID", "ColorR", "ColorG", "ColorB"],
            ColumnOverrides = new(),
            ColumnRenames = new()
        };

        var clientMeta = result.EnvMetadata["client"];

        // Split for server
        var serverTable = TableSplitter.Split(result.Table, "server", serverMeta);
        serverTable.Data.Count.ShouldBe(2);
        serverTable.Columns.Select(c => c.Name).ShouldBe(["ID", "ColorR", "ColorG", "ColorB"]);

        // Server should have target (server) values
        var serverRow1 = serverTable.Data.First(r => r["ID"]?.ToString() == "1");
        serverRow1["ColorR"]?.ToString().ShouldBe("255");
        serverRow1["ColorG"]?.ToString().ShouldBe("0");
        serverRow1["ColorB"]?.ToString().ShouldBe("0");

        // Split for client
        var clientTable = TableSplitter.Split(result.Table, "client", clientMeta);
        clientTable.Data.Count.ShouldBe(2);
        clientTable.Columns.Select(c => c.Name).ShouldBe(["ID", "ColorR", "ColorG", "ColorB"]);

        // Client should have source (client) values
        var clientRow1 = clientTable.Data.First(r => r["ID"]?.ToString() == "1");
        clientRow1["ColorR"]?.ToString().ShouldBe("200");
        clientRow1["ColorG"]?.ToString().ShouldBe("50");
        clientRow1["ColorB"]?.ToString().ShouldBe("50");

        var clientRow2 = clientTable.Data.First(r => r["ID"]?.ToString() == "2");
        clientRow2["ColorR"]?.ToString().ShouldBe("10");
        clientRow2["ColorG"]?.ToString().ShouldBe("200");
        clientRow2["ColorB"]?.ToString().ShouldBe("10");
    }
}
