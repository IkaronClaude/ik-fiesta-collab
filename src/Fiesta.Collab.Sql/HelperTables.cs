using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fiesta.Collab.Sql;

/// <summary>
/// HELPER TABLES: working tables a migration keeps for LATER migrations - never built into a game file.
/// <list type="bullet">
/// <item><c>-- @helper Name</c> in a migration: the table <c>Name</c> its own SQL creates (or fills) is kept. A variant
/// build's session writes it to the project (<c>data/helpers/Name.json</c>, listed under fiesta.json <c>"helpers"</c>), so a
/// later step - and a <c>--from</c> rerun restored from the project - can read it; a plain <c>fiesta migrate</c> runs every
/// migration in one engine, where the table simply stays. A helper is a plain SQLite table: its declared column types and its
/// rows, nothing of a game table (no environments, no file).</item>
/// <item><c>_build</c>: a read-only table <c>(key, value)</c> a migration sees when it names it - what is being built
/// (<c>environment</c>: the variant, e.g. <c>rebalanced</c>; <c>layer</c>; <c>step</c>: this migration's file name), for
/// conditional edits: <c>WHERE (SELECT value FROM _build WHERE key = 'environment') = 'rebalanced'</c>.</item>
/// </list>
/// </summary>
public static class HelperTables
{
    public const string BuildTable = "_build";
    private static readonly Regex HelperLine = new(@"^\s*--\s*@helper\s+""?([A-Za-z_]\w*)""?\s*$", RegexOptions.Multiline);

    /// <summary>The helpers a migration declares (<c>-- @helper Name</c>), in order.</summary>
    public static List<string> Declared(string sql) => HelperLine.Matches(sql).Select(m => m.Groups[1].Value).Distinct().ToList();

    public static bool Exists(ISqlEngine engine, string name) =>
        engine.Query($"SELECT 1 AS x FROM sqlite_master WHERE type = 'table' AND name = '{name.Replace("'", "''")}'").Count > 0;

    /// <summary>(Re)creates <c>temp._build</c> with these rows.</summary>
    public static void ExposeBuild(ISqlEngine engine, IReadOnlyDictionary<string, string> vars)
    {
        engine.Execute($"DROP TABLE IF EXISTS temp.[{BuildTable}]");
        engine.Execute($"CREATE TEMP TABLE [{BuildTable}] (key TEXT PRIMARY KEY, value TEXT)");
        engine.ExecuteMany($"INSERT INTO temp.[{BuildTable}] (key, value) VALUES (@k, @v)",
            vars.Select(kv => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["@k"] = kv.Key, ["@v"] = kv.Value }));
    }

    /// <summary>A helper as JSON: <c>{"columns": [{"name", "type"}], "rows": [[...], ...]}</c> (rows in rowid order).</summary>
    public static string ToJson(ISqlEngine engine, string name)
    {
        var q = name.Replace("'", "''");
        var cols = engine.Query($"SELECT name, type FROM pragma_table_info('{q}') ORDER BY cid")
            .Select(r => (Name: (string)r["name"]!, Type: (string?)r["type"] ?? "")).ToList();
        if (cols.Count == 0) throw new InvalidOperationException($"@helper {name}: the migration did not create it");
        var (_, rows) = engine.QueryRows($"SELECT {string.Join(", ", cols.Select(c => $"[{c.Name}]"))} FROM [{name}] ORDER BY rowid",
            new Dictionary<string, object?>());
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartArray("columns");
            foreach (var c in cols)
            {
                w.WriteStartObject();
                w.WriteString("name", c.Name);
                w.WriteString("type", c.Type);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("rows");
            foreach (var row in rows)
            {
                w.WriteStartArray();
                foreach (var v in row)
                {
                    switch (v)
                    {
                        case null: w.WriteNullValue(); break;
                        case long l: w.WriteNumberValue(l); break;
                        case double d: w.WriteNumberValue(d); break;
                        case byte[] b: w.WriteStartObject(); w.WriteString("blob", Convert.ToHexString(b)); w.WriteEndObject(); break;
                        default: w.WriteStringValue(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
                    }
                }
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Creates the helper <paramref name="name"/> from its JSON (<see cref="ToJson"/>), replacing one of that name.</summary>
    public static void FromJson(ISqlEngine engine, string name, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var cols = doc.RootElement.GetProperty("columns").EnumerateArray()
            .Select(c => (Name: c.GetProperty("name").GetString()!, Type: c.GetProperty("type").GetString() ?? "")).ToList();
        engine.Execute($"DROP TABLE IF EXISTS [{name}]");
        engine.Execute($"CREATE TABLE [{name}] ({string.Join(", ", cols.Select(c => $"[{c.Name}] {c.Type}".TrimEnd()))})");
        var names = string.Join(", ", cols.Select(c => $"[{c.Name}]"));
        var ps = string.Join(", ", cols.Select((_, i) => $"@p{i}"));
        engine.ExecuteMany($"INSERT INTO [{name}] ({names}) VALUES ({ps})",
            doc.RootElement.GetProperty("rows").EnumerateArray().Select(r =>
            {
                var d = new Dictionary<string, object?>();
                var i = 0;
                foreach (var v in r.EnumerateArray())
                {
                    d[$"@p{i++}"] = v.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
                        JsonValueKind.Object => Convert.FromHexString(v.GetProperty("blob").GetString()!),
                        _ => v.GetString()
                    };
                }
                return (IReadOnlyDictionary<string, object?>)d;
            }));
    }
}
