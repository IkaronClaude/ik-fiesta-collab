using Fiesta.Collab.Cli;
using Fiesta.Collab.Core;
using Fiesta.Collab.Core.Models;
using Fiesta.Collab.Core.Project;
using Fiesta.Collab.Sql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Fiesta.Collab.Integration.Tests;

/// <summary>
/// migrations/NNNN-name.py runs in sequence with the .sql steps against the LIVE engine of the migrate run
/// (PythonStep): it sees what earlier steps wrote, later steps see what it wrote, nothing is reloaded.
/// </summary>
public class PythonStepTests : IAsyncLifetime
{
    private string _dir = null!;
    private ServiceProvider _sp = null!;
    private IProjectService _ps = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"collab-pystep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "migrations"));
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddFiestaCore();
        services.AddFiestaSql();
        _sp = services.BuildServiceProvider();
        _ps = _sp.GetRequiredService<IProjectService>();
        await _ps.WriteTableFileAsync(_dir, "data/Items.json", new TableFile
        {
            Header = new TableHeader { TableName = "Items", SourceFormat = "shn" },
            Columns =
            [
                new ColumnDefinition { Name = "ID", Type = ColumnType.UInt32, Length = 4 },
                new ColumnDefinition { Name = "Name", Type = ColumnType.String, Length = 32 },
                new ColumnDefinition { Name = "Price", Type = ColumnType.UInt32, Length = 4 },
            ],
            Data = [new() { ["ID"] = 1L, ["Name"] = "Sword", ["Price"] = 100L }]
        });
        await _ps.SaveProjectAsync(_dir, new FiestaProject { Tables = { ["Items"] = "data/Items.json" } });
    }

    public Task DisposeAsync()
    {
        _sp.Dispose();
        Directory.Delete(_dir, true);
        return Task.CompletedTask;
    }

    private static long L(object? o) => o is System.Text.Json.JsonElement e ? e.GetInt64() : Convert.ToInt64(o);
    private static string S(object? o) => o is System.Text.Json.JsonElement e ? e.GetString()! : (string)o!;

    private void Step(string file, string text) => File.WriteAllText(Path.Combine(_dir, "migrations", file), text);

    [Fact]
    public async Task A_python_step_reads_and_writes_the_live_engine_between_sql_steps()
    {
        Step("0001-price.sql", "UPDATE Items SET Price = 150 WHERE ID = 1;");
        Step("0002-copies.py", string.Join("\n",
            "from fiesta_step import query, executemany",
            "rows = query('SELECT ID, Name, Price FROM Items WHERE Price = @p', {'p': 150})",
            "print('saw', len(rows), 'row(s)')",
            "executemany('INSERT INTO Items (ID, Name, Price) VALUES (@id, @name, @price)',",
            "            [{'id': r['ID'] + 10 * k, 'name': r['Name'] + str(k), 'price': r['Price'] + k} for r in rows for k in (1, 2)])",
            ""));
        Step("0003-after.sql", "UPDATE Items SET Price = Price + 1000 WHERE ID = 21;");

        await Migrations.RunAsync(_dir, _sp, NullLogger.Instance);

        var items = await _ps.ReadTableFileAsync(_dir, "data/Items.json");
        items.Data.Select(r => (L(r["ID"]), S(r["Name"]), L(r["Price"])))
            .ShouldBe([(1L, "Sword", 150L), (11L, "Sword1", 151L), (21L, "Sword2", 1152L)]);
    }

    [Fact]
    public async Task A_failing_python_step_stops_the_run_and_saves_nothing()
    {
        Step("0001-price.sql", "UPDATE Items SET Price = 999 WHERE ID = 1;");
        Step("0002-broken.py", "from fiesta_step import execute\nexecute('UPDATE NoSuchTable SET X = 1')\n");

        await Should.ThrowAsync<InvalidOperationException>(() => Migrations.RunAsync(_dir, _sp, NullLogger.Instance));

        var items = await _ps.ReadTableFileAsync(_dir, "data/Items.json");
        L(items.Data[0]["Price"]).ShouldBe(100L);
    }
}
