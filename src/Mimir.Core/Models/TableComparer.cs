using System.Text.Json;

namespace Mimir.Core.Models;

/// <summary>
/// Compares two table entries to determine if they contain identical data.
/// Used during multi-source import to detect shared vs conflicting tables.
/// </summary>
public static class TableComparer
{
    /// <summary>
    /// Returns null if tables match, or a description of the first difference found.
    /// Compares column schemas and row data. Ignores provider metadata differences
    /// (e.g. different source file paths are fine if the actual data matches).
    /// </summary>
    public static string? FindDifference(TableFile a, TableFile b)
    {
        // Compare column count
        if (a.Columns.Count != b.Columns.Count)
            return $"Column count differs: {a.Columns.Count} vs {b.Columns.Count}";

        // Compare column definitions
        for (int i = 0; i < a.Columns.Count; i++)
        {
            var ca = a.Columns[i];
            var cb = b.Columns[i];
            if (ca.Name != cb.Name)
                return $"Column {i} name differs: '{ca.Name}' vs '{cb.Name}'";
            if (ca.Type != cb.Type)
                return $"Column '{ca.Name}' type differs: {ca.Type} vs {cb.Type}";
            if (ca.Length != cb.Length)
                return $"Column '{ca.Name}' length differs: {ca.Length} vs {cb.Length}";
        }

        // Compare row count
        if (a.Data.Count != b.Data.Count)
            return $"Row count differs: {a.Data.Count} vs {b.Data.Count}";

        // Compare row data
        for (int r = 0; r < a.Data.Count; r++)
        {
            var ra = a.Data[r];
            var rb = b.Data[r];

            foreach (var col in a.Columns)
            {
                var va = ra.TryGetValue(col.Name, out var valA) ? valA : null;
                var vb = rb.TryGetValue(col.Name, out var valB) ? valB : null;

                if (!ValuesEqual(va, vb))
                    return $"Row {r}, column '{col.Name}': '{Stringify(va)}' vs '{Stringify(vb)}'";
            }
        }

        return null;
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        a = Unbox(a);
        b = Unbox(b);

        if (a is null && b is null) return true;
        if (a is null || b is null) return false;

        // Numeric comparison: convert both to same type for comparison
        if (a is IConvertible && b is IConvertible)
        {
            try
            {
                // Compare as strings for exact match (handles float precision too)
                return a.ToString() == b.ToString();
            }
            catch
            {
                // Fall through to object.Equals
            }
        }

        return a.Equals(b);
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
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => je.ToString()
            };
        }
        return val;
    }

    private static string Stringify(object? val) =>
        val is null ? "null" : val.ToString() ?? "null";
}
