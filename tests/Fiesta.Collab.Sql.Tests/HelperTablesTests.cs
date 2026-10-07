using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Fiesta.Collab.Sql.Tests;

/// <summary>
/// Helper tables (-- @helper: kept for later migrations, never built) and _build (what is being built, for conditional
/// edits) - HelperTables.
/// </summary>
public class HelperTablesTests : IDisposable
{
    private readonly SqlEngine _engine = new(NullLogger<SqlEngine>.Instance);

    public void Dispose() => _engine.Dispose();

    [Fact]
    public void A_declared_helper_made_by_the_script_is_handed_to_the_caller()
    {
        var kept = new List<string>();
        MigrationScript.Run(_engine, """
            -- @helper ParityCounts
            CREATE TABLE ParityCounts (quest INTEGER, count INTEGER);
            INSERT INTO ParityCounts VALUES (69, 4), (70, 4);
            """, "0001.sql", null, onHelper: kept.Add);

        kept.ShouldBe(["ParityCounts"]);
        _engine.Query("SELECT COUNT(*) AS n FROM ParityCounts")[0]["n"].ShouldBe(2L);
    }

    [Fact]
    public void A_declared_helper_the_script_did_not_make_fails_the_migration()
        => Should.Throw<InvalidOperationException>(() => MigrationScript.Run(_engine,
            "-- @helper Nope\nCREATE TEMP TABLE other (x INTEGER);", "0001.sql", null)).Message.ShouldContain("Nope");

    [Fact]
    public void Build_tells_the_script_what_is_being_built_and_which_step_it_is()
    {
        _engine.Execute("CREATE TABLE Items (InxName TEXT, Price INTEGER); INSERT INTO Items VALUES ('Sword', 100);");
        var build = new Dictionary<string, string> { ["environment"] = "rebalanced", ["layer"] = "rebalance" };
        MigrationScript.Run(_engine, """
            UPDATE Items SET Price = 1 WHERE (SELECT value FROM _build WHERE key = 'environment') = 'rebalanced';
            CREATE TEMP TABLE seen AS SELECT value FROM _build WHERE key = 'step';
            """, "0042-cheap.sql", null, build: build);

        _engine.Query("SELECT Price FROM Items")[0]["Price"].ShouldBe(1L);
        _engine.Query("SELECT value FROM seen")[0]["value"].ShouldBe("0042-cheap.sql");

        MigrationScript.Run(_engine, "UPDATE Items SET Price = 7 WHERE (SELECT value FROM _build WHERE key = 'environment') = 'rebalanced';",
            "0043.sql", null, build: new Dictionary<string, string> { ["environment"] = "qol" });
        _engine.Query("SELECT Price FROM Items")[0]["Price"].ShouldBe(1L);       // a QoL build: the edit does not apply
    }

    [Fact]
    public void A_helper_round_trips_through_its_json_with_types_nulls_reals_and_blobs()
    {
        _engine.Execute("CREATE TABLE H (k TEXT, n INTEGER, r REAL, b BLOB);" +
                        "INSERT INTO H VALUES ('x''quoted', 5, 1.5, x'00FF'), (NULL, NULL, NULL, NULL), ('x''AB''', -3, 2.0, NULL);");
        var json = HelperTables.ToJson(_engine, "H");
        _engine.Execute("DROP TABLE H");
        HelperTables.FromJson(_engine, "H", json);

        var rows = _engine.Query("SELECT k, n, r, b, typeof(r) AS tr FROM H ORDER BY rowid");
        rows[0]["k"].ShouldBe("x'quoted");
        rows[0]["n"].ShouldBe(5L);
        rows[0]["r"].ShouldBe(1.5);
        ((byte[])rows[0]["b"]!).ShouldBe(new byte[] { 0x00, 0xFF });
        rows[1]["k"].ShouldBeNull();
        rows[2]["k"].ShouldBe("x'AB'");                       // text that looks like a blob literal stays text
        rows[2]["tr"].ShouldBe("real");                        // the declared REAL column keeps 2.0 a real
        HelperTables.ToJson(_engine, "H").ShouldBe(json);
    }
}
