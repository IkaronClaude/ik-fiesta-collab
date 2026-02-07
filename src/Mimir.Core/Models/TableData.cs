namespace Mimir.Core.Models;

/// <summary>
/// Represents a fully loaded table: schema + rows.
/// Rows are dictionaries keyed by column name with typed values.
/// </summary>
public sealed class TableData
{
    public required TableSchema Schema { get; init; }
    public required IReadOnlyList<Dictionary<string, object?>> Rows { get; init; }
}
