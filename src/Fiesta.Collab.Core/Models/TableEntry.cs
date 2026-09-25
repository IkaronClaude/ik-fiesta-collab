namespace Fiesta.Collab.Core.Models;

/// <summary>
/// Represents a fully loaded table: schema + rows.
/// Rows are dictionaries keyed by column name with typed values.
/// </summary>
public sealed class TableEntry
{
    public required TableSchema Schema { get; init; }
    public required IReadOnlyList<Dictionary<string, object?>> Rows { get; init; }

    /// <summary>Per-row environments, parallel to <see cref="Rows"/> (null entry = every environment; null list = all
    /// rows shared). The SQL engine keeps them in the `_envs` column so they follow their row.</summary>
    public IReadOnlyList<List<string>?>? RowEnvironments { get; init; }
}
