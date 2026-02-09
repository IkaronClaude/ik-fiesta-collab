using Mimir.Core.Models;
using Shouldly;
using Xunit;

namespace Mimir.Core.Tests;

public class TableSplitterTests
{
    private static ColumnDefinition Col(string name, ColumnType type, int length = 4,
        List<string>? environments = null) =>
        new() { Name = name, Type = type, Length = length, Environments = environments };

    private static Dictionary<string, object?> Row(params (string key, object? val)[] pairs) =>
        pairs.ToDictionary(p => p.key, p => p.val);

    private static TableFile MakeMergedTable(
        IReadOnlyList<ColumnDefinition> columns,
        IReadOnlyList<Dictionary<string, object?>> data,
        IReadOnlyList<List<string>?>? rowEnvironments = null) => new()
    {
        Header = new TableHeader { TableName = "Test", SourceFormat = "shn" },
        Columns = columns,
        Data = data,
        RowEnvironments = rowEnvironments
    };

    [Fact]
    public void SplitByEnv_ExtractsCorrectColumnsAndRows()
    {
        var table = MakeMergedTable(
            [
                Col("ID", ColumnType.UInt32),
                Col("Name", ColumnType.String, 64),
                Col("ClientCol", ColumnType.String, 32, ["client"]),
            ],
            [
                Row(("ID", (object?)(uint)1), ("Name", "Sword"), ("ClientCol", "icon.png")),
                Row(("ID", (uint)2), ("Name", "Shield"), ("ClientCol", null)),
            ],
            [null, null] // both shared
        );

        var envMeta = new EnvMergeMetadata
        {
            ColumnOrder = ["ID", "Name", "ClientCol"],
            ColumnOverrides = new(),
            ColumnRenames = new()
        };

        var result = TableSplitter.Split(table, "client", envMeta);

        result.Columns.Count.ShouldBe(3);
        result.Columns.Select(c => c.Name).ShouldBe(["ID", "Name", "ClientCol"]);
        result.Data.Count.ShouldBe(2);
        result.RowEnvironments.ShouldBeNull(); // non-merged output
    }

    [Fact]
    public void SplitColumnOrder_PreservesPerEnvOrder()
    {
        var table = MakeMergedTable(
            [
                Col("ID", ColumnType.UInt32),
                Col("Name", ColumnType.String, 64),
                Col("Level", ColumnType.UInt16, 2),
            ],
            [Row(("ID", (object?)(uint)1), ("Name", "Sword"), ("Level", (ushort)10))],
            [null]
        );

        // Client sees columns in different order
        var envMeta = new EnvMergeMetadata
        {
            ColumnOrder = ["Level", "ID", "Name"],
            ColumnOverrides = new(),
            ColumnRenames = new()
        };

        var result = TableSplitter.Split(table, "client", envMeta);

        result.Columns.Select(c => c.Name).ShouldBe(["Level", "ID", "Name"]);
    }

    [Fact]
    public void SplitLengthOverride_AppliesColumnLengthOverride()
    {
        var table = MakeMergedTable(
            [Col("ID", ColumnType.UInt32), Col("Name", ColumnType.String, 64)],
            [Row(("ID", (object?)(uint)1), ("Name", "Sword"))],
            [null]
        );

        var envMeta = new EnvMergeMetadata
        {
            ColumnOrder = ["ID", "Name"],
            ColumnOverrides = new() { ["Name"] = new ColumnOverride { Length = 32 } },
            ColumnRenames = new()
        };

        var result = TableSplitter.Split(table, "client", envMeta);

        var nameCol = result.Columns.First(c => c.Name == "Name");
        nameCol.Length.ShouldBe(32);
    }

    [Fact]
    public void SplitColumnRename_RenamesSplitColumnsBack()
    {
        var table = MakeMergedTable(
            [
                Col("ID", ColumnType.UInt32),
                Col("Value", ColumnType.UInt16, 2),        // target's version
                Col("Value__client", ColumnType.String, 32, ["client"]), // client's version
            ],
            [Row(("ID", (object?)(uint)1), ("Value", (ushort)100), ("Value__client", "text"))],
            [null]
        );

        var envMeta = new EnvMergeMetadata
        {
            ColumnOrder = ["ID", "Value__client"],
            ColumnOverrides = new(),
            ColumnRenames = new() { ["Value__client"] = "Value" }
        };

        var result = TableSplitter.Split(table, "client", envMeta);

        // Value__client should be renamed back to Value
        result.Columns.Select(c => c.Name).ShouldBe(["ID", "Value"]);

        // Row data should use the renamed key
        result.Data[0].ShouldContainKey("Value");
        result.Data[0]["Value"].ShouldBe("text");
        result.Data[0].ShouldNotContainKey("Value__client");
    }

    [Fact]
    public void SplitRowFilter_OnlySharedAndEnvSpecificRows()
    {
        var table = MakeMergedTable(
            [Col("ID", ColumnType.UInt32), Col("Name", ColumnType.String, 64)],
            [
                Row(("ID", (object?)(uint)1), ("Name", "Sword")),   // shared
                Row(("ID", (uint)2), ("Name", "Shield")),            // server-only
                Row(("ID", (uint)3), ("Name", "Wand")),              // client-only
            ],
            [null, ["server"], ["client"]]
        );

        var envMeta = new EnvMergeMetadata
        {
            ColumnOrder = ["ID", "Name"],
            ColumnOverrides = new(),
            ColumnRenames = new()
        };

        var serverResult = TableSplitter.Split(table, "server", envMeta);
        serverResult.Data.Count.ShouldBe(2); // shared + server-only
        var serverIds = serverResult.Data.Select(r => (r["ID"] ?? "").ToString()).ToList();
        serverIds.ShouldContain("1");
        serverIds.ShouldContain("2");
        serverIds.ShouldNotContain("3");

        var clientResult = TableSplitter.Split(table, "client", envMeta);
        clientResult.Data.Count.ShouldBe(2); // shared + client-only
        var clientIds = clientResult.Data.Select(r => (r["ID"] ?? "").ToString()).ToList();
        clientIds.ShouldContain("1");
        clientIds.ShouldNotContain("2");
        clientIds.ShouldContain("3");
    }

    [Fact]
    public void SplitNonMergedPassthrough_NullRowEnvironmentsPassesThrough()
    {
        var table = new TableFile
        {
            Header = new TableHeader { TableName = "Simple", SourceFormat = "shn" },
            Columns = [Col("ID", ColumnType.UInt32), Col("Name", ColumnType.String, 64)],
            Data = [Row(("ID", (object?)(uint)1), ("Name", "Sword"))],
            RowEnvironments = null // non-merged table
        };

        // For non-merged tables, Split should return all rows (no filtering)
        var envMeta = new EnvMergeMetadata
        {
            ColumnOrder = ["ID", "Name"],
            ColumnOverrides = new(),
            ColumnRenames = new()
        };

        var result = TableSplitter.Split(table, "server", envMeta);
        result.Data.Count.ShouldBe(1);
        result.RowEnvironments.ShouldBeNull();
    }
}
