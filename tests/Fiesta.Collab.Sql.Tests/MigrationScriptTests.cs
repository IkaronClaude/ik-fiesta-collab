using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Fiesta.Collab.Sql.Tests;

/// <summary>
/// Migration-comment directives (docs/DESIGN-variants-and-native-steps.md, section 2): @param constants, @assert
/// guards and @report tables, run around the migration's own statements.
/// </summary>
public class MigrationScriptTests : IDisposable
{
    private readonly SqlEngine _engine = new(NullLogger<SqlEngine>.Instance);
    private readonly string _reports = Path.Combine(Path.GetTempPath(), "collab-reports-" + Guid.NewGuid().ToString("N"));

    public MigrationScriptTests()
        => _engine.Execute("CREATE TABLE Items (ID INTEGER, Name TEXT, Price INTEGER);" +
                           "INSERT INTO Items VALUES (1, 'Sword', 100), (2, 'Shield', 250);");

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_reports)) Directory.Delete(_reports, true);
    }

    private long Price(int id) => Convert.ToInt64(_engine.Query($"SELECT Price FROM Items WHERE ID = {id}")[0]["Price"]);

    [Fact]
    public void Param_is_substituted_as_a_named_constant()
    {
        MigrationScript.Run(_engine, """
            -- @param FACTOR = 3
            -- @param NAME = 'Sword'
            UPDATE Items SET Price = Price * :FACTOR WHERE Name = :NAME;
            """, "t.sql", _reports);

        Price(1).ShouldBe(300);
        Price(2).ShouldBe(250);
    }

    [Fact]
    public void Param_does_not_touch_longer_names_or_string_contents()
    {
        MigrationScript.Run(_engine, """
            -- @param P = 7
            UPDATE Items SET Name = ':P and :PX' WHERE ID = :P - 6;
            """, "t.sql", _reports);

        _engine.Query("SELECT Name FROM Items WHERE ID = 1")[0]["Name"].ShouldBe(":P and :PX");
    }

    [Fact]
    public void Assert_that_returns_rows_fails_the_migration_with_the_rows()
    {
        var ex = Should.Throw<MigrationAssertException>(() => MigrationScript.Run(_engine, """
            UPDATE Items SET Price = 70000 WHERE ID = 2;
            -- @assert SELECT ID, Price FROM Items WHERE Price > 65535
            """, "0007-prices.sql", _reports));

        ex.Message.ShouldContain("0007-prices.sql");
        ex.Message.ShouldContain("70000");
    }

    [Fact]
    public void Assert_that_returns_nothing_passes()
    {
        MigrationScript.Run(_engine, """
            -- @param MAX = 65535
            UPDATE Items SET Price = 500 WHERE ID = 2;
            -- @assert SELECT ID FROM Items WHERE Price > :MAX
            """, "t.sql", _reports).ShouldBe(1);
    }

    [Fact]
    public void Report_writes_a_markdown_table_after_the_statements()
    {
        MigrationScript.Run(_engine, """
            UPDATE Items SET Price = 1 WHERE ID = 1;
            -- @report prices SELECT ID, Name, Price FROM Items ORDER BY ID
            """, "t.sql", _reports);

        var md = File.ReadAllText(Path.Combine(_reports, "prices.md"));
        md.ShouldContain("| ID | Name | Price |");
        md.ShouldContain("| 1 | Sword | 1 |");
        md.ShouldContain("| 2 | Shield | 250 |");
    }

    [Fact]
    public void A_script_of_directives_only_changes_nothing()
    {
        MigrationScript.Run(_engine, "-- @report all SELECT * FROM Items", "t.sql", _reports).ShouldBe(0);
    }

    [Fact]
    public void Table_directive_creates_the_table_before_the_statements_and_reports_it()
    {
        var decls = new List<TableDeclaration>();
        MigrationScript.Run(_engine, """
            -- @table Shop_Tab01 LIKE Items FILE Shop.txt SECTION 1 AS Tab01
            INSERT INTO Shop_Tab01 (ID, Name, Price) VALUES (7, 'Potion', 5);
            """, "t.sql", _reports, decls.Add);

        decls.Single().ShouldBe(new TableDeclaration("Shop_Tab01", "Items", "Shop.txt", 1, "Tab01"));
        _engine.Query("SELECT Name FROM Shop_Tab01")[0]["Name"].ShouldBe("Potion");
        _engine.Query("SELECT COUNT(*) AS n FROM Items")[0]["n"].ShouldBe(2L);   // the template keeps its rows
    }

    [Fact]
    public void Table_directive_is_refused_where_nobody_can_keep_the_table()
    {
        Should.Throw<NotSupportedException>(() => MigrationScript.Run(_engine,
            "-- @table Shop_Tab00 LIKE Items FILE Shop.txt", "t.sql", _reports));
    }
}
