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
public sealed record TableDeclaration(string Name, string Like, string File, int Section, string? As);

/// <summary>
/// Runs one migration file with its comment directives (docs/DESIGN-variants-and-native-steps.md, section 2):
/// <list type="bullet">
/// <item><c>-- @param NAME = value</c>: a named constant; <c>:NAME</c> in the script (outside string literals) and in
/// the directive queries is replaced by <c>value</c> as written (a number, or a quoted SQL string).</item>
/// <item><c>-- @assert &lt;SELECT&gt;</c>: after the statements; any row returned fails the migration.</item>
/// <item><c>-- @report name &lt;SELECT&gt;</c>: after the statements (and the asserts); the result becomes
/// <c>&lt;reportDir&gt;/name.md</c>, a markdown table.</item>
/// <item><c>-- @table Name LIKE Template FILE file.txt [SECTION n] [AS InFileName]</c>: BEFORE the statements, an empty
/// table with the template's columns; the caller (a variant build) keeps it and builds it into that file. Refused
/// where no caller keeps it (<paramref name="onTable"/> null).</item>
/// </list>
/// A directive is one line. Returns the statements' affected-row count.
/// </summary>
public static class MigrationScript
{
    private static readonly Regex Directive = new(@"^\s*--\s*@(param|assert|report|table)\b\s*(.*)$", RegexOptions.Multiline);
    private static readonly Regex TableDef = new(
        @"^([A-Za-z_]\w*)\s+LIKE\s+([A-Za-z_]\w*)\s+FILE\s+(\S+)(?:\s+SECTION\s+(\d+))?(?:\s+AS\s+(\S+))?\s*$",
        RegexOptions.IgnoreCase);
    private static readonly Regex ParamDef = new(@"^([A-Za-z_]\w*)\s*=\s*(.+?)\s*$");
    private static readonly Regex ReportDef = new(@"^([\w.-]+)\s+(.+)$", RegexOptions.Singleline);

    public static int Run(ISqlEngine engine, string sql, string name, string? reportDir, Action<TableDeclaration>? onTable = null)
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
                        throw new FormatException($"{name}: bad @table '{arg}' (want Name LIKE Template FILE file.txt [SECTION n] [AS InFileName])");
                    tables.Add(new TableDeclaration(t.Groups[1].Value, t.Groups[2].Value, t.Groups[3].Value,
                        t.Groups[4].Success ? int.Parse(t.Groups[4].Value) : 0, t.Groups[5].Success ? t.Groups[5].Value : null));
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
            // the template's columns (and the _envs column), no rows
            engine.Execute($"CREATE TABLE [{t.Name}] AS SELECT * FROM [{t.Like}] WHERE 0");
            onTable!(t);
        }

        var body = Substitute(Directive.Replace(sql, ""), parms);
        var affected = HasStatements(body) ? Math.Max(0, engine.Execute(body)) : 0;

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

    /// <summary>Replace :NAME tokens outside '...' literals (and never a longer name that starts with NAME).</summary>
    public static string Substitute(string sql, IReadOnlyDictionary<string, string> parms)
    {
        if (parms.Count == 0) return sql;
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
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
