using Fiesta.Collab.Shn;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Text.Json;
using Xunit;

namespace Fiesta.Collab.Shn.Tests;

/// <summary>
/// The monolith QuestData layout, pinned against real files.
///
/// These assert MEASURED values, not intended ones, because the record is a nest of structs with compiler
/// padding and every offset is a chance to be one or two bytes out in a way that still parses. A wrong
/// NPCMobList stride, for instance, reads plausible small integers out of the neighbouring entry and only
/// shows up as the wrong monster in game. The quests below are the ones whose givers and objectives are
/// independently known, so a padding slip moves them.
///
/// These return early when the reference client is not mounted: the game files live outside the repo and
/// are never committed, so the suite has to pass without them.
/// </summary>
public class QuestDataProviderTests
{
    private const string Monolith2016 = "Z:/ClientProd2/ressystem/QuestData.shn";
    private const string Columnar2026 = "Z:/ClientOfficialUS/ressystem/QuestData.shn";

    private static QuestDataProvider Provider() => new(NullLogger<QuestDataProvider>.Instance);

    private static bool Available(string p) => File.Exists(p);

    [Fact]
    public void Detects_the_2016_monolith()
    {
        if (!Available(Monolith2016)) return;   // reference client not mounted
        Provider().CanHandle(Monolith2016).ShouldBeTrue();
    }

    [Fact]
    public void Declines_the_2026_columnar_file_so_the_shn_provider_takes_it()
    {
        // 2026 split the quests into normalised tables; the file with the same name is an ordinary
        // 33-column SHN. Detection reads the file rather than trusting the name or a client version.
        if (!Available(Columnar2026)) return;   // reference client not mounted
        Provider().CanHandle(Columnar2026).ShouldBeFalse();
    }

    [Fact]
    public async Task Reads_every_quest_of_the_live_file()
    {
        if (!Available(Monolith2016)) return;   // reference client not mounted
        var t = (await Provider().ReadAsync(Monolith2016))[0];
        t.Rows.Count.ShouldBe(2304);
        t.Schema.Metadata!["questDataFormat"].ShouldBe("monolith");
    }

    [Theory]
    // quest, giver NPC, NeedsNPC, NeedsLevel, min, max — the givers are known by name:
    // 111 Remi, 88 Zach, 29 Julia, 93 Pey.
    [InlineData(1, 111, 1, 1, 1, 10)]
    [InlineData(8, 88, 1, 1, 2, 10)]
    [InlineData(12, 29, 0, 0, 0, 15)]
    [InlineData(21, 93, 0, 0, 0, 14)]
    [InlineData(415, 28, 1, 1, 7, 10)]
    [InlineData(392, 152, 1, 1, 81, 83)]
    public async Task The_start_condition_lands_on_its_fields(int id, int npc, int needsNpc,
                                                              int needsLevel, int min, int max)
    {
        if (!Available(Monolith2016)) return;   // reference client not mounted
        var row = await Quest(id);
        Convert.ToInt32(row["StartNPC"]).ShouldBe(npc);
        Convert.ToInt32(row["NeedsNPC"]).ShouldBe(needsNpc);
        Convert.ToInt32(row["NeedsLevel"]).ShouldBe(needsLevel);
        Convert.ToInt32(row["LevelMin"]).ShouldBe(min);
        Convert.ToInt32(row["LevelMax"]).ShouldBe(max);
    }

    [Theory]
    // quest -> the objectives, as "NPCMobID:Action:Count". Action 0 is the hand-in NPC, 1 is kill.
    // These catch a wrong NPCMobList stride: at stride 6 instead of 8 the ids slide into each other.
    [InlineData(8, "88:0:0,0:1:5")]
    [InlineData(12, "29:0:0,2002:1:1")]
    [InlineData(21, "93:0:0,6:1:5")]
    [InlineData(415, "28:0:0,303:1:1,304:1:2")]
    [InlineData(392, "152:0:0,515:1:50")]
    public async Task The_objectives_land_on_their_entries(int id, string expected)
    {
        if (!Available(Monolith2016)) return;   // reference client not mounted
        var row = await Quest(id);
        var objs = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>((string)row["Objectives"]!)!;
        var got = string.Join(",", objs.Select(o =>
            $"{o["NPCMobID"].GetInt32()}:{o["Action"].GetInt32()}:{o["Count"].GetInt32()}"));
        got.ShouldBe(expected);
    }

    [Fact]
    public async Task Title_and_description_ids_are_u32()
    {
        // An earlier reading had these as u16, which silently truncated every id above 65535.
        if (!Available(Monolith2016)) return;   // reference client not mounted
        Convert.ToInt64((await Quest(1))["NameID"]).ShouldBe(200);
        Convert.ToInt64((await Quest(392))["NameID"]).ShouldBe(12500);
        Convert.ToInt64((await Quest(415))["NameID"]).ShouldBe(18700);
    }

    [Fact]
    public async Task Region_is_at_sixteen_not_a_level()
    {
        if (!Available(Monolith2016)) return;   // reference client not mounted
        Convert.ToInt32((await Quest(392))["Region"]).ShouldBe(17);
        Convert.ToInt32((await Quest(1))["Region"]).ShouldBe(0);
    }

    [Fact]
    public async Task Round_trips_the_live_file_byte_for_byte()
    {
        // The strongest check there is: every byte this provider does not decode has to come back
        // untouched, including the three dead script pointers that hold heap garbage on disk.
        if (!Available(Monolith2016)) return;   // reference client not mounted
        var p = Provider();
        var tables = await p.ReadAsync(Monolith2016);
        var tmp = Path.Combine(Path.GetTempPath(), $"questdata-{Guid.NewGuid():N}.shn");
        try
        {
            await p.WriteAsync(tmp, tables);
            File.ReadAllBytes(tmp).ShouldBe(File.ReadAllBytes(Monolith2016));
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static async Task<Dictionary<string, object?>> Quest(int id)
    {
        var t = (await Provider().ReadAsync(Monolith2016))[0];
        return t.Rows.First(r => Convert.ToInt32(r["ID"]) == id);
    }
}
