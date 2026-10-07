using System.Text;
using System.Text.RegularExpressions;

namespace Fiesta.Collab.Sql;

/// <summary>A migration's @assert query returned rows.</summary>
public sealed class MigrationAssertException(string message) : Exception(message);

/// <summary>
/// A table a migration creates with <c>-- @table Name LIKE Template FILE file.txt [SECTION n] [AS InFileName]</c>: the
/// template's columns and header, written to <c>file.txt</c> beside the template's own file, as section <c>n</c> (0) under
/// the in-file table name <c>InFileName</c> (the template's).
/// </summary>
public sealed record TableDeclaration(string Name, string Like, string File, int Section, string? As, string? Columns = null)
{
    /// <summary><c>COLUMNS a, b, c</c>: the file's column names, the template's columns in order (null = the template's own).
    /// The table keeps the template's names - the statements use them - and the build writes these.</summary>
    public IReadOnlyList<string>? ColumnNames => Columns?.Split(',');
}

/// <summary>
/// Runs one migration file with its comment directives (docs/DESIGN-variants-and-native-steps.md, section 2):
/// <list type="bullet">
/// <item><c>-- @param NAME = value</c>: a named constant; <c>:NAME</c> in the script (outside string literals) and in
/// the directive queries is replaced by <c>value</c> as written (a number, or a quoted SQL string).</item>
/// <item><c>-- @assert &lt;SELECT&gt;</c>: after the statements; any row returned fails the migration.</item>
/// <item><c>-- @report name &lt;SELECT&gt;</c>: after the statements (and the asserts); the result becomes
/// <c>&lt;reportDir&gt;/name.md</c>, a markdown table.</item>
/// <item><c>-- @table Name LIKE Template FILE file.txt [SECTION n] [AS InFileName] [COLUMNS a, b, ...]</c>: BEFORE the
/// statements, an empty table with the template's columns; the caller (a variant build) keeps it and builds it into that
/// file - with COLUMNS, the file's column names (one per template column, in order; the statements still use the
/// template's). Refused where no caller keeps it (<paramref name="onTable"/> null).</item>
/// <item><c>-- @copy Table WHERE &lt;condition&gt; [SET col = expr, ...]</c>: IN PLACE among the statements (they run in
/// order around it), a copy of every row of Table the condition picks - all columns, the hidden ones (_envs) too - with
/// the SET columns replaced by their expressions, evaluated against the copied row. Appended in the rows' order, or by a
/// trailing <c>ORDER BY expr</c> (outside parentheses). A SET expression may use window functions (ROW_NUMBER() OVER
/// ...) to give each copy its own id.</item>
/// <item><c>-- @each GLOB statement</c>: IN PLACE among the statements, the statement once per table whose name the
/// GLOB matches (sqlite_master, in name order), <c>{table}</c> replaced by that table's name - every shop table of a
/// layer (<c>QoL_*_Tab[0-9][0-9]</c>) without listing them.</item>
/// <item>A script naming <c>_id_registry</c> sees the project's id registry (<see cref="IdRegistry"/>) as that temp
/// table: allocate by INSERT, read by SELECT; new entries are written back to the registry file afterwards.</item>
/// </list>
/// A directive is one line. Returns the statements' affected-row count.
/// </summary>
public static class MigrationScript
{
    private static readonly Regex Directive = new(@"^\s*--\s*@(param|assert|report|table)\b\s*(.*)$", RegexOptions.Multiline);
    private static readonly Regex TableDef = new(
        @"^([A-Za-z_]\w*)\s+LIKE\s+([A-Za-z_]\w*)\s+FILE\s+(\S+)(?:\s+SECTION\s+(\d+))?(?:\s+AS\s+(\S+))?(?:\s+COLUMNS\s+(\w+(?:\s*,\s*\w+)*))?\s*$",
        RegexOptions.IgnoreCase);
    // the in-place directives: SQL in comment form, run among the statements at their line
    private static readonly Regex CopyLine = new(@"^\s*--\s*@(copy|each)\b\s*(.*)$", RegexOptions.Multiline);
    private static readonly Regex CopyDef = new(
        @"^""?([A-Za-z_]\w*)""?\s+WHERE\s+(.+?)(?:\s+SET\s+(.+))?\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex ParamDef =new(@"^([A-Za-z_]\w*)\s*=\s*(.+?)\s*$");
    private static readonly Regex ReportDef = new(@"^([\w.-]+)\s+(.+)$", RegexOptions.Singleline);

    public static int Run(ISqlEngine engine, string sql, string name, string? reportDir, Action<TableDeclaration>? onTable = null,
                          IdRegistry? registry = null)
    {
        var parms = new Dictionary<string, string>(StringComparer.Ordinal);
        var asserts = new List<string>();
        var reports = new List<(string Name, string Query)>();
        var tables = new List<TableDeclaration>();
        foreach (Match m in Directive.Matches(sql))
        {
            var arg = m.Groups[2].Value.Trim();
            switch (m.Groups[1].Value)
            {
                case "param":
                    var p = ParamDef.Match(arg);
                    if (!p.Success) throw new FormatException($"{name}: bad @param '{arg}' (want NAME = value)");
                    parms[p.Groups[1].Value] = p.Groups[2].Value;
                    break;
                case "assert":
                    asserts.Add(arg);
                    break;
                case "table":
                    var t = TableDef.Match(arg);
                    if (!t.Success)
                        throw new FormatException($"{name}: bad @table '{arg}' (want Name LIKE Template FILE file.txt [SECTION n] [AS InFileName] [COLUMNS a, b, ...])");
                    tables.Add(new TableDeclaration(t.Groups[1].Value, t.Groups[2].Value, t.Groups[3].Value,
                        t.Groups[4].Success ? int.Parse(t.Groups[4].Value) : 0, t.Groups[5].Success ? t.Groups[5].Value : null,
                        t.Groups[6].Success ? Regex.Replace(t.Groups[6].Value, @"\s+", "") : null));
                    break;
                case "report":
                    var r = ReportDef.Match(arg);
                    if (!r.Success) throw new FormatException($"{name}: bad @report '{arg}' (want name SELECT ...)");
                    reports.Add((r.Groups[1].Value, r.Groups[2].Value));
                    break;
            }
        }

        if (tables.Count > 0 && onTable is null)
            throw new NotSupportedException($"{name}: @table creates a table only a variant build keeps (fiesta build --variant)");
        foreach (var t in tables)
        {
            if (t.ColumnNames is { } names)
            {
                var have = engine.Query($"SELECT name FROM pragma_table_info('{t.Like.Replace("'", "''")}')")
                    .Select(r => (string)r["name"]!).Count(c => c != SqlEngine.EnvsColumn);
                if (have != names.Count)
                    throw new FormatException($"{name}: @table {t.Name} COLUMNS names {names.Count} column(s), {t.Like} has {have}");
            }
            // the template's columns (and the _envs column), no rows
            engine.Execute($"CREATE TABLE [{t.Name}] AS SELECT * FROM [{t.Like}] WHERE 0");
            onTable!(t);
        }

        var useRegistry = IdRegistry.UsedBy(sql);
        if (useRegistry)
        {
            if (registry is null)
                throw new NotSupportedException($"{name}: uses {IdRegistry.TableName} but the project has no id registry (fiesta.json \"idRegistry\" / $FIESTA_ID_REGISTRY)");
            registry.Expose(engine);
        }

        // the statements, split at each -- @copy line: the parts run in order with the copies between them
        var body = Substitute(Directive.Replace(sql, ""), parms);
        var affected = 0;
        var at = 0;
        foreach (Match c in CopyLine.Matches(body))
        {
            var part = body[at..c.Index];
            if (HasStatements(part)) affected += Math.Max(0, engine.Execute(part));
            affected += c.Groups[1].Value == "copy" ? Copy(engine, c.Groups[2].Value.Trim(), name)
                                                    : Each(engine, c.Groups[2].Value.Trim(), name);
            at = c.Index + c.Length;
        }
        var rest = body[at..];
        if (HasStatements(rest)) affected += Math.Max(0, engine.Execute(rest));

        if (useRegistry)
            registry!.Collect(engine, name);

        foreach (var a in asserts)
        {
            var rows = engine.Query(Substitute(a, parms));
            if (rows.Count > 0)
                throw new MigrationAssertException(
                    $"{name}: @assert returned {rows.Count} row(s): {a}\n" + Markdown(rows.Take(20).ToList()));
        }
        foreach (var (rname, q) in reports)
        {
            if (reportDir is null) continue;
            Directory.CreateDirectory(reportDir);
            var rows = engine.Query(Substitute(q, parms));
            File.WriteAllText(Path.Combine(reportDir, rname + ".md"), $"# {rname}\n\nFrom `{name}`, {rows.Count} row(s).\n\n" + Markdown(rows));
        }
        return affected;
    }

    /// <summary>One <c>@copy</c>: INSERT INTO T (every column) SELECT (each column, or its SET expression) FROM T WHERE ...</summary>
    private static int Copy(ISqlEngine engine, string arg, string name)
    {
        // an optional trailing ORDER BY (outside parentheses and literals): the order the copies are appended in
        var orderBy = "rowid";
        var ob = TopLevelIndex(arg, " ORDER BY ");
        if (ob >= 0)
        {
            orderBy = arg[(ob + " ORDER BY ".Length)..].Trim();
            arg = arg[..ob];
        }
        var m = CopyDef.Match(arg);
        if (!m.Success) throw new FormatException($"{name}: bad @copy '{arg}' (want Table WHERE condition [SET col = expr, ...] [ORDER BY expr])");
        var table = m.Groups[1].Value;
        var cols = engine.Query($"SELECT name FROM pragma_table_info('{table.Replace("'", "''")}')")
            .Select(r => (string)r["name"]!).ToList();
        if (cols.Count == 0) throw new InvalidOperationException($"{name}: @copy: no table {table}");
        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (m.Groups[3].Success)
            foreach (var a in SplitTopLevel(m.Groups[3].Value))
            {
                var eq = a.IndexOf('=');
                if (eq <= 0) throw new FormatException($"{name}: @copy SET '{a}' (want col = expr)");
                var col = a[..eq].Trim().Trim('"', '[', ']');
                if (!cols.Contains(col, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{name}: @copy {table}: no column {col}");
                set[col] = a[(eq + 1)..].Trim();
            }
        var list = string.Join(", ", cols.Select(c => $"[{c}]"));
        var select = string.Join(", ", cols.Select(c => set.TryGetValue(c, out var e) ? $"({e})" : $"[{c}]"));
        return engine.Execute($"INSERT INTO [{table}] ({list}) SELECT {select} FROM [{table}] WHERE {m.Groups[2].Value} ORDER BY {orderBy}");
    }

    /// <summary>One <c>@each GLOB statement</c>: the statement once per table whose name the GLOB matches (by name),
    /// <c>{table}</c> replaced by that name.</summary>
    private static int Each(ISqlEngine engine, string arg, string name)
    {
        var sp = arg.IndexOfAny([' ', '\t']);
        var stmt = sp < 0 ? "" : arg[(sp + 1)..].Trim();
        if (sp < 0 || !stmt.Contains("{table}"))
            throw new FormatException($"{name}: bad @each '{arg}' (want GLOB statement-naming-{{table}})");
        var glob = arg[..sp];
        var tables = engine.Query($"SELECT name FROM sqlite_master WHERE type = 'table' AND name GLOB '{glob.Replace("'", "''")}' ORDER BY name")
            .Select(r => (string)r["name"]!).ToList();
        return tables.Sum(t => Math.Max(0, engine.Execute(stmt.Replace("{table}", t))));
    }

    /// <summary>The tables among <paramref name="tables"/> an <c>@each</c> line of <paramref name="sql"/> matches (a
    /// session loads them: the script names none of them) - with <paramref name="writtenOnly"/>, only where its statement
    /// UPDATEs / INSERTs INTO / DELETEs FROM / REPLACEs INTO <c>{table}</c> (they are saved back).</summary>
    public static IEnumerable<string> EachTables(string sql, IEnumerable<string> tables, bool writtenOnly)
    {
        var all = tables.ToList();
        foreach (Match m in CopyLine.Matches(sql))
        {
            if (m.Groups[1].Value != "each") continue;
            var arg = m.Groups[2].Value.Trim();
            var sp = arg.IndexOfAny([' ', '\t']);
            if (sp < 0) continue;
            if (writtenOnly && !Regex.IsMatch(arg[sp..], @"\b(?:UPDATE|INSERT\s+(?:OR\s+\w+\s+)?INTO|DELETE\s+FROM|REPLACE\s+INTO)\s+""?\{table\}",
                    RegexOptions.IgnoreCase)) continue;
            var rx = new Regex("^" + Regex.Escape(arg[..sp]).Replace(@"\*", ".*").Replace(@"\?", ".").Replace(@"\[", "[") + "$");
            foreach (var t in all.Where(t => rx.IsMatch(t))) yield return t;
        }
    }

    /// <summary>The last index of <paramref name="word"/> (case-insensitive) outside parentheses and '...' literals, or -1.</summary>
    private static int TopLevelIndex(string s, string word)
    {
        int depth = 0, found = -1;
        var quoted = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\'') quoted = !quoted;
            else if (!quoted && c == '(') depth++;
            else if (!quoted && c == ')') depth--;
            else if (!quoted && depth == 0 && string.Compare(s, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0)
                found = i;
        }
        return found;
    }

    /// <summary>Splits at commas outside parentheses and '...' literals.</summary>
    private static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        var quoted = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\'') quoted = !quoted;
            else if (!quoted && c == '(') depth++;
            else if (!quoted && c == ')') depth--;
            else if (!quoted && depth == 0 && c == ',')
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
        }
        parts.Add(s[start..]);
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    /// <summary>Replace :NAME tokens outside '...' literals (and never a longer name that starts with NAME).</summary>
    public static string Substitute(string sql, IReadOnlyDictionary<string, string> parms)
    {
        if (parms.Count == 0) return sql;
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            // a comment is copied as it is: an apostrophe in one ("the client's list") must not open a string literal and
            // leave every later :NAME unsubstituted (found 2026-10-07: "Must add values for the following parameters").
            // A -- @copy / -- @each line is SQL in comment form - it goes through as code.
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var eol = sql.IndexOf('\n', i);
                if (eol < 0) eol = sql.Length;
                if (!CopyLine.IsMatch(sql[i..eol]))
                {
                    sb.Append(sql, i, eol - i);
                    i = eol;
                    continue;
                }
                sb.Append("--");
                i += 2;
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? sql.Length : end + 2;
                sb.Append(sql, i, end - i);
                i = end;
                continue;
            }
            if (c == '\'')
            {
                var j = i + 1;
                while (j < sql.Length && !(sql[j] == '\'' && (j + 1 >= sql.Length || sql[j + 1] != '\''))) j += sql[j] == '\'' ? 2 : 1;
                sb.Append(sql, i, Math.Min(j + 1, sql.Length) - i);
                i = j + 1;
                continue;
            }
            if (c == ':' && i + 1 < sql.Length && (char.IsLetter(sql[i + 1]) || sql[i + 1] == '_') && (i == 0 || !IsWord(sql[i - 1])))
            {
                var j = i + 1;
                while (j < sql.Length && IsWord(sql[j])) j++;
                var key = sql[(i + 1)..j];
                if (parms.TryGetValue(key, out var v)) { sb.Append(v); i = j; continue; }
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool HasStatements(string sql)
        => sql.Split('\n').Select(l => l.Trim()).Any(l => l.Length > 0 && !l.StartsWith("--"));

    private static string Markdown(IReadOnlyList<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return "(no rows)\n";
        var cols = rows[0].Keys.ToList();
        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", cols)).Append(" |\n");
        sb.Append('|').Append(string.Concat(cols.Select(_ => "---|"))).Append('\n');
        foreach (var r in rows)
            sb.Append("| ").Append(string.Join(" | ", cols.Select(k => Convert.ToString(r[k], System.Globalization.CultureInfo.InvariantCulture)?.Replace("|", "\\|") ?? ""))).Append(" |\n");
        return sb.ToString();
    }
}
