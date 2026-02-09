namespace Mimir.Core.Models;

/// <summary>
/// Extracts one environment's view from a merged table at build time.
/// </summary>
public static class TableSplitter
{
    public static TableFile Split(TableFile merged, string envName, EnvMergeMetadata envMeta)
    {
        // Build column map: merged column name → ColumnDefinition
        var mergedColMap = merged.Columns.ToDictionary(c => c.Name);

        // Resolve output columns in env's column order, applying overrides and renames
        var outputColumns = new List<ColumnDefinition>();
        var colNameMapping = new Dictionary<string, string>(); // mergedName → outputName

        foreach (var mergedColName in envMeta.ColumnOrder)
        {
            if (!mergedColMap.TryGetValue(mergedColName, out var col)) continue;

            // Check if this column is visible to this env
            if (col.Environments != null && !col.Environments.Contains(envName)) continue;

            // Apply renames
            var outputName = envMeta.ColumnRenames.TryGetValue(mergedColName, out var renamed)
                ? renamed
                : mergedColName;

            colNameMapping[mergedColName] = outputName;

            // Apply overrides
            var length = col.Length;
            var sourceTypeCode = col.SourceTypeCode;

            if (envMeta.ColumnOverrides.TryGetValue(col.Name, out var ov))
            {
                if (ov.Length.HasValue) length = ov.Length.Value;
                if (ov.SourceTypeCode.HasValue) sourceTypeCode = ov.SourceTypeCode.Value;
            }

            outputColumns.Add(new ColumnDefinition
            {
                Name = outputName,
                Type = col.Type,
                Length = length,
                SourceTypeCode = sourceTypeCode
                // No Environments — output is a clean non-merged table
            });
        }

        // Filter and remap rows
        var outputRows = new List<Dictionary<string, object?>>();

        for (int i = 0; i < merged.Data.Count; i++)
        {
            // Check if row is visible to this env
            if (merged.RowEnvironments != null && i < merged.RowEnvironments.Count)
            {
                var rowEnv = merged.RowEnvironments[i];
                if (rowEnv != null && !rowEnv.Contains(envName))
                    continue; // row not in this env
            }

            var sourceRow = merged.Data[i];
            var outputRow = new Dictionary<string, object?>();

            foreach (var (mergedColName, outputName) in colNameMapping)
            {
                outputRow[outputName] = sourceRow.GetValueOrDefault(mergedColName);
            }

            outputRows.Add(outputRow);
        }

        return new TableFile
        {
            Header = merged.Header,
            Columns = outputColumns,
            Data = outputRows
            // No RowEnvironments — clean non-merged output
        };
    }
}
