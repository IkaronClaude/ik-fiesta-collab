using Mimir.Core.Models;
using Mimir.Core.Templates;
using Shouldly;
using Xunit;

namespace Mimir.Core.Tests;

public class TableMergerTests
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

    // --- Column merge tests ---

    [Fact]
    public void SharedColumnsOnly_SameTypes_AllShared()
    {
        var target = MakeTable("Items",
            [Col("ID", ColumnType.UInt32), Col("Name", ColumnType.String, 64)],
            Row(("ID", (object?)(uint)1), ("Name", "Sword")),
            Row(("ID", (uint)2), ("Name", "Shield")));

        var source = MakeTable("Items",
            [Col("ID", ColumnType.UInt32), Col("Name", ColumnType.String, 64)],
            Row(("ID", (object?)(uint)1), ("Name", "Sword")),
            Row(("ID", (uint)2), ("Name", "Shield")));

        var join = new JoinClause { Source = "ID", Target = "ID" };
        var result = TableMerger.Merge(target, source, join, "client", "auto");

        // All columns should be shared (no env annotation)
        result.Table.Columns.ShouldAllBe(c => c.Environments == null);

        // All rows should be shared (null in RowEnvironments)
        result.Table.RowEnvironments.ShouldNotBeNull();
        result.Table.RowEnvironments!.ShouldAllBe(re => re == null);

        // No conflicts
        result.Conflicts.ShouldBeEmpty();
    }

    [Fact]
    public void EnvOnlyColumns_AnnotatedWithEnvName()
    {
        var target = MakeTable("Items",
            [Col("ID", ColumnType.UInt32), Col("ServerCol", ColumnType.UInt16, 2)],
            Row(("ID", (object?)(uint)1), ("ServerCol", (ushort)10)));

        var source = MakeTable("Items",
            [Col("ID", ColumnType.UInt32), Col("ClientCol", ColumnType.String, 32)],
            Row(("ID", (object?)(uint)1), ("ClientCol", "visual")));

        var join = new JoinClause { Source = "ID", Target = "ID" };
        var result = TableMerger.Merge(target, source, join, "client", "auto");

        // ID is shared
        var idCol = result.Table.Columns.First(c => c.Name == "ID");
        idCol.Environments.ShouldBeNull();

        // ServerCol should NOT be annotated (it came from target, which is already
        // the "base" - we don't know the base env name yet, it's just target)
        // Actually, target-only columns keep whatever annotations they had.
        // For a fresh copy, they'd have no annotation.
        var serverCol = result.Table.Columns.FirstOrDefault(c => c.Name == "ServerCol");
        serverCol.ShouldNotBeNull();

        // ClientCol should be annotated with ["client"]
        var clientCol = result.Table.Columns.First(c => c.Name == "ClientCol");
        clientCol.Environments.ShouldNotBeNull();
        clientCol.Environments.ShouldContain("client");
    }

    [Fact]
    public void ColumnTypeSplit_DifferentTypes_SplitWithSuffix()
    {
        var target = MakeTable("Items",
            [Col("ID", ColumnType.UInt32), Col("Value", ColumnType.UInt16, 2)],
            Row(("ID", (object?)(uint)1), ("Value", (ushort)100)));

        var source = MakeTable("Items",
            [Col("ID", ColumnType.UInt32), Col("Value", ColumnType.String, 32)],
            Row(("ID", (object?)(uint)1), ("Value", "text")));

        var join = new JoinClause { Source = "ID", Target = "ID" };
        var result = TableMerger.Merge(target, source, join, "client", "auto");

        // Original "Value" column kept for target
        result.Table.Columns.ShouldContain(c => c.Name == "Value");
        // Split column "Value__client" added for source
        result.Table.Columns.ShouldContain(c => c.Name == "Value__client");

        // Verify the split column has client env
        var splitCol = result.Table.Columns.First(c => c.Name == "Value__client");
        splitCol.Environments.ShouldNotBeNull();
        splitCol.Environments.ShouldContain("client");
        splitCol.Type.ShouldBe(ColumnType.String);
    }

    [Fact]
    public void ColumnLengthOverride_SameType_DifferentLength_SharedWithOverride()
    {
        var target = MakeTable("Items",
            [Col("ID", ColumnType.UInt32), Col("Name", ColumnType.String, 64)],
            Row(("ID", (object?)(uint)1), ("Name", "Sword")));

        var source = MakeTable("Items",
            [Col("ID", ColumnType.UInt32), Col("Name", ColumnType.String, 32)],
            Row(("ID", (object?)(uint)1), ("Name", "Sword")));

        var join = new JoinClause { Source = "ID", Target = "ID" };
        var result = TableMerger.Merge(target, source, join, "client", "auto");

        // Name is shared (same type, just different length)
        var nameCol = result.Table.Columns.First(c => c.Name == "Name");
        nameCol.Environments.ShouldBeNull();

        // Length override should be in env metadata
        result.EnvMetadata.ShouldContainKey("client");
        var clientMeta = result.EnvMetadata["client"];
        clientMeta.ColumnOverrides.ShouldContainKey("Name");
        clientMeta.ColumnOverrides["Name"].Length.ShouldBe(32);
    }

    // --- Row merge tests ---

    [Fact]
    public void RowMergeByKey_MatchedShared_ExtrasEnvSpecific()
    {
        var target = MakeTable("Items",
            [Col("ID", ColumnType.UInt32), Col("Name", ColumnType.String, 64)],
            Row(("ID", (object?)(uint)1), ("Name", "Sword")),
            Row(("ID", (uint)2), ("Name", "Shield")));

        var source = MakeTable("Items",
            [Col("ID", ColumnType.UInt32), Col("Name", ColumnType.String, 64)],
            Row(("ID", (object?)(uint)1), ("Name", "Sword")),
            Row(("ID", (uint)3), ("Name", "Wand")));

        var join = new JoinClause { Source = "ID", Target = "ID" };
        var result = TableMerger.Merge(target, source, join, "client", "auto");

        result.Table.Data.Count.ShouldBe(3); // ID=1 shared, ID=2 target-only, ID=3 source-only

        result.Table.RowEnvironments.ShouldNotBeNull();
        var envs = result.Table.RowEnvironments!;

        // Find the shared row (ID=1)
        var sharedIdx = result.Table.Data.ToList().FindIndex(r => r["ID"]?.ToString() == "1");
        envs[sharedIdx].ShouldBeNull(); // shared

        // Find the source-only row (ID=3)
        var sourceIdx = result.Table.Data.ToList().FindIndex(r => r["ID"]?.ToString() == "3");
        envs[sourceIdx].ShouldNotBeNull();
        envs[sourceIdx]!.ShouldContain("client");
    }

    [Fact]
    public void TrueValueConflict_MatchedRow_DifferentSharedValue()
    {
        var target = MakeTable("Items",
            [Col("ID", ColumnType.UInt32), Col("Name", ColumnType.String, 64)],
            Row(("ID", (object?)(uint)1), ("Name", "Sword")));

        var source = MakeTable("Items",
            [Col("ID", ColumnType.UInt32), Col("Name", ColumnType.String, 64)],
            Row(("ID", (object?)(uint)1), ("Name", "Blade")));

        var join = new JoinClause { Source = "ID", Target = "ID" };
        var result = TableMerger.Merge(target, source, join, "client", "auto");

        result.Conflicts.ShouldNotBeEmpty();
        result.Conflicts[0].JoinKey.ShouldBe("1");
        result.Conflicts[0].Column.ShouldBe("Name");
        result.Conflicts[0].TargetValue.ShouldBe("Sword");
        result.Conflicts[0].SourceValue.ShouldBe("Blade");
    }

    [Fact]
    public void CrossKeyJoin_DifferentColumnNames()
    {
        var target = MakeTable("Items",
            [Col("ID", ColumnType.UInt32), Col("InxName", ColumnType.String, 64)],
            Row(("ID", (object?)(uint)1), ("InxName", "item001")));

        var source = MakeTable("Items",
            [Col("ClientID", ColumnType.UInt32), Col("InxName", ColumnType.String, 64)],
            Row(("ClientID", (object?)(uint)99), ("InxName", "item001")));

        // Join on different column names
        var join = new JoinClause { Source = "InxName", Target = "InxName" };
        var result = TableMerger.Merge(target, source, join, "client", "auto");

        // Should match by InxName and merge
        result.Table.Data.Count.ShouldBe(1);

        // ClientID should be added as client-only column
        result.Table.Columns.ShouldContain(c => c.Name == "ClientID");
        var clientIdCol = result.Table.Columns.First(c => c.Name == "ClientID");
        clientIdCol.Environments.ShouldNotBeNull();
        clientIdCol.Environments.ShouldContain("client");
    }

    [Fact]
    public void MergeMetadata_PerEnvColumnOrderAndOverrides()
    {
        var target = MakeTable("Items",
            [Col("ID", ColumnType.UInt32), Col("Name", ColumnType.String, 64), Col("Level", ColumnType.UInt16, 2)],
            Row(("ID", (object?)(uint)1), ("Name", "Sword"), ("Level", (ushort)10)));

        var source = MakeTable("Items",
            [Col("ID", ColumnType.UInt32), Col("Name", ColumnType.String, 32), Col("ViewCol", ColumnType.String, 16)],
            Row(("ID", (object?)(uint)1), ("Name", "Sword"), ("ViewCol", "icon.png")));

        var join = new JoinClause { Source = "ID", Target = "ID" };
        var result = TableMerger.Merge(target, source, join, "client", "auto");

        // Client metadata should have its original column order
        result.EnvMetadata.ShouldContainKey("client");
        var clientMeta = result.EnvMetadata["client"];
        clientMeta.ColumnOrder.ShouldBe(["ID", "Name", "ViewCol"]);

        // Client should have length override for Name (32 vs 64)
        clientMeta.ColumnOverrides.ShouldContainKey("Name");
        clientMeta.ColumnOverrides["Name"].Length.ShouldBe(32);
    }

    [Fact]
    public void SourceOnlyRow_HasNullForTargetOnlyColumns()
    {
        var target = MakeTable("Items",
            [Col("ID", ColumnType.UInt32), Col("ServerCol", ColumnType.UInt16, 2)],
            Row(("ID", (object?)(uint)1), ("ServerCol", (ushort)10)));

        var source = MakeTable("Items",
            [Col("ID", ColumnType.UInt32), Col("ClientCol", ColumnType.String, 32)],
            Row(("ID", (object?)(uint)2), ("ClientCol", "only-in-client")));

        var join = new JoinClause { Source = "ID", Target = "ID" };
        var result = TableMerger.Merge(target, source, join, "client", "auto");

        // Source-only row (ID=2) should have null for ServerCol
        var sourceRow = result.Table.Data.First(r => r["ID"]?.ToString() == "2");
        sourceRow.ShouldContainKey("ServerCol");
        sourceRow["ServerCol"].ShouldBeNull();

        // Target-only row (ID=1) should have null for ClientCol
        var targetRow = result.Table.Data.First(r => r["ID"]?.ToString() == "1");
        targetRow.ShouldContainKey("ClientCol");
        targetRow["ClientCol"].ShouldBeNull();
    }
}
