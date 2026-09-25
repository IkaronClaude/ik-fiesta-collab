using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Fiesta.Collab.Sql;

/// <summary>
/// Functions every engine connection carries, so a migration can compute what generated steps used to compute
/// outside collab (docs/DESIGN-variants-and-native-steps.md, section 2). ln / exp / pow come with SQLite's math
/// functions already.
/// <list type="bullet">
/// <item><c>median(x)</c>: aggregate, NULLs ignored, NULL for no rows; an even count averages the middle two.</item>
/// <item><c>interp_log(x, 'k:v,k:v,...')</c>: log-linear between the anchors, flat outside them.</item>
/// <item><c>blob_u8/u16/u32(hex, off)</c> and <c>blob_set_u8/u16/u32(hex, off, v)</c>: little-endian reads and
/// writes over the hex strings blob columns are stored as (QuestData.FixedData); NULL past the end. A write returns
/// uppercase hex.</item>
/// </list>
/// </summary>
public static class SqlFunctions
{
    public static void Register(SqliteConnection c)
    {
        // the seed is ONE object shared by every call, so the list is made per group on its first row
        c.CreateAggregate<double?, List<double>?, double?>("median",
            null,
            (acc, x) => { acc ??= new List<double>(); if (x is { } v) acc.Add(v); return acc; },
            acc => acc is null ? null : Median(acc),
            isDeterministic: true);
        c.CreateFunction<double?, string?, double?>("interp_log",
            (x, pts) => x is { } v && pts is not null ? InterpLog(v, pts) : null, isDeterministic: true);
        foreach (var (name, size) in new[] { ("u8", 1), ("u16", 2), ("u32", 4) })
        {
            var n = size;
            c.CreateFunction<string?, long?, long?>("blob_" + name,
                (hex, off) => hex is null || off is null ? null : Read(hex, (int)off, n), isDeterministic: true);
            c.CreateFunction<string?, long?, long?, string?>("blob_set_" + name,
                (hex, off, v) => hex is null || off is null || v is null ? null : Write(hex, (int)off, n, v.Value),
                isDeterministic: true);
        }
    }

    public static double? Median(List<double> xs)
    {
        if (xs.Count == 0) return null;
        xs.Sort();
        var m = xs.Count / 2;
        return xs.Count % 2 == 1 ? xs[m] : (xs[m - 1] + xs[m]) / 2;
    }

    public static double InterpLog(double x, string points)
    {
        var pts = points.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split(':'))
            .Select(kv => (k: double.Parse(kv[0], CultureInfo.InvariantCulture), v: double.Parse(kv[1], CultureInfo.InvariantCulture)))
            .OrderBy(p => p.k).ToList();
        if (pts.Count == 0) throw new ArgumentException("interp_log: no anchors");
        if (x <= pts[0].k) return pts[0].v;
        if (x >= pts[^1].k) return pts[^1].v;
        var i = pts.FindIndex(p => p.k > x);
        var (a, b) = (pts[i - 1], pts[i]);
        var t = (x - a.k) / (b.k - a.k);
        return Math.Exp(Math.Log(a.v) * (1 - t) + Math.Log(b.v) * t);
    }

    private static long? Read(string hex, int off, int size)
    {
        var b = Convert.FromHexString(hex);
        if (off < 0 || off + size > b.Length) return null;
        long v = 0;
        for (var i = size - 1; i >= 0; i--) v = (v << 8) | b[off + i];
        return v;
    }

    private static string? Write(string hex, int off, int size, long value)
    {
        var b = Convert.FromHexString(hex);
        if (off < 0 || off + size > b.Length) return null;
        for (var i = 0; i < size; i++) b[off + i] = (byte)(value >> (8 * i));
        return Convert.ToHexString(b);
    }
}
