using System.Text.Json.Serialization;

namespace Mimir.Core.Models;

/// <summary>
/// A single table file as stored on disk (.json).
/// Contains header metadata, column definitions, and all row data.
/// </summary>
public sealed class TableFile
{
    [JsonPropertyName("header")]
    public required TableHeader Header { get; init; }

    [JsonPropertyName("columns")]
    public required IReadOnlyList<ColumnDefinition> Columns { get; init; }

    [JsonPropertyName("data")]
    public required IReadOnlyList<Dictionary<string, object?>> Data { get; init; }

    /// <summary>
    /// Per-row environment annotations for merged tables.
    /// null entry = row present in all environments (shared).
    /// ["server"] = server-only row.
    /// null list = non-merged table (all rows shared).
    /// </summary>
    [JsonPropertyName("rowEnvironments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<List<string>?>? RowEnvironments { get; init; }
}

public sealed class TableHeader
{
    [JsonPropertyName("tableName")]
    public required string TableName { get; init; }

    [JsonPropertyName("sourceFormat")]
    public required string SourceFormat { get; init; }

    /// <summary>
    /// Provider-specific metadata preserved for round-tripping.
    /// For SHN: cryptHeader (base64), header uint, defaultRecordLength.
    /// </summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Metadata { get; init; }
}
