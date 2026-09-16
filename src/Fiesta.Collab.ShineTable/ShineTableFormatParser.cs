using System.Text.Json;
using Fiesta.Collab.Core.Models;

namespace Fiesta.Collab.ShineTable;

/// <summary>
/// Parses the #table/#columntype/#columnname/#record text format.
/// One file can contain multiple tables. Tab-separated values.
/// </summary>
internal static class ShineTableFormatParser
{
    public static List<string> Write(IReadOnlyList<TableEntry> tables)
    {
        var lines = new List<string>();

        // The preprocessor directives are file-level: every table parsed out of one file carries the same
        // list, so they are emitted once, before the first #table, exactly as they were read.
        var directives = tables.Select(DirectivesOf).FirstOrDefault(d => d.Length > 0) ?? [];
        lines.AddRange(directives);

        // Reading turned '#' into a space; writing has to turn it back, or a space-delimited row gains a
        // field. Built from the same directive lines so the two directions cannot drift apart.
        var encoder = new Preprocessor();
        foreach (var d in directives)
        {
            if (d.StartsWith("#exchange", StringComparison.OrdinalIgnoreCase)) encoder.ParseExchange(d);
            else if (d.StartsWith("#ignore", StringComparison.OrdinalIgnoreCase)) encoder.ParseIgnore(d);
        }

        foreach (var table in tables)
        {
            var meta = table.Schema.Metadata;
            string tableName = meta?.TryGetValue("tableName", out var tn) == true
                ? ToStr(tn) : table.Schema.TableName;

            lines.Add($"#table\t{tableName}");
            lines.Add("#columntype\t" + string.Join('\t', table.Schema.Columns.Select(MapTypeBack)));
            lines.Add("#columnname\t" + string.Join('\t', table.Schema.Columns.Select(c => c.Name)));

            foreach (var row in table.Rows)
            {
                var fields = table.Schema.Columns.Select(col =>
                {
                    var val = row.TryGetValue(col.Name, out var v) ? v : null;
                    // No re-encoding: the stored value is already in the file's own form, because reading
                    // no longer decodes #exchange. See Preprocessor.Apply for why that has to be so.
                    return FormatValue(val, col.Type);
                });
                lines.Add("#record\t" + string.Join('\t', fields));
            }

            lines.Add(""); // blank line between tables
        }

        lines.Add("#End");
        return lines;
    }

    /// <summary>The raw preprocessor lines stored on a table by the parser, if any.</summary>
    private static string[] DirectivesOf(TableEntry table)
    {
        if (table.Schema.Metadata?.TryGetValue("directives", out var d) != true) return [];
        return d switch
        {
            string[] a => a,
            IEnumerable<object> e => e.Select(x => ToStr(x)).Where(x => x.Length > 0).ToArray(),
            JsonElement { ValueKind: JsonValueKind.Array } je =>
                je.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray(),
            _ => [],
        };
    }

    private static string MapTypeBack(ColumnDefinition col) => col.Type switch
    {
        ColumnType.Byte => "BYTE",
        ColumnType.UInt16 => "WORD",
        ColumnType.Int32 or ColumnType.UInt32 => "DWRD",
        ColumnType.Float => "FLOAT",
        // SourceTypeCode == 0 means the original source used the "INDEX" keyword explicitly.
        // Any other string column (SourceTypeCode == explicit length, or null) writes STRING[N].
        ColumnType.String when col.SourceTypeCode == 0 => "INDEX",
        ColumnType.String => $"STRING[{col.Length}]",
        _ => $"STRING[{col.Length}]"
    };

    // An absent or empty string writes an EMPTY field, not a dash. Both mean "none" to the loader - the
    // reader maps "-" and "" alike to nothing - but they are not the same text, and substituting the dash
    // invented content the source never had: Scenario.txt ships Marlone14 with an empty ScrString and got
    // a literal "-" written into it. A value that really is "-" still reads back as "-" and is unaffected.
    private static string FormatValue(object? val, ColumnType type)
    {
        if (val is null or DBNull) return type == ColumnType.String ? "" : "0";
        if (val is JsonElement je) val = UnboxJsonElement(je);
        var s = val.ToString() ?? "";
        if (s.Length == 0) return type == ColumnType.String ? "" : "0";
        return s;
    }

    private static object UnboxJsonElement(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.Number when je.TryGetInt64(out var l) => l,
        JsonValueKind.Number => je.GetDouble(),
        JsonValueKind.String => je.GetString() ?? "",
        _ => je.ToString()
    };

    private static string ToStr(object val) => val is JsonElement je ? je.GetString() ?? val.ToString()! : val.ToString()!;

    public static List<TableEntry> Parse(string filePath, string[] lines)
    {
        var tables = new List<TableEntry>();
        // Keyed by raw table name (from metadata["tableName"]) for #RecordIn lookup
        var tablesByName = new Dictionary<string, TableEntry>(StringComparer.OrdinalIgnoreCase);
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        var preprocessor = new Preprocessor();
        int sectionIndex = 0;

        int i = 0;
        while (i < lines.Length)
        {
            string raw = lines[i].Trim();

            // Skip empty lines and comments
            if (raw.Length == 0 || raw.StartsWith(';'))
            {
                i++;
                continue;
            }

            // Preprocessor directives
            if (raw.StartsWith("#ignore", StringComparison.OrdinalIgnoreCase))
            {
                preprocessor.ParseIgnore(raw);
                preprocessor.Remember(raw);
                i++;
                continue;
            }

            if (raw.StartsWith("#exchange", StringComparison.OrdinalIgnoreCase))
            {
                preprocessor.ParseExchange(raw);
                preprocessor.Remember(raw);
                i++;
                continue;
            }

            // #delimiter \x20 (some files spell it #delimeter): extra field separator besides tab
            if (raw.StartsWith("#delimiter", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("#delimeter", StringComparison.OrdinalIgnoreCase))
            {
                preprocessor.ParseDelimiter(raw);
                preprocessor.Remember(raw);
                i++;
                continue;
            }

            // Table start
            if (raw.StartsWith("#table", StringComparison.OrdinalIgnoreCase))
            {
                var (table, nextLine) = ParseTable(filePath, fileName, lines, i, preprocessor);
                if (table != null)
                {
                    table.Schema.Metadata!["sectionIndex"] = sectionIndex++;
                    tables.Add(table);
                    if (table.Schema.Metadata.TryGetValue("tableName", out var tn))
                        tablesByName[tn.ToString()!] = table;
                }
                i = nextLine;
                continue;
            }

            // #RecordIn TABLE_NAME field1 field2... - file-level rows routed to a named table
            if (raw.StartsWith("#recordin", StringComparison.OrdinalIgnoreCase))
            {
                var parts = SplitFields(raw, preprocessor);
                if (parts.Length >= 2 && tablesByName.TryGetValue(parts[1], out var target))
                {
                    var cols = target.Schema.Columns;
                    var row = new Dictionary<string, object?>(cols.Count);
                    for (int c = 0; c < cols.Count && c + 2 < parts.Length; c++)
                    {
                        string field = parts[c + 2];   // stored exactly as written; see Preprocessor.Apply
                        row[cols[c].Name] = ConvertValue(field, cols[c].Type);
                    }
                    ((List<Dictionary<string, object?>>)target.Rows).Add(row);
                }
                i++;
                continue;
            }

            // #end at file level
            if (raw.StartsWith("#end", StringComparison.OrdinalIgnoreCase))
                break;

            i++;
        }

        return tables;
    }

    private static (TableEntry? table, int nextLine) ParseTable(
        string filePath, string fileName, string[] lines, int startLine, Preprocessor preprocessor)
    {
        // Parse #table line: "#table TableName" or "#table TableName ;comment"
        string tableLine = lines[startLine].Trim();
        string[] tableParts = SplitFields(tableLine, preprocessor);
        string tableName = tableParts.Length > 1 ? tableParts[1] : "Unknown";

        List<ColumnDefinition>? columns = null;
        List<string>? columnNames = null;
        List<string>? columnTypes = null;
        var rows = new List<Dictionary<string, object?>>();

        int i = startLine + 1;
        while (i < lines.Length)
        {
            string raw = lines[i].Trim();

            if (raw.Length == 0 || raw.StartsWith(';'))
            {
                i++;
                continue;
            }

            string lower = raw.ToLowerInvariant();

            // Next table, end of file, or file-level #RecordIn section
            if (lower.StartsWith("#table") || lower.StartsWith("#end") || lower.StartsWith("#recordin"))
                break;

            if (lower.StartsWith("#columntype"))
            {
                columnTypes = SplitFields(raw, preprocessor).Skip(1).ToList();
                i++;
                continue;
            }

            if (lower.StartsWith("#columnname"))
            {
                columnNames = SplitFields(raw, preprocessor).Skip(1).ToList();
                i++;
                continue;
            }

            if (lower.StartsWith("#record"))
            {
                // Build column definitions on first record if we haven't yet
                columns ??= BuildColumns(columnTypes, columnNames);

                var fields = SplitFields(raw, preprocessor).Skip(1).ToList();
                var row = new Dictionary<string, object?>(columns.Count);

                for (int c = 0; c < columns.Count && c < fields.Count; c++)
                {
                    string field = fields[c];      // stored exactly as written; see Preprocessor.Apply
                    row[columns[c].Name] = ConvertValue(field, columns[c].Type);
                }

                rows.Add(row);
                i++;
                continue;
            }

            // Skip unknown directives
            i++;
        }

        columns ??= BuildColumns(columnTypes, columnNames);
        if (columns.Count == 0)
            return (null, i);

        var schema = new TableSchema
        {
            TableName = $"{fileName}_{tableName}",
            SourceFormat = "shinetable",
            Columns = columns,
            Metadata = new Dictionary<string, object>
            {
                ["sourceFile"] = Path.GetFileName(filePath),
                ["tableName"] = tableName,
                ["format"] = "table",
                // Re-emitted by Write, and the source of the inverse #exchange it applies to every value.
                ["directives"] = preprocessor.Directives.ToArray()
            }
        };

        return (new TableEntry { Schema = schema, Rows = rows }, i);
    }

    private static List<ColumnDefinition> BuildColumns(List<string>? types, List<string>? names)
    {
        int count = Math.Max(types?.Count ?? 0, names?.Count ?? 0);
        if (count == 0) return [];

        var columns = new List<ColumnDefinition>(count);
        for (int i = 0; i < count; i++)
        {
            string typeStr = (types != null && i < types.Count) ? types[i] : "STRING[32]";
            string name = (names != null && i < names.Count) ? names[i] : $"Col{i}";
            var (colType, length, sourceTypeCode) = MapColumnType(typeStr);

            columns.Add(new ColumnDefinition
            {
                Name = name,
                Type = colType,
                Length = length,
                SourceTypeCode = sourceTypeCode
            });
        }

        return columns;
    }

    /// <summary>
    /// Maps a ShineTable column type string to a ColumnDefinition type + length.
    /// SourceTypeCode convention: 0 = explicitly "INDEX", positive N = "STRING[N]", null = other types.
    /// </summary>
    private static (ColumnType type, int length, int? sourceTypeCode) MapColumnType(string typeStr)
    {
        string upper = typeStr.ToUpperInvariant();

        if (upper == "INDEX")
            return (ColumnType.String, 32, 0); // 0 = INDEX sentinel

        if (upper.StartsWith("STRING"))
        {
            int length = 32; // default
            int bracket = typeStr.IndexOf('[');
            if (bracket >= 0)
            {
                int end = typeStr.IndexOf(']', bracket);
                if (end > bracket)
                    int.TryParse(typeStr.AsSpan(bracket + 1, end - bracket - 1), out length);
            }
            return (ColumnType.String, length, length); // sourceTypeCode = explicit length
        }

        return upper switch
        {
            "BYTE" => (ColumnType.Byte, 1, null),
            "WORD" => (ColumnType.UInt16, 2, null),
            "DWRD" or "DWORD" => (ColumnType.Int32, 4, null),
            "FLOAT" => (ColumnType.Float, 4, null),
            _ => (ColumnType.String, 32, null)
        };
    }

    private static object? ConvertValue(string field, ColumnType type)
    {
        if (string.IsNullOrEmpty(field) || field == "-")
        {
            return type switch
            {
                ColumnType.String => field,
                ColumnType.Byte => (byte)0,
                ColumnType.UInt16 => (ushort)0,
                ColumnType.Int32 => 0,
                ColumnType.UInt32 => (uint)0,
                ColumnType.Float => 0f,
                _ => field
            };
        }

        // A negative value keeps its sign. The column type says WORD, but these tables are written with
        // negatives anyway - ExpRecalculation's ByLevelDiff starts at -150 - and the 2016 loader wraps them
        // itself. Wrapping here instead threw the sign away permanently: -150 was stored as 65386 and
        // written back as 65386, turning a level-difference penalty into a huge positive number. Storing
        // the number the file states round-trips it and is what SQL should see.
        return type switch
        {
            ColumnType.Byte when byte.TryParse(field, out byte b) => b,
            ColumnType.Byte when sbyte.TryParse(field, out sbyte sb) => sb,
            ColumnType.UInt16 when ushort.TryParse(field, out ushort u) => u,
            ColumnType.UInt16 when short.TryParse(field, out short sh) => sh,
            ColumnType.UInt32 when uint.TryParse(field, out uint u) => u,
            ColumnType.UInt32 when int.TryParse(field, out int iv) => iv,
            ColumnType.Int32 when int.TryParse(field, out int v) => v,
            ColumnType.Float when float.TryParse(field, out float f) => f,
            _ => field
        };
    }

    /// <summary>
    /// Split a line on tabs, trimming each field. Handles the tab-separated format.
    /// </summary>
    private static string[] SplitFields(string line, Preprocessor? preprocessor = null)
    {
        // Fields are tab-separated, plus any #delimiter the file declares (NPC.txt: space). A quoted
        // field is never split (Script tables quote multi-word dialogue). Strip inline comments (;).
        //
        // A ";" opens a comment only where it OPENS A FIELD - at the start of the line or straight after
        // a delimiter, which is where every comment in these files actually sits. Treating one anywhere
        // as a comment cut a mob's line off mid-sentence: MobChat ships "Flee if you are scared; you do
        // not want me to catch you." unquoted, and the half after the semicolon was dropped on import
        // and gone from everything written afterwards.
        IReadOnlyList<char> extra = preprocessor?.Delimiters ?? [];
        int commentIdx = -1;
        bool inQuote = false;
        bool atFieldStart = true;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') inQuote = !inQuote;
            if (c == ';' && !inQuote && atFieldStart) { commentIdx = i; break; }
            if (!inQuote && (c == '	' || extra.Contains(c))) atFieldStart = true;
            else if (c != ' ') atFieldStart = false;
        }

        string data = commentIdx >= 0 ? line[..commentIdx] : line;
        var fields = new List<string>();
        var cur = new System.Text.StringBuilder();
        inQuote = false;
        foreach (char ch in data)
        {
            if (ch == '"') inQuote = !inQuote;
            if (!inQuote && (ch == '\t' || extra.Contains(ch)))
            {
                if (cur.Length > 0) fields.Add(cur.ToString());
                cur.Clear();
                continue;
            }
            cur.Append(ch);
        }
        if (cur.Length > 0) fields.Add(cur.ToString());
        return fields.Select(f => f.Trim()).Where(f => f.Length > 0).ToArray();
    }
}

/// <summary>
/// Handles #ignore and #exchange preprocessor directives.
/// </summary>
internal class Preprocessor
{
    private readonly List<char> _ignoreChars = [];
    private readonly List<(string from, string to)> _exchanges = [];
    private readonly List<char> _delimiters = [];
    private readonly List<string> _directives = [];

    /// <summary>Field separators declared with #delimiter, in addition to tab.</summary>
    public IReadOnlyList<char> Delimiters => _delimiters;

    /// <summary>
    /// The directive lines exactly as they appeared, so a writer can re-emit them.
    ///
    /// Without these the round trip is lossy in a way that corrupts data rather than formatting: #exchange
    /// declares how an embedded space is encoded (Sand#Beach), and a file written without the directive AND
    /// without the encoding puts a literal space inside a field of a space-delimited table.
    /// </summary>
    public IReadOnlyList<string> Directives => _directives;

    public void Remember(string line) => _directives.Add(line);

    /// <summary>The inverse of <see cref="Apply"/>: space back to '#', for writing.</summary>
    public string Encode(string value)
    {
        string result = value;
        foreach (var (from, to) in _exchanges)
            if (to.Length > 0)
                result = result.Replace(to, from);
        return result;
    }

    public void ParseDelimiter(string line)
    {
        // #delimiter \x20  -> space is a field separator too
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < parts.Length; i++)
        {
            string token = parts[i];
            if (token.StartsWith(';')) break;
            char? ch = ParseEscape(token);
            if (ch.HasValue && ch.Value != '\t') _delimiters.Add(ch.Value);
        }
    }

    public void ParseIgnore(string line)
    {
        // #ignore \o042  → ignore octal 042 = double quote
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < parts.Length; i++)
        {
            string token = parts[i];
            if (token.StartsWith(';')) break;
            char? ch = ParseEscape(token);
            if (ch.HasValue) _ignoreChars.Add(ch.Value);
        }
    }

    public void ParseExchange(string line)
    {
        // #exchange # \x20  → replace '#' with space
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3)
        {
            string from = parts[1];
            string to = ParseEscapeStr(parts[2]);
            _exchanges.Add((from, to));
        }
    }

    /// <summary>Remove the characters #ignore declares (quotes). Not used when reading - see Apply.</summary>
    public string StripIgnored(string value)
    {
        string result = value;
        foreach (char c in _ignoreChars)
            result = result.Replace(c.ToString(), "");
        return result;
    }

    /// <summary>
    /// StripIgnored plus the #exchange substitution, i.e. the value as the SERVER finally sees it.
    ///
    /// Not used when reading a table. Whether a space is written literally or as '#' is a per-file habit,
    /// not a rule - Field.txt writes Sand#Beach while Script/AdlF.txt writes "I don't think I can make it"
    /// with real spaces, and neither declares anything that would let a writer tell them apart. Decoding on
    /// read throws that away, and then no writer can put it back: encoding everything rewrote 51 script
    /// files into I#don't#think#I#can#make#it, and encoding nothing rewrote Field.txt's Sand#Beach. Keeping
    /// the value exactly as written round-trips both, and the exchange stays what it is - a loader concern.
    ///
    /// #ignore is the same story one level down. It tells the LOADER to ignore quotes; it does not mean the
    /// file omits them, and Script/AdlF.txt keeps "Eglack, I think that kid is alive." quoted on disk.
    /// Stripping them on read leaves a writer unable to tell a quoted value from an unquoted one, so they
    /// are kept too, and both directives are re-emitted for the server to act on.
    /// </summary>
    public string Apply(string value)
    {
        string result = StripIgnored(value);
        foreach (var (from, to) in _exchanges)
            result = result.Replace(from, to);
        return result;
    }

    private static char? ParseEscape(string token)
    {
        if (token.StartsWith("\\o") && token.Length > 2)
        {
            // Octal
            try { return (char)Convert.ToInt32(token[2..], 8); }
            catch { return null; }
        }
        if (token.StartsWith("\\x") && token.Length > 2)
        {
            // Hex
            try { return (char)Convert.ToInt32(token[2..], 16); }
            catch { return null; }
        }
        return token.Length == 1 ? token[0] : null;
    }

    private static string ParseEscapeStr(string token)
    {
        char? ch = ParseEscape(token);
        return ch.HasValue ? ch.Value.ToString() : token;
    }
}
