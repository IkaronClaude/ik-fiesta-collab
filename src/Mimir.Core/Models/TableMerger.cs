using System.Text.Json;
using System.Text.Json.Serialization;
using Mimir.Core.Templates;

namespace Mimir.Core.Models;

/// <summary>
/// Merges a source table into a target table based on a join clause.
/// Used during multi-environment import to combine tables from different environments.
/// </summary>
public static class TableMerger
{
    public static MergeResult Merge(
        TableFile target, TableFile source,
        JoinClause on, string envName, string columnStrategy,
        string conflictStrategy = "report",
        string? targetEnvName = null)
    {
        var conflicts = new List<MergeConflict>();
        var envMetadata = new Dictionary<string, EnvMergeMetadata>();

        // --- Column merge ---
        var mergedColumns = new List<ColumnDefinition>();
        var sourceColMap = source.Columns.ToDictionary(c => c.Name);
        var targetColNames = new HashSet<string>(target.Columns.Select(c => c.Name));
        var sourceOnlyColNames = new HashSet<string>();
        var splitRenames = new Dictionary<string, string>(); // mergedName → originalName

        // Track which source columns were handled
        var handledSourceCols = new HashSet<string>();

        foreach (var tCol in target.Columns)
        {
            if (sourceColMap.TryGetValue(tCol.Name, out var sCol))
            {
                handledSourceCols.Add(tCol.Name);

                if (tCol.Type == sCol.Type)
                {
                    // Same type → shared column (keep target's length as canonical)
                    mergedColumns.Add(new ColumnDefinition
                    {
                        Name = tCol.Name,
                        Type = tCol.Type,
                        Length = tCol.Length,
                        SourceTypeCode = tCol.SourceTypeCode,
                        Environments = tCol.Environments // keep existing env annotations
                    });
                }
                else
                {
                    // Different type → split: keep target's col, add Col__env for source
                    mergedColumns.Add(new ColumnDefinition
                    {
                        Name = tCol.Name,
                        Type = tCol.Type,
                        Length = tCol.Length,
                        SourceTypeCode = tCol.SourceTypeCode,
                        Environments = tCol.Environments
                    });

                    var splitName = $"{tCol.Name}__{envName}";
                    mergedColumns.Add(new ColumnDefinition
                    {
                        Name = splitName,
                        Type = sCol.Type,
                        Length = sCol.Length,
                        SourceTypeCode = sCol.SourceTypeCode,
                        Environments = [envName]
                    });
                    splitRenames[splitName] = tCol.Name;
                }
            }
            else
            {
                // Target-only column — keep as-is
                mergedColumns.Add(new ColumnDefinition
                {
                    Name = tCol.Name,
                    Type = tCol.Type,
                    Length = tCol.Length,
                    SourceTypeCode = tCol.SourceTypeCode,
                    Environments = tCol.Environments
                });
            }
        }

        // Source-only columns
        foreach (var sCol in source.Columns)
        {
            if (handledSourceCols.Contains(sCol.Name)) continue;

            sourceOnlyColNames.Add(sCol.Name);
            mergedColumns.Add(new ColumnDefinition
            {
                Name = sCol.Name,
                Type = sCol.Type,
                Length = sCol.Length,
                SourceTypeCode = sCol.SourceTypeCode,
                Environments = [envName]
            });
        }

        // --- Build column overrides for source env ---
        var columnOverrides = new Dictionary<string, ColumnOverride>();
        foreach (var tCol in target.Columns)
        {
            if (!sourceColMap.TryGetValue(tCol.Name, out var sCol)) continue;
            if (tCol.Type != sCol.Type) continue; // split columns don't need overrides

            if (tCol.Length != sCol.Length || tCol.SourceTypeCode != sCol.SourceTypeCode)
            {
                var ov = new ColumnOverride();
                if (tCol.Length != sCol.Length) ov.Length = sCol.Length;
                if (tCol.SourceTypeCode != sCol.SourceTypeCode) ov.SourceTypeCode = sCol.SourceTypeCode;
                columnOverrides[tCol.Name] = ov;
            }
        }

        // --- Determine shared column names (for conflict detection) ---
        var sharedColNames = new HashSet<string>();
        foreach (var col in mergedColumns)
        {
            if (col.Environments == null && targetColNames.Contains(col.Name) && sourceColMap.ContainsKey(col.Name))
                sharedColNames.Add(col.Name);
        }

        // --- Row merge ---
        var mergedRows = new List<Dictionary<string, object?>>();
        var mergedRowEnvs = new List<List<string>?>();

        // Build source join index.
        // Use a queue per key so that duplicate join-key values in both tables are
        // matched in order (FIFO) rather than the last occurrence overwriting previous ones.
        var duplicateJoinKeys = new List<string>();
        var sourceIndex = new Dictionary<string, Queue<int>>();
        for (int i = 0; i < source.Data.Count; i++)
        {
            var key = Stringify(source.Data[i].GetValueOrDefault(on.Source));
            if (!sourceIndex.TryGetValue(key, out var q))
            {
                q = new Queue<int>();
                sourceIndex[key] = q;
            }
            else if (q.Count == 1)
            {
                // Track the first time a key appears more than once
                duplicateJoinKeys.Add(key);
            }
            q.Enqueue(i);
        }

        var matchedSourceRows = new HashSet<int>();
        // Maps merged-row index → source-row index for matched rows (used by conflict splitting).
        var matchedSourceIdxByMergedRowIdx = new Dictionary<int, int>();

        // Process all target rows in their original order.
        // Each row is either matched (has a corresponding source row) or target-only.
        // This preserves the target's original row order in the merged output.
        for (int i = 0; i < target.Data.Count; i++)
        {
            var targetRow = target.Data[i];
            var key = Stringify(targetRow.GetValueOrDefault(on.Target));
            var merged = new Dictionary<string, object?>();

            if (sourceIndex.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                // Matched row — dequeue the next available source occurrence
                var sIdx = queue.Dequeue();
                matchedSourceRows.Add(sIdx);
                matchedSourceIdxByMergedRowIdx[mergedRows.Count] = sIdx;
                var sourceRow = source.Data[sIdx];

                foreach (var col in mergedColumns)
                {
                    if (splitRenames.TryGetValue(col.Name, out var origName))
                    {
                        // Split column (Col__env) — get value from source using original name
                        merged[col.Name] = sourceRow.GetValueOrDefault(origName);
                    }
                    else if (sourceOnlyColNames.Contains(col.Name))
                    {
                        // Source-only column
                        merged[col.Name] = sourceRow.GetValueOrDefault(col.Name);
                    }
                    else if (sharedColNames.Contains(col.Name))
                    {
                        // Shared column — check for conflicts
                        var tVal = targetRow.GetValueOrDefault(col.Name);
                        var sVal = sourceRow.GetValueOrDefault(col.Name);

                        if (!ValuesEqual(tVal, sVal))
                        {
                            conflicts.Add(new MergeConflict
                            {
                                JoinKey = key,
                                Column = col.Name,
                                TargetValue = Stringify(tVal),
                                SourceValue = Stringify(sVal)
                            });
                        }

                        merged[col.Name] = tVal; // keep target value
                    }
                    else
                    {
                        // Target-only column
                        merged[col.Name] = targetRow.GetValueOrDefault(col.Name);
                    }
                }

                mergedRows.Add(merged);
                mergedRowEnvs.Add(null); // null = shared (present in both envs)
            }
            else
            {
                // Target-only row — no match in source
                foreach (var col in mergedColumns)
                    merged[col.Name] = targetRow.GetValueOrDefault(col.Name);

                mergedRows.Add(merged);

                // Preserve existing row env annotation from target (set by copy action pre-tagging).
                // If untagged (null) and targetEnvName is known, tag with the target env so
                // this row is excluded from other environments' builds.
                var existingEnv = target.RowEnvironments != null && i < target.RowEnvironments.Count
                    ? target.RowEnvironments[i]
                    : null;
                mergedRowEnvs.Add(existingEnv ?? (targetEnvName != null ? [targetEnvName] : null));
            }
        }

        // Source-only rows
        for (int i = 0; i < source.Data.Count; i++)
        {
            if (matchedSourceRows.Contains(i)) continue;

            var row = new Dictionary<string, object?>();
            foreach (var col in mergedColumns)
            {
                if (splitRenames.TryGetValue(col.Name, out var origName))
                {
                    row[col.Name] = source.Data[i].GetValueOrDefault(origName);
                }
                else if (sourceOnlyColNames.Contains(col.Name) || sourceColMap.ContainsKey(col.Name))
                {
                    row[col.Name] = source.Data[i].GetValueOrDefault(col.Name);
                }
                else
                {
                    row[col.Name] = null; // target-only col → null for source row
                }
            }
            mergedRows.Add(row);
            mergedRowEnvs.Add([envName]);
        }

        // --- Conflict column splitting (only when conflictStrategy == "split") ---
        var conflictColumns = conflicts.Select(c => c.Column).Distinct().ToHashSet();
        if (conflictStrategy == "split" && conflictColumns.Count > 0)
        {
            foreach (var colName in conflictColumns)
            {
                var splitName = $"{colName}__{envName}";
                var sCol = sourceColMap[colName];

                // Add split column definition
                mergedColumns.Add(new ColumnDefinition
                {
                    Name = splitName,
                    Type = sCol.Type,
                    Length = sCol.Length,
                    SourceTypeCode = sCol.SourceTypeCode,
                    Environments = [envName]
                });
                splitRenames[splitName] = colName;

                // Populate split column for all rows
                for (int i = 0; i < mergedRows.Count; i++)
                {
                    var rowEnv = mergedRowEnvs[i];
                    if (rowEnv == null)
                    {
                        // Matched (shared) row — look up source row via the recorded match index
                        mergedRows[i][splitName] = matchedSourceIdxByMergedRowIdx.TryGetValue(i, out var si)
                            ? source.Data[si].GetValueOrDefault(colName) : null;
                    }
                    else if (rowEnv.Contains(envName))
                    {
                        // Source-only row — use same value as shared column
                        mergedRows[i][splitName] = mergedRows[i].GetValueOrDefault(colName);
                    }
                    else
                    {
                        // Target-only row — null for source env's split column
                        mergedRows[i][splitName] = null;
                    }
                }
            }
        }

        // --- Build env metadata ---
        var sourceColumnOrder = source.Columns.Select(c =>
            conflictStrategy == "split" && conflictColumns.Contains(c.Name)
                ? $"{c.Name}__{envName}" : c.Name
        ).ToList();
        envMetadata[envName] = new EnvMergeMetadata
        {
            ColumnOrder = sourceColumnOrder,
            ColumnOverrides = columnOverrides,
            ColumnRenames = splitRenames.ToDictionary(kv => kv.Key, kv => kv.Value),
            FormatMetadata = ExtractFormatMetadata(source.Header.Metadata)
        };

        var mergedTable = new TableFile
        {
            Header = target.Header,
            Columns = mergedColumns,
            Data = mergedRows,
            RowEnvironments = mergedRowEnvs
        };

        return new MergeResult
        {
            Table = mergedTable,
            Conflicts = conflicts,
            EnvMetadata = envMetadata,
            DuplicateJoinKeys = duplicateJoinKeys
        };
    }

    /// <summary>
    /// Extracts format-specific metadata keys (cryptHeader, header) from a raw table's metadata.
    /// These are stored per-env so each env can rebuild its SHN files with the original binary headers.
    /// </summary>
    internal static Dictionary<string, object>? ExtractFormatMetadata(Dictionary<string, object>? metadata)
    {
        if (metadata == null) return null;
        var result = new Dictionary<string, object>();
        foreach (var key in new[] { "cryptHeader", "header" })
            if (metadata.TryGetValue(key, out var val) && val != null)
                result[key] = val;
        return result.Count > 0 ? result : null;
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        a = Unbox(a);
        b = Unbox(b);

        if (a is null && b is null) return true;
        if (a is null || b is null) return false;

        return a.ToString() == b.ToString();
    }

    private static object? Unbox(object? val)
    {
        if (val is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.Number when je.TryGetInt64(out var l) => l,
                JsonValueKind.Number => je.GetDouble(),
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Null => null,
                _ => je.ToString()
            };
        }
        return val;
    }

    private static string Stringify(object? val)
    {
        if (val is JsonElement je)
            return je.ValueKind == JsonValueKind.Null ? "" : je.ToString() ?? "";
        return val?.ToString() ?? "";
    }
}

public sealed class MergeResult
{
    public required TableFile Table { get; init; }
    public required List<MergeConflict> Conflicts { get; init; }
    public required Dictionary<string, EnvMergeMetadata> EnvMetadata { get; init; }

    /// <summary>
    /// Join key values that appeared more than once in the source table.
    /// Duplicate join keys are a data integrity issue and are surfaced as warnings during import.
    /// </summary>
    public required List<string> DuplicateJoinKeys { get; init; }
}

public sealed class MergeConflict
{
    public required string JoinKey { get; init; }
    public required string Column { get; init; }
    public required string TargetValue { get; init; }
    public required string SourceValue { get; init; }
}

public sealed class EnvMergeMetadata
{
    public required List<string> ColumnOrder { get; init; }
    public required Dictionary<string, ColumnOverride> ColumnOverrides { get; init; }
    public required Dictionary<string, string> ColumnRenames { get; init; }

    /// <summary>
    /// Relative directory from the environment's import root where this table was found.
    /// e.g. "Shine" for server tables in 9Data/Shine/, "" for client tables in ressystem/.
    /// Used at build time to reconstruct the original file layout.
    /// </summary>
    public string? SourceRelDir { get; set; }

    /// <summary>
    /// Format-specific metadata captured at import (e.g. cryptHeader, header for SHN files).
    /// Stored per-env so each environment builds with its own original binary headers.
    /// </summary>
    public Dictionary<string, object>? FormatMetadata { get; set; }

    /// <summary>
    /// Parses an EnvMergeMetadata from a JsonElement (as stored in table metadata).
    /// </summary>
    public static EnvMergeMetadata? FromJsonElement(JsonElement je)
    {
        if (je.ValueKind != JsonValueKind.Object) return null;

        var columnOrder = new List<string>();
        if (je.TryGetProperty("columnOrder", out var coElem) && coElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in coElem.EnumerateArray())
                if (item.GetString() is string s)
                    columnOrder.Add(s);
        }

        var columnOverrides = new Dictionary<string, ColumnOverride>();
        if (je.TryGetProperty("columnOverrides", out var ovElem) && ovElem.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in ovElem.EnumerateObject())
            {
                var ov = new ColumnOverride();
                if (prop.Value.TryGetProperty("length", out var lenElem))
                    ov.Length = lenElem.GetInt32();
                if (prop.Value.TryGetProperty("sourceTypeCode", out var stcElem))
                    ov.SourceTypeCode = stcElem.GetInt32();
                columnOverrides[prop.Name] = ov;
            }
        }

        var columnRenames = new Dictionary<string, string>();
        if (je.TryGetProperty("columnRenames", out var rnElem) && rnElem.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in rnElem.EnumerateObject())
                if (prop.Value.GetString() is string s)
                    columnRenames[prop.Name] = s;
        }

        string? sourceRelDir = null;
        if (je.TryGetProperty("sourceRelDir", out var srdElem) && srdElem.ValueKind == JsonValueKind.String)
            sourceRelDir = srdElem.GetString();

        Dictionary<string, object>? formatMetadata = null;
        if (je.TryGetProperty("formatMetadata", out var fmElem) && fmElem.ValueKind == JsonValueKind.Object)
        {
            formatMetadata = new Dictionary<string, object>();
            foreach (var prop in fmElem.EnumerateObject())
                formatMetadata[prop.Name] = prop.Value.Clone(); // clone so JsonElement stays valid
        }

        return new EnvMergeMetadata
        {
            ColumnOrder = columnOrder,
            ColumnOverrides = columnOverrides,
            ColumnRenames = columnRenames,
            SourceRelDir = sourceRelDir,
            FormatMetadata = formatMetadata
        };
    }
}

public sealed class ColumnOverride
{
    [JsonPropertyName("length")]
    public int? Length { get; set; }

    [JsonPropertyName("sourceTypeCode")]
    public int? SourceTypeCode { get; set; }
}
