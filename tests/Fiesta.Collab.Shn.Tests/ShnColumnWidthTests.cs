using Fiesta.Collab.Shn;
using Fiesta.Collab.Shn.Crypto;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Fiesta.Collab.Shn.Tests;

/// <summary>
/// A numeric column's width comes from the header, not from its type code.
///
/// Type 29 is 8 bytes in the 2016 tables and 4 in the 2026 ones (a localisation id), so a reader that
/// derives the width from the code reads 8 bytes for a 4-byte field and walks off the end of the file.
/// That cost the whole of MobInfo, and an unrecognised code cost the whole of MobLoca - both silently, as
/// a logged read failure that left the table simply absent from the build.
///
/// These return early when the reference clients are not mounted; the game files live outside the repo.
/// </summary>
public class ShnColumnWidthTests
{
    private const string Client2026 = "Z:/ClientOfficialUS/ressystem";
    private const string Client2016 = "Z:/ClientProd2/ressystem";

    private static ShnDataProvider Provider() => new(new ShnCrypto(), NullLogger<ShnDataProvider>.Instance);

    [Fact]
    public async Task Reads_a_2026_table_whose_type_29_is_four_bytes()
    {
        var p = Path.Combine(Client2026, "MobInfo.shn");
        if (!File.Exists(p)) return;

        var t = (await Provider().ReadAsync(p))[0];
        t.Rows.Count.ShouldBe(5370);   // the header's record count; 5,355 of the InxNames are distinct

        var name = t.Schema.Columns.First(c => c.Name == "Name");
        name.SourceTypeCode.ShouldBe(29);
        name.Length.ShouldBe(4);          // 4 here, 8 in the 2016 files
    }

    [Fact]
    public async Task Reads_a_table_using_a_type_code_it_has_never_seen()
    {
        // MobLoca uses type 28. Refusing an unknown numeric code loses the entire table, and a column of a
        // declared width is always readable.
        var p = Path.Combine(Client2026, "Loca", "MobLoca.shn");
        if (!File.Exists(p)) p = Path.Combine(Client2026, "MobLoca.shn");
        if (!File.Exists(p)) return;

        var t = (await Provider().ReadAsync(p))[0];
        t.Rows.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Round_trips_a_2016_table_whose_type_29_is_eight_bytes()
    {
        var p = Path.Combine(Client2016, "MobInfo.shn");
        if (!File.Exists(p)) return;

        var provider = Provider();
        var tables = await provider.ReadAsync(p);
        var tmp = Path.Combine(Path.GetTempPath(), $"mobinfo-{Guid.NewGuid():N}.shn");
        try
        {
            await provider.WriteAsync(tmp, tables);
            File.ReadAllBytes(tmp).ShouldBe(File.ReadAllBytes(p));
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
