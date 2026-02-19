using System.Text.Json;
using Mimir.Core.Models;

namespace Mimir.ShineTable;

/// <summary>
/// Parses the #DEFINE/#ENDDEFINE text format.
/// Schema blocks define types upfront, then data rows reference them by name.
/// Primarily used for server config (ServerInfo.txt, DefaultCharacterData.txt).
/// </summary>
internal static class ConfigTableFormatParser
{
    public static List<string> Write(IReadOnlyList<TableEntry> tables)
    {
        var lines = new List<string>();

        // Resolve type names upfront
        var typeNames = tables.Select(table =>
        {
            var meta = table.Schema.Metadata;
            return meta?.TryGetValue("typeName", out var tn) == true
                ? ToStr(tn) : table.Schema.TableName;
        }).ToList();

        // Write ALL #DEFINE blocks first (matching real Fiesta file layout)
        for (int i = 0; i < tables.Count; i++)
        {
            var table = tables[i];
            lines.Add($"#DEFINE {typeNames[i]}");
            foreach (var col in table.Schema.Columns)
            {
                string typeTag = MapTypeBack(col.Type);
                lines.Add($"<{typeTag}>\t; {col.Name}");
            }
            lines.Add("#ENDDEFINE");
            lines.Add("");
        }

        // Then write ALL data rows
        for (int i = 0; i < tables.Count; i++)
        {
            var table = tables[i];
            foreach (var row in table.Rows)
            {
                var values = table.Schema.Columns.Select(col =>
                {
                    var val = row.TryGetValue(col.Name, out var v) ? v : null;
                    return FormatCsvValue(val, col.Type);
                });
                lines.Add($"{typeNames[i]} {string.Join(", ", values)}");
            }

            if (table.Rows.Count > 0)
                lines.Add("");
        }

        lines.Add("#END");
        return lines;
    }

    private static string MapTypeBack(ColumnType type) => type switch
    {
        ColumnType.String => "STRING",
        ColumnType.Int32 => "INTEGER",
        ColumnType.Float => "FLOAT",
        ColumnType.Byte => "BYTE",
        ColumnType.UInt16 => "WORD",
        ColumnType.UInt32 => "DWORD",
        _ => "STRING"
    };

    private static string FormatCsvValue(object? val, ColumnType type)
    {
        if (val is null or DBNull) return type == ColumnType.String ? "\"\"" : "0";
        if (val is JsonElement je) val = UnboxJsonElement(je);
        var s = val.ToString() ?? "";
        if (s.Length == 0) return type == ColumnType.String ? "\"\"" : "0";
        // Quote strings that contain commas or whitespace
        if (type == ColumnType.String && (s.Contains(',') || s.Contains(' ') || s.Contains('\t')))
            return $"\"{s}\"";
        if (type == ColumnType.String)
            return $"\"{s}\"";
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
        string fileName = Path.GetFileNameWithoutExtension(filePath);

        // Pass 1: collect all #DEFINE blocks (name → list of column types)
        var defines = ParseDefines(lines);

        // Pass 2: collect data rows grouped by type name
        var tableRows = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            // Data line: TYPE_NAME value1, value2, ...
            // First token (before whitespace) is the type name
            int firstSpace = line.IndexOfAny([' ', '\t']);
            if (firstSpace < 0) continue;

            string typeName = line[..firstSpace].Trim();
            if (!defines.ContainsKey(typeName)) continue;

            string rest = line[firstSpace..].Trim();
            // Strip inline comment
            rest = StripComment(rest);

            var values = ParseCsvValues(rest);

            if (!tableRows.TryGetValue(typeName, out var rows))
            {
                rows = [];
                tableRows[typeName] = rows;
            }

            var define = defines[typeName];
            var row = new Dictionary<string, object?>(define.Count);
            for (int c = 0; c < define.Count; c++)
            {
                string val = c < values.Count ? values[c] : "";
                row[define[c].Name] = ConvertValue(val, define[c].Type);
            }
            rows.Add(row);
        }

        // Build TableEntry per type (preserving order from file)
        var tables = new List<TableEntry>();
        int sectionIndex = 0;
        foreach (var (typeName, rows) in tableRows)
        {
            if (!defines.TryGetValue(typeName, out var cols)) continue;

            var schema = new TableSchema
            {
                TableName = $"{fileName}_{typeName}",
                SourceFormat = "configtable",
                Columns = cols,
                Metadata = new Dictionary<string, object>
                {
                    ["sourceFile"] = Path.GetFileName(filePath),
                    ["typeName"] = typeName,
                    ["format"] = "define",
                    ["sectionIndex"] = sectionIndex++
                }
            };

            tables.Add(new TableEntry { Schema = schema, Rows = rows });
        }

        return tables;
    }

    private static Dictionary<string, List<ColumnDefinition>> ParseDefines(string[] lines)
    {
        var defines = new Dictionary<string, List<ColumnDefinition>>(StringComparer.OrdinalIgnoreCase);

        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i].Trim();

            if (line.StartsWith("#DEFINE", StringComparison.OrdinalIgnoreCase))
            {
                string typeName = line["#DEFINE".Length..].Trim();
                var columns = new List<ColumnDefinition>();
                int colIndex = 0;
                i++;

                while (i < lines.Length)
                {
                    string defLine = lines[i].Trim();

                    if (defLine.StartsWith("#ENDDEFINE", StringComparison.OrdinalIgnoreCase))
                    {
                        i++;
                        break;
                    }

                    // Skip comments and blank lines inside define
                    if (defLine.Length == 0 || defLine.StartsWith(';'))
                    {
                        i++;
                        continue;
                    }

                    // Nested #DEFINE (e.g. DefaultCharacterData has #DEFINE ITEM inside)
                    if (defLine.StartsWith("#DEFINE", StringComparison.OrdinalIgnoreCase))
                        break;

                    // Parse type declaration: <STRING>, <INTEGER>, etc.
                    // May have inline comment: <INTEGER>  ; Class
                    if (defLine.StartsWith('<'))
                    {
                        int closeAngle = defLine.IndexOf('>');
                        if (closeAngle > 0)
                        {
                            string typeTag = defLine[1..closeAngle].Trim().ToUpperInvariant();
                            string colName = ExtractCommentName(defLine, closeAngle) ?? $"Col{colIndex}";

                            var (colType, length) = MapDefineType(typeTag);
                            columns.Add(new ColumnDefinition
                            {
                                Name = colName,
                                Type = colType,
                                Length = length
                            });
                            colIndex++;
                        }
                    }

                    i++;
                }

                if (columns.Count > 0)
                    defines[typeName] = columns;

                continue;
            }

            i++;
        }

        return defines;
    }

    private static string? ExtractCommentName(string line, int afterTypeEnd)
    {
        // Look for ; comment after the type tag, use it as column name
        int semi = line.IndexOf(';', afterTypeEnd);
        if (semi < 0) return null;

        string comment = line[(semi + 1)..].Trim();
        if (comment.Length == 0) return null;

        // Clean up: take first word or short phrase, make it a valid identifier
        // e.g. "Start Map name" → "StartMapName", "Class" → "Class"
        var words = comment.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;

        return string.Concat(words.Select(w =>
            char.ToUpperInvariant(w[0]) + (w.Length > 1 ? w[1..] : "")));
    }

    private static (ColumnType type, int length) MapDefineType(string typeTag) => typeTag switch
    {
        "STRING" => (ColumnType.String, 0),
        "INTEGER" => (ColumnType.Int32, 4),
        "FLOAT" => (ColumnType.Float, 4),
        "BYTE" => (ColumnType.Byte, 1),
        "WORD" => (ColumnType.UInt16, 2),
        "DWORD" or "DWRD" => (ColumnType.UInt32, 4),
        _ => (ColumnType.String, 0)
    };

    private static List<string> ParseCsvValues(string input)
    {
        var values = new List<string>();
        int i = 0;

        while (i < input.Length)
        {
            // Skip leading whitespace and commas
            while (i < input.Length && (input[i] == ' ' || input[i] == '\t' || input[i] == ','))
                i++;

            if (i >= input.Length) break;

            if (input[i] == '"')
            {
                // Quoted string
                i++; // skip opening quote
                int start = i;
                while (i < input.Length && input[i] != '"') i++;
                values.Add(input[start..i]);
                if (i < input.Length) i++; // skip closing quote
            }
            else
            {
                // Unquoted value - read until comma or tab (both are field separators)
                int start = i;
                while (i < input.Length && input[i] != ',' && input[i] != '\t')
                    i++;
                values.Add(input[start..i].Trim());
            }
        }

        return values;
    }

    private static string StripComment(string line)
    {
        bool inQuote = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuote = !inQuote;
            if (line[i] == ';' && !inQuote) return line[..i].TrimEnd();
        }
        return line;
    }

    private static object? ConvertValue(string field, ColumnType type)
    {
        if (string.IsNullOrEmpty(field))
        {
            return type switch
            {
                ColumnType.String => "",
                ColumnType.Int32 => 0,
                ColumnType.Float => 0f,
                ColumnType.Byte => (byte)0,
                ColumnType.UInt16 => (ushort)0,
                ColumnType.UInt32 => (uint)0,
                _ => ""
            };
        }

        return type switch
        {
            ColumnType.Int32 when int.TryParse(field, out int v) => v,
            ColumnType.Float when float.TryParse(field, out float f) => f,
            ColumnType.Byte when byte.TryParse(field, out byte b) => b,
            ColumnType.UInt16 when ushort.TryParse(field, out ushort u) => u,
            ColumnType.UInt32 when uint.TryParse(field, out uint u) => u,
            _ => field
        };
    }
}
