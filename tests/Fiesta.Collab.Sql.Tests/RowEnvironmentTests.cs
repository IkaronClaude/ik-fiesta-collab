using Fiesta.Collab.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Fiesta.Collab.Sql.Tests;

/// <summary>
/// Row environments as a real column (docs/DESIGN-variants-and-native-steps.md, section 3): every loaded table carries
/// `_envs` (a comma list, NULL = every environment), so a row keeps its environments through deletes and inserts, and a
/// migration can add an environment-specific row.
/// </summary>
public class RowEnvironmentTests : IDisposable
{
    private readonly SqlEngine _engine = new(NullLogger<SqlEngine>.Instance);

    public void Dispose() => _engine.Dispose();

    private static readonly TableSchema Schema = new()
    {
        TableName = "Items",
        SourceFormat = "test",
        Columns =
        [
            new ColumnDefinition { Name = "ID", Type = ColumnType.UInt32, Length = 4 },
            new ColumnDefinition { Name = "Name", Type = ColumnType.String, Length = 32 },
        ]
    };

    private void Load(IReadOnlyList<List<string>?>? envs) => _engine.LoadTable(new TableEntry
    {
        Schema = Schema,
        Rows =
        [
            new() { ["ID"] = 1u, ["Name"] = "Shared" },
            new() { ["ID"] = 2u, ["Name"] = "ServerOnly" },
            new() { ["ID"] = 3u, ["Name"] = "OverlayOnly" },
        ],
        RowEnvironments = envs
    });

    private static string?[] Flat(IReadOnlyList<List<string>?>? envs) => envs?.Select(e => e is null ? null : string.Join(",", e)).ToArray() ?? [];

    [Fact]
    public void Environments_round_trip_and_rows_do_not_carry_the_column()
    {
        Load([null, ["server", "client"], ["overlay"]]);

        var t = _engine.ExtractTable(Schema);

        Flat(t.RowEnvironments).ShouldBe([null, "server,client", "overlay"]);
        t.Rows[0].Keys.ShouldBe(["ID", "Name"]);
    }

    [Fact]
    public void A_delete_keeps_each_row_with_its_own_environments()
    {
        Load([null, ["server", "client"], ["overlay"]]);
        _engine.Execute("DELETE FROM Items WHERE ID = 2");

        var t = _engine.ExtractTable(Schema);

        t.Rows.Select(r => r["Name"]).ShouldBe(["Shared", "OverlayOnly"]);
        Flat(t.RowEnvironments).ShouldBe([null, "overlay"]);
    }

    [Fact]
    public void An_insert_can_name_its_environments_and_is_shared_otherwise()
    {
        Load([null, ["server", "client"], ["overlay"]]);
        _engine.Execute("INSERT INTO Items (ID, Name, _envs) VALUES (4, 'NewServer', 'server');" +
                        "INSERT INTO Items (ID, Name) VALUES (5, 'NewShared');");

        Flat(_engine.ExtractTable(Schema).RowEnvironments).ShouldBe([null, "server,client", "overlay", "server", null]);
    }

    [Fact]
    public void A_table_whose_rows_are_all_shared_has_no_environment_list()
    {
        Load(null);

        _engine.ExtractTable(Schema).RowEnvironments.ShouldBeNull();
    }
}
