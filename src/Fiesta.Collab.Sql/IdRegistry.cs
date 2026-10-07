using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Fiesta.Collab.Sql;

/// <summary>
/// Ids a project hands out ONCE and keeps forever (docs/DESIGN-variants-and-native-steps.md 6a): characters store item ids
/// and buff handles in the live database, so an id given to a new item must never move. The registry is a JSON file -
/// <c>{"kind": {"key": id | [id, ...]}, ...}</c>, other top-level keys (e.g. <c>_doc</c>) kept as they are - and a
/// migration sees it as the temp table <c>_id_registry(kind, key, idx, value)</c>: a scalar entry is idx 0, a list entry
/// one row per element. A migration ALLOCATES by inserting rows; entries are never changed or removed (refused), and new
/// rows are written back to the file in the order they were inserted.
/// <para>The file is written the way Python's <c>json.dump(..., indent=1)</c> writes it (Fiesta2026on2016's variant_lib
/// keeps writing the same file), so a run that adds nothing leaves it byte for byte unchanged.</para>
/// </summary>
public sealed class IdRegistry
{
    public const string TableName = "_id_registry";

    private readonly string _path;
    private readonly List<(string Key, JsonElement Value)> _other = [];          // non-registry top-level keys, in order
    private readonly List<string> _keyOrder = [];                                 // every top-level key, in file order
    private readonly Dictionary<string, List<(string Key, List<long> Values, bool IsList)>> _kinds = new(StringComparer.Ordinal);

    private IdRegistry(string path) => _path = path;

    public string Path => _path;

    /// <summary>The project's registry: $FIESTA_ID_REGISTRY, else fiesta.json "idRegistry" (relative to the project), else none.</summary>
    public static IdRegistry? ForProject(string projectDir, string? configured)
    {
        var env = Environment.GetEnvironmentVariable("FIESTA_ID_REGISTRY");
        var path = !string.IsNullOrWhiteSpace(env) ? env
            : !string.IsNullOrWhiteSpace(configured) ? System.IO.Path.Combine(projectDir, configured) : null;
        return path == null ? null : Load(path);
    }

    public static IdRegistry Load(string path)
    {
        var reg = new IdRegistry(path);
        if (!File.Exists(path)) return reg;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            reg._keyOrder.Add(prop.Name);
            if (prop.Name.StartsWith('_') || prop.Value.ValueKind != JsonValueKind.Object)
            {
                reg._other.Add((prop.Name, prop.Value.Clone()));
                continue;
            }
            var entries = new List<(string, List<long>, bool)>();
            foreach (var e in prop.Value.EnumerateObject())
            {
                if (e.Value.ValueKind == JsonValueKind.Number)
                    entries.Add((e.Name, [e.Value.GetInt64()], false));
                else if (e.Value.ValueKind == JsonValueKind.Array)
                    entries.Add((e.Name, e.Value.EnumerateArray().Select(x => x.GetInt64()).ToList(), true));
                else
                    throw new FormatException($"{path}: {prop.Name}.{e.Name} is neither an id nor a list of ids");
            }
            reg._kinds[prop.Name] = entries;
        }
        return reg;
    }

    /// <summary>Whether a script uses the registry (only then is the temp table built).</summary>
    public static bool UsedBy(string sql) => sql.Contains(TableName, StringComparison.OrdinalIgnoreCase);

    /// <summary>(Re)creates the temp table from the file's entries.</summary>
    public void Expose(ISqlEngine engine)
    {
        engine.Execute($"DROP TABLE IF EXISTS temp.[{TableName}]");
        engine.Execute($"CREATE TEMP TABLE [{TableName}] (kind TEXT NOT NULL, key TEXT NOT NULL, idx INTEGER NOT NULL, " +
                       "value INTEGER NOT NULL, PRIMARY KEY (kind, key, idx))");
        var rows = _kinds.SelectMany(k => k.Value.SelectMany(e => e.Values.Select((v, i) =>
            (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["@kind"] = k.Key, ["@key"] = e.Key, ["@idx"] = (long)i, ["@value"] = v
            })));
        engine.ExecuteMany($"INSERT INTO temp.[{TableName}] (kind, key, idx, value) VALUES (@kind, @key, @idx, @value)", rows);
    }

    /// <summary>
    /// Reads the temp table back: every existing entry must be unchanged; new rows are appended (per kind, per key, in
    /// insertion order) and the file is rewritten. Returns the number of new entries.
    /// </summary>
    public int Collect(ISqlEngine engine, string scriptName)
    {
        var (_, rows) = engine.QueryRows($"SELECT kind, key, idx, value FROM temp.[{TableName}] ORDER BY rowid",
            new Dictionary<string, object?>());
        var seen = rows.Select(r => (Kind: (string)r[0]!, Key: (string)r[1]!, Idx: Convert.ToInt32(r[2], CultureInfo.InvariantCulture),
                                     Value: Convert.ToInt64(r[3], CultureInfo.InvariantCulture))).ToList();
        var have = seen.ToDictionary(r => (r.Kind, r.Key, r.Idx), r => r.Value);
        foreach (var (kind, entries) in _kinds)
            foreach (var (key, values, _) in entries)
                for (var i = 0; i < values.Count; i++)
                {
                    if (!have.TryGetValue((kind, key, i), out var v))
                        throw new InvalidOperationException($"{scriptName}: {TableName} lost {kind}.{key}[{i}] - registry ids are forever");
                    if (v != values[i])
                        throw new InvalidOperationException($"{scriptName}: {TableName} changed {kind}.{key}[{i}] {values[i]} -> {v} - registry ids are forever");
                }

        var known = _kinds.SelectMany(k => k.Value.Select(e => (k.Key, e.Key))).ToHashSet();
        var added = seen.Where(r => !known.Contains((r.Kind, r.Key))).ToList();
        if (added.Count == 0) return 0;
        foreach (var g in added.GroupBy(r => (r.Kind, r.Key)))
        {
            var vals = g.OrderBy(r => r.Idx).ToList();
            if (vals.Select((r, i) => r.Idx != i).Any(b => b))
                throw new InvalidOperationException($"{scriptName}: {TableName} {g.Key.Kind}.{g.Key.Key} has idx {string.Join(",", vals.Select(r => r.Idx))} - want 0..n-1");
            if (!_kinds.TryGetValue(g.Key.Kind, out var list))
            {
                _kinds[g.Key.Kind] = list = [];
                _keyOrder.Add(g.Key.Kind);
            }
            // the kind's shape: a kind already holding lists stays a list kind; otherwise a single idx-0 row is a scalar
            var isList = list.Any(e => e.IsList) || vals.Count > 1;
            list.Add((g.Key.Key, vals.Select(r => r.Value).ToList(), isList));
        }
        Save();
        return added.Select(r => (r.Kind, r.Key)).Distinct().Count();
    }

    private void Save()
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        var first = true;
        foreach (var top in _keyOrder)
        {
            if (!first) sb.Append(",\n");
            first = false;
            sb.Append(' ').Append(PyString(top)).Append(": ");
            if (_kinds.TryGetValue(top, out var entries))
            {
                if (entries.Count == 0) { sb.Append("{}"); continue; }
                sb.Append("{\n");
                for (var i = 0; i < entries.Count; i++)
                {
                    var (key, values, isList) = entries[i];
                    sb.Append("  ").Append(PyString(key)).Append(": ");
                    if (!isList) sb.Append(values[0].ToString(CultureInfo.InvariantCulture));
                    else if (values.Count == 0) sb.Append("[]");
                    else
                    {
                        sb.Append("[\n");
                        sb.Append(string.Join(",\n", values.Select(v => "   " + v.ToString(CultureInfo.InvariantCulture))));
                        sb.Append("\n  ]");
                    }
                    sb.Append(i + 1 < entries.Count ? ",\n" : "\n");
                }
                sb.Append(" }");
            }
            else
            {
                WritePy(sb, _other.First(o => o.Key == top).Value, 1);
            }
        }
        sb.Append("\n}");
        File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
    }

    // Python json.dump(indent=1) of an arbitrary value nested `level` deep
    private static void WritePy(StringBuilder sb, JsonElement v, int level)
    {
        var pad = new string(' ', level + 1);
        var close = new string(' ', level);
        switch (v.ValueKind)
        {
            case JsonValueKind.Object:
                var props = v.EnumerateObject().ToList();
                if (props.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                for (var i = 0; i < props.Count; i++)
                {
                    sb.Append(pad).Append(PyString(props[i].Name)).Append(": ");
                    WritePy(sb, props[i].Value, level + 1);
                    sb.Append(i + 1 < props.Count ? ",\n" : "\n");
                }
                sb.Append(close).Append('}');
                return;
            case JsonValueKind.Array:
                var items = v.EnumerateArray().ToList();
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (var i = 0; i < items.Count; i++)
                {
                    sb.Append(pad);
                    WritePy(sb, items[i], level + 1);
                    sb.Append(i + 1 < items.Count ? ",\n" : "\n");
                }
                sb.Append(close).Append(']');
                return;
            case JsonValueKind.String:
                sb.Append(PyString(v.GetString()!));
                return;
            default:
                sb.Append(v.GetRawText());
                return;
        }
    }

    // json.dumps of a string with ensure_ascii=True
    private static string PyString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
