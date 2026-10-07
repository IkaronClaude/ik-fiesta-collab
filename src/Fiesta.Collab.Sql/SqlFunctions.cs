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
/// <item><c>blob_u8/u16/u32/u64(hex, off)</c> and <c>blob_set_u8/u16/u32/u64(hex, off, v)</c>: little-endian reads and
/// writes over the hex strings blob columns are stored as (QuestData.FixedData); NULL past the end. A write returns
/// uppercase hex.</item>
/// <item><c>blob_compact(hex, off, size, count, drop)</c>: the <c>count</c> records of <c>size</c> bytes at <c>off</c>
/// with the ones listed in <c>drop</c> (comma-separated indexes, '' or NULL = none) removed, the later ones moved up and
/// zeros at the end (a QuestData reward list keeps its slots contiguous). Uppercase hex; NULL past the end.</item>
/// <item><c>x REGEXP p</c>, <c>regexp_group(x, p, n)</c> (capture n, NULL without a match) and
/// <c>regexp_replace(x, p, r [, count])</c> (every match, or the first count - Python's re.sub): .NET regex syntax,
/// replacements write groups as <c>$1</c>.</item>
/// <item><c>regexp_split_join(x, split, drop, joiner)</c>: <c>x</c> split on the pattern <c>split</c>, the parts
/// <c>drop</c> matches left out (NULL keeps every part), the rest joined with <c>joiner</c> - a text's sentences
/// without some of them.</item>
/// <item><c>regexp_matches(x, p)</c>: every match as a JSON array <c>[{"s": start, "e": end, "m": text, "g": [group, ...]},
/// ...]</c> - positions in characters from 0, so <c>substr(x, s + 1, e - s)</c> is the match; <c>json_each</c> walks it
/// (a decision per match - what Python's re.sub with a callback did).</item>
/// <item><c>text_splice(x, edits)</c>: <c>x</c> with the JSON edits <c>[[start, end, replacement], ...]</c> (positions as
/// regexp_matches gives them, non-overlapping, any order) applied - a callback's replacements put back.</item>
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
        // x REGEXP p calls regexp(p, x); NULL in, NULL out (no match)
        c.CreateFunction<string?, string?, bool?>("regexp",
            (p, x) => p is null || x is null ? null : Rx(p).IsMatch(x), isDeterministic: true);
        c.CreateFunction<string?, string?, long?, string?>("regexp_group",
            (x, p, g) => x is null || p is null || g is null ? null : Group(x, p, (int)g), isDeterministic: true);
        c.CreateFunction<string?, string?, string?, string?>("regexp_replace",
            (x, p, r) => x is null || p is null || r is null ? null : Rx(p).Replace(x, r), isDeterministic: true);
        c.CreateFunction<string?, string?, string?, long?, string?>("regexp_replace",
            (x, p, r, n) => x is null || p is null || r is null || n is null ? null : Rx(p).Replace(x, r, (int)n),
            isDeterministic: true);
        c.CreateFunction<string?, string?, string?, string?, string?>("regexp_split_join",
            (x, split, drop, joiner) => x is null || split is null ? null
                : string.Join(joiner ?? "", Rx(split).Split(x).Where(p => drop is null || !Rx(drop).IsMatch(p))),
            isDeterministic: true);
        c.CreateFunction<string?, string?, string?>("regexp_matches",
            (x, p) => x is null || p is null ? null : Matches(x, p), isDeterministic: true);
        c.CreateFunction<string?, string?, string?>("text_splice",
            (x, edits) => x is null ? null : edits is null ? x : Splice(x, edits), isDeterministic: true);
        c.CreateFunction<string?, long?, long?, long?, string?, string?>("blob_compact",
            (hex, off, size, count, drop) => hex is null || off is null || size is null || count is null ? null
                : Compact(hex, (int)off, (int)size, (int)count, drop), isDeterministic: true);
        foreach (var (name, size) in new[] { ("u8", 1), ("u16", 2), ("u32", 4), ("u64", 8) })
        {
            var n = size;
            c.CreateFunction<string?, long?, long?>("blob_" + name,
                (hex, off) => hex is null || off is null ? null : Read(hex, (int)off, n), isDeterministic: true);
            c.CreateFunction<string?, long?, long?, string?>("blob_set_" + name,
                (hex, off, v) => hex is null || off is null || v is null ? null : Write(hex, (int)off, n, v.Value),
                isDeterministic: true);
        }
    }

    // character (code point) positions - SQLite's substr counts the same way, .NET's string indexes UTF-16 units
    private static int[] CodePointStarts(string x)
    {
        var at = new int[x.Length + 1];                     // UTF-16 index -> code points before it
        var n = 0;
        for (var i = 0; i < x.Length; i++)
        {
            at[i] = n;
            if (!(char.IsHighSurrogate(x[i]) && i + 1 < x.Length && char.IsLowSurrogate(x[i + 1]))) n++;
        }
        at[x.Length] = n;
        return at;
    }

    public static string Matches(string x, string p)
    {
        var cp = CodePointStarts(x);
        using var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartArray();
            foreach (System.Text.RegularExpressions.Match m in Rx(p).Matches(x))
            {
                w.WriteStartObject();
                w.WriteNumber("s", cp[m.Index]);
                w.WriteNumber("e", cp[m.Index + m.Length]);
                w.WriteString("m", m.Value);
                w.WriteStartArray("g");
                for (var g = 1; g < m.Groups.Count; g++)
                {
                    if (m.Groups[g].Success) w.WriteStringValue(m.Groups[g].Value);
                    else w.WriteNullValue();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string Splice(string x, string edits)
    {
        var cp = CodePointStarts(x);
        var utf16 = new int[cp[x.Length] + 1];              // code point position -> UTF-16 index
        for (var i = x.Length; i >= 0; i--) utf16[cp[i]] = i;
        using var doc = System.Text.Json.JsonDocument.Parse(edits);
        var list = doc.RootElement.EnumerateArray()
            .Select(e => (S: e[0].GetInt32(), E: e[1].GetInt32(), R: e[2].ValueKind == System.Text.Json.JsonValueKind.Null ? "" : e[2].ToString()))
            .OrderBy(e => e.S).ToList();
        var sb = new System.Text.StringBuilder();
        var at = 0;
        foreach (var (s0, e0, r) in list)
        {
            if (s0 < at || e0 < s0 || e0 > cp[x.Length])
                throw new ArgumentException($"text_splice: edit [{s0}, {e0}] overlaps another or lies outside the text");
            sb.Append(x, utf16[at], utf16[s0] - utf16[at]).Append(r);
            at = e0;
        }
        sb.Append(x, utf16[at], x.Length - utf16[at]);
        return sb.ToString();
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

    // a migration applies one pattern to thousands of rows: compiled once per pattern
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.RegularExpressions.Regex> Patterns = new();

    private static System.Text.RegularExpressions.Regex Rx(string pattern) =>
        Patterns.GetOrAdd(pattern, p => new System.Text.RegularExpressions.Regex(p, System.Text.RegularExpressions.RegexOptions.CultureInvariant));

    private static string? Group(string x, string pattern, int group)
    {
        var m = Rx(pattern).Match(x);
        return m.Success && group < m.Groups.Count && m.Groups[group].Success ? m.Groups[group].Value : null;
    }

    private static long? Read(string hex, int off, int size)
    {
        var b = Convert.FromHexString(hex);
        if (off < 0 || off + size > b.Length) return null;
        long v = 0;
        for (var i = size - 1; i >= 0; i--) v = (v << 8) | b[off + i];
        return v;
    }

    private static string? Compact(string hex, int off, int size, int count, string? drop)
    {
        var b = Convert.FromHexString(hex);
        if (off < 0 || size <= 0 || count < 0 || off + size * count > b.Length) return null;
        var gone = (drop ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToHashSet();
        var kept = Enumerable.Range(0, count).Where(k => !gone.Contains(k))
            .SelectMany(k => b.Skip(off + k * size).Take(size)).ToArray();
        Array.Clear(b, off, size * count);
        kept.CopyTo(b, off);
        return Convert.ToHexString(b);
    }

    private static string? Write(string hex, int off, int size, long value)
    {
        var b = Convert.FromHexString(hex);
        if (off < 0 || off + size > b.Length) return null;
        for (var i = 0; i < size; i++) b[off + i] = (byte)(value >> (8 * i));
        return Convert.ToHexString(b);
    }
}
