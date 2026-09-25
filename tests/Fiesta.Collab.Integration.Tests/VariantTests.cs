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
/// Build variants (docs/DESIGN-variants-and-native-steps.md, section 1): fiesta.json "variants" name layer directories
/// whose migrations are applied, in order, on top of data/ - in memory, never written back.
/// </summary>
public class VariantTests : IAsyncLifetime
{
    private string _dir = null!;
    private ServiceProvider _sp = null!;
    private IProjectService _ps = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"collab-variant-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddFiestaCore();
        services.AddFiestaSql();
        _sp = services.BuildServiceProvider();
        _ps = _sp.GetRequiredService<IProjectService>();

        await Table("Items", [Row(1, "Sword", 100), Row(2, "Shield", 250)]);
        await Table("Mobs", [Row(1, "Slime", 1)]);
        // a Float that is not exact in binary: JSON gives the double 0.6, SQLite hands back the Single 0.6f
        await _ps.WriteTableFileAsync(_dir, "data/Views.json", new TableFile
        {
            Header = new TableHeader { TableName = "Views", SourceFormat = "shn" },
            Columns = [new ColumnDefinition { Name = "ID", Type = ColumnType.UInt32, Length = 4 },
                       new ColumnDefinition { Name = "Scale", Type = ColumnType.Float, Length = 4 }],
            Data = [new() { ["ID"] = 1L, ["Scale"] = 0.6 }, new() { ["ID"] = 2L, ["Scale"] = 1.855 }]
        });
        await _ps.SaveProjectAsync(_dir, new FiestaProject
        {
            Tables = { ["Items"] = "data/Items.json", ["Mobs"] = "data/Mobs.json", ["Views"] = "data/Views.json" },
            Variants = new() { ["qol"] = ["layer-a"], ["rebalanced"] = ["layer-a", "layer-b"] }
        });
        Layer("layer-a", "0001-cheap.sql", "UPDATE Items SET Price = 1 WHERE ID = 1;");
        Layer("layer-b", "0001-double.sql", "-- @param F = 2\nUPDATE Items SET Price = Price * :F;");
    }

    public Task DisposeAsync()
    {
        _sp.Dispose();
        Directory.Delete(_dir, true);
        return Task.CompletedTask;
    }

    private static Dictionary<string, object?> Row(long id, string name, long price)
        => new() { ["ID"] = id, ["Name"] = name, ["Price"] = price };

    private Task Table(string name, IReadOnlyList<Dictionary<string, object?>> rows) => _ps.WriteTableFileAsync(_dir, $"data/{name}.json", new TableFile
    {
        Header = new TableHeader { TableName = name, SourceFormat = "shn" },
        Columns =
        [
            new ColumnDefinition { Name = "ID", Type = ColumnType.UInt32, Length = 4 },
            new ColumnDefinition { Name = "Name", Type = ColumnType.String, Length = 32 },
            new ColumnDefinition { Name = "Price", Type = ColumnType.UInt32, Length = 4 },
        ],
        Data = rows
    });

    private void Layer(string dir, string file, string sql)
    {
        Directory.CreateDirectory(Path.Combine(_dir, dir));
        File.WriteAllText(Path.Combine(_dir, dir, file), sql);
    }

    // rows read back from data/ hold JsonElement values, rows from the engine longs
    private static long N(object? v) => long.Parse(v!.ToString()!);

    private static long Price(TableFile t, int id) => N(t.Data.Single(r => N(r["ID"]) == id)["Price"]);

    [Fact]
    public async Task Layers_apply_in_order_and_only_changed_tables_come_back()
    {
        var changed = await Migrations.ApplyVariantAsync(_dir, "rebalanced", _sp, NullLogger.Instance);

        changed.Keys.ShouldBe(["Items"]);            // not Views: its floats only went through the round trip
        Price(changed["Items"], 1).ShouldBe(2);      // layer-a set 1, layer-b doubled it
        Price(changed["Items"], 2).ShouldBe(500);
    }

    [Fact]
    public async Task Data_is_never_written()
    {
        await Migrations.ApplyVariantAsync(_dir, "qol", _sp, NullLogger.Instance);

        var items = await _ps.ReadTableFileAsync(_dir, "data/Items.json");
        Price(items, 1).ShouldBe(100);
    }

    [Fact]
    public async Task Unknown_variant_and_generated_steps_are_refused()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => Migrations.ApplyVariantAsync(_dir, "nope", _sp, NullLogger.Instance));

        Layer("layer-b", "0002-curve.py", "print('generated')");
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Migrations.ApplyVariantAsync(_dir, "rebalanced", _sp, NullLogger.Instance));
        ex.Message.ShouldContain("0002-curve.py");
    }

    [Fact]
    public async Task Variants_survive_a_manifest_round_trip()
    {
        var m = await _ps.LoadProjectAsync(_dir);
        m.Variants!["rebalanced"].ShouldBe(["layer-a", "layer-b"]);
    }

    [Fact]
    public async Task Reports_go_under_the_variant()
    {
        Layer("layer-a", "0002-report.sql", "-- @report cheap SELECT ID, Price FROM Items WHERE Price < 10");
        await Migrations.ApplyVariantAsync(_dir, "qol", _sp, NullLogger.Instance);

        File.Exists(Path.Combine(_dir, "build", "qol", "reports", "cheap.md")).ShouldBeTrue();
    }

    [Fact]
    public void Layer_override_files_come_in_layer_order_later_layers_win()
    {
        void Put(string layer, string rel, string text)
        {
            var p = Path.Combine(_dir, layer, "overrides", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        Put("layer-a", "server/Shine/A.flag", "a");
        Put("layer-a", "server/Shine/Both.txt", "from a");
        Put("layer-b", "server/Shine/Both.txt", "from b");
        Put("layer-b", "client/ressystem/C.txt", "c");

        var files = Migrations.LayerOverrides(_dir, ["layer-a", "layer-b"], "server");

        files.Select(f => f.Relative.Replace('\\', '/')).ShouldBe(["Shine/A.flag", "Shine/Both.txt"], ignoreOrder: true);
        File.ReadAllText(files.Single(f => f.Relative.EndsWith("Both.txt")).Source).ShouldBe("from b");
        Migrations.LayerOverrides(_dir, ["layer-a"], "overlay").ShouldBeEmpty();
    }
}
