using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Fiesta.Collab.Sql.Tests;

/// <summary>
/// -- @copy (a row copied with column overrides, in place among the statements) and the id registry seen as the temp
/// table _id_registry (docs/DESIGN-variants-and-native-steps.md 6a).
/// </summary>
public class CopyAndRegistryTests : IDisposable
{
    private readonly SqlEngine _engine = new(NullLogger<SqlEngine>.Instance);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "collab-registry-" + Guid.NewGuid().ToString("N"));

    // what Python's json.dump(..., indent=1) writes (Fiesta2026on2016 variant-ids.json): LF, no trailing newline
    private const string PyRegistry =
        "{\n \"_doc\": [\n  \"Ids are forever.\",\n  \"caf\\u00e9 \\\"quoted\\\"\"\n ],\n \"items\": {\n  \"A\": 60000,\n  \"B\": 60001\n },\n" +
        " \"handles\": {\n  \"A\": [\n   10113\n  ],\n  \"B\": [\n   10114,\n   10115\n  ]\n }\n}";

    public CopyAndRegistryTests()
    {
        Directory.CreateDirectory(_dir);
        _engine.Execute("CREATE TABLE Items (ID INTEGER, InxName TEXT, Name TEXT, Price INTEGER);" +
                        "INSERT INTO Items VALUES (1, 'Sword', 'Sword', 100), (2, 'Shield', 'Shield', 250);");
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private string Registry(string text)
    {
        var p = Path.Combine(_dir, "ids.json");
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void Copy_appends_the_picked_rows_with_the_set_columns_replaced()
    {
        var n = MigrationScript.Run(_engine, """
            UPDATE Items SET Price = 300 WHERE ID = 2;
            -- @copy Items WHERE InxName = 'Shield' SET ID = 3, InxName = 'BigShield', Name = Name || ' (big)'
            UPDATE Items SET Price = Price * 2 WHERE ID = 3;
            """, "t.sql", null);

        n.ShouldBe(3);
        var r = _engine.Query("SELECT * FROM Items WHERE ID = 3").ShouldHaveSingleItem();
        r["InxName"].ShouldBe("BigShield");
        r["Name"].ShouldBe("Shield (big)");
        Convert.ToInt64(r["Price"]).ShouldBe(600);   // copied AFTER the first UPDATE, doubled by the one after it
    }

    [Fact]
    public void Copy_refuses_an_unknown_column()
        => Should.Throw<InvalidOperationException>(() => MigrationScript.Run(_engine,
            "-- @copy Items WHERE ID = 1 SET Nope = 1", "t.sql", null)).Message.ShouldContain("Nope");

    [Fact]
    public void Registry_untouched_is_rewritten_never_and_read_as_rows()
    {
        var path = Registry(PyRegistry);
        var reg = IdRegistry.Load(path);
        MigrationScript.Run(_engine, """
            UPDATE Items SET Price = (SELECT value FROM _id_registry WHERE kind = 'handles' AND key = 'B' AND idx = 1) WHERE ID = 1;
            """, "t.sql", null, registry: reg);

        Convert.ToInt64(_engine.Query("SELECT Price FROM Items WHERE ID = 1")[0]["Price"]).ShouldBe(10115);
        File.ReadAllText(path).ShouldBe(PyRegistry);
    }

    [Fact]
    public void Registry_new_entries_are_appended_in_python_layout()
    {
        var path = Registry(PyRegistry);
        MigrationScript.Run(_engine, """
            INSERT INTO _id_registry (kind, key, idx, value) VALUES ('items', 'C', 0, 60002);
            INSERT INTO _id_registry (kind, key, idx, value) VALUES ('handles', 'C', 0, 10116), ('handles', 'C', 1, 10117);
            """, "t.sql", null, registry: IdRegistry.Load(path));

        File.ReadAllText(path).ShouldBe(
            "{\n \"_doc\": [\n  \"Ids are forever.\",\n  \"caf\\u00e9 \\\"quoted\\\"\"\n ],\n \"items\": {\n  \"A\": 60000,\n  \"B\": 60001,\n" +
            "  \"C\": 60002\n },\n \"handles\": {\n  \"A\": [\n   10113\n  ],\n  \"B\": [\n   10114,\n   10115\n  ],\n" +
            "  \"C\": [\n   10116,\n   10117\n  ]\n }\n}");
    }

    [Fact]
    public void Registry_refuses_a_changed_or_removed_entry()
    {
        var path = Registry(PyRegistry);
        Should.Throw<InvalidOperationException>(() => MigrationScript.Run(_engine,
            "UPDATE _id_registry SET value = 1 WHERE key = 'A' AND kind = 'items';", "t.sql", null,
            registry: IdRegistry.Load(path))).Message.ShouldContain("forever");
        Should.Throw<InvalidOperationException>(() => MigrationScript.Run(_engine,
            "DELETE FROM _id_registry WHERE key = 'B';", "t.sql", null,
            registry: IdRegistry.Load(path))).Message.ShouldContain("forever");
        File.ReadAllText(path).ShouldBe(PyRegistry);
    }

    [Fact]
    public void Registry_without_a_file_is_refused_by_name()
        => Should.Throw<NotSupportedException>(() => MigrationScript.Run(_engine,
            "SELECT * FROM _id_registry;", "t.sql", null)).Message.ShouldContain("idRegistry");

    [Fact]
    public void Clone_by_registry_and_copy_together()
    {
        var path = Registry(PyRegistry);
        MigrationScript.Run(_engine, """
            INSERT INTO _id_registry (kind, key, idx, value)
                SELECT 'items', 'Sword2', 0, (SELECT MAX(value) + 1 FROM _id_registry WHERE kind = 'items')
                WHERE NOT EXISTS (SELECT 1 FROM _id_registry WHERE kind = 'items' AND key = 'Sword2');
            -- @copy Items WHERE InxName = 'Sword' SET ID = (SELECT value FROM _id_registry WHERE kind = 'items' AND key = 'Sword2'), InxName = 'Sword2'
            """, "t.sql", null, registry: IdRegistry.Load(path));

        Convert.ToInt64(_engine.Query("SELECT ID FROM Items WHERE InxName = 'Sword2'")[0]["ID"]).ShouldBe(60002);
        File.ReadAllText(path).ShouldContain("\"Sword2\": 60002");
    }
}
