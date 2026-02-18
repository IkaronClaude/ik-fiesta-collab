using System.Text.Json;
using Mimir.Core.Models;

namespace Mimir.ShineTable;

/// <summary>
/// Parses the #table/#columntype/#columnname/#record text format.
/// One file can contain multiple tables. Tab-separated values.
/// </summary>
internal static class ShineTableFormatParser
{
    public static List<string> Write(IReadOnlyList<TableEntry> tables)
    {
        var lines = new List<string>();

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
                    return FormatValue(val, col.Type);
                });
                lines.Add("#record\t" + string.Join('\t', fields));
            }

            lines.Add(""); // blank line between tables
        }

        return lines;
    }

    private static string MapTypeBack(ColumnDefinition col) => col.Type switch
    {
        ColumnType.Byte => "BYTE",
        ColumnType.UInt16 => "WORD",
        ColumnType.UInt32 => "DWRD",
        ColumnType.Float => "FLOAT",
        ColumnType.String when col.Length != 32 => $"STRING[{col.Length}]",
        ColumnType.String => "INDEX",
        _ => $"STRING[{col.Length}]"
    };

    private static string FormatValue(object? val, ColumnType type)
    {
        if (val is null or DBNull) return type == ColumnType.String ? "-" : "0";
        if (val is JsonElement je) val = UnboxJsonElement(je);
        var s = val.ToString() ?? "";
        if (s.Length == 0) return type == ColumnType.String ? "-" : "0";
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
                i++;
                continue;
            }

            if (raw.StartsWith("#exchange", StringComparison.OrdinalIgnoreCase))
            {
                preprocessor.ParseExchange(raw);
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
                }
                i = nextLine;
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
        string[] tableParts = SplitFields(tableLine);
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

            // Next table or end of file
            if (lower.StartsWith("#table") || lower.StartsWith("#end"))
                break;

            if (lower.StartsWith("#columntype"))
            {
                columnTypes = SplitFields(raw).Skip(1).ToList();
                i++;
                continue;
            }

            if (lower.StartsWith("#columnname"))
            {
                columnNames = SplitFields(raw).Skip(1).ToList();
                i++;
                continue;
            }

            if (lower.StartsWith("#record"))
            {
                // Build column definitions on first record if we haven't yet
                columns ??= BuildColumns(columnTypes, columnNames);

                var fields = SplitFields(raw).Skip(1).ToList();
                var row = new Dictionary<string, object?>(columns.Count);

                for (int c = 0; c < columns.Count && c < fields.Count; c++)
                {
                    string field = preprocessor.Apply(fields[c]);
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
                ["format"] = "table"
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
            var (colType, length) = MapColumnType(typeStr);

            columns.Add(new ColumnDefinition
            {
                Name = name,
                Type = colType,
                Length = length
            });
        }

        return columns;
    }

    private static (ColumnType type, int length) MapColumnType(string typeStr)
    {
        string upper = typeStr.ToUpperInvariant();

        if (upper == "INDEX" || upper.StartsWith("STRING"))
        {
            int length = 32; // default
            int bracket = typeStr.IndexOf('[');
            if (bracket >= 0)
            {
                int end = typeStr.IndexOf(']', bracket);
                if (end > bracket)
                    int.TryParse(typeStr.AsSpan(bracket + 1, end - bracket - 1), out length);
            }
            return (ColumnType.String, length);
        }

        return upper switch
        {
            "BYTE" => (ColumnType.Byte, 1),
            "WORD" => (ColumnType.UInt16, 2),
            "DWRD" or "DWORD" => (ColumnType.UInt32, 4),
            "FLOAT" => (ColumnType.Float, 4),
            _ => (ColumnType.String, 32)
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
                ColumnType.UInt32 => (uint)0,
                ColumnType.Float => 0f,
                _ => field
            };
        }

        return type switch
        {
            ColumnType.Byte when byte.TryParse(field, out byte b) => b,
            ColumnType.UInt16 when ushort.TryParse(field, out ushort u) => u,
            ColumnType.UInt32 when uint.TryParse(field, out uint u) => u,
            ColumnType.Int32 when int.TryParse(field, out int v) => v,
            ColumnType.Float when float.TryParse(field, out float f) => f,
            _ => field
        };
    }

    /// <summary>
    /// Split a line on tabs, trimming each field. Handles the tab-separated format.
    /// </summary>
    private static string[] SplitFields(string line)
    {
        // Fields are tab-separated. Strip inline comments (;)
        int commentIdx = -1;
        bool inQuote = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuote = !inQuote;
            if (line[i] == ';' && !inQuote) { commentIdx = i; break; }
        }

        string data = commentIdx >= 0 ? line[..commentIdx] : line;
        return data.Split('\t', StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => f.Trim())
                    .ToArray();
    }
}

/// <summary>
/// Handles #ignore and #exchange preprocessor directives.
/// </summary>
internal class Preprocessor
{
    private readonly List<char> _ignoreChars = [];
    private readonly List<(string from, string to)> _exchanges = [];

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

    public string Apply(string value)
    {
        string result = value;
        foreach (char c in _ignoreChars)
            result = result.Replace(c.ToString(), "");
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
