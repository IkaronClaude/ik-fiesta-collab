using System.Text.Json.Serialization;

namespace Mimir.Core.Models;

public sealed class TableSchema
{
    [JsonPropertyName("tableName")]
    public required string TableName { get; init; }

    [JsonPropertyName("sourceFormat")]
    public required string SourceFormat { get; init; }

    [JsonPropertyName("columns")]
    public required IReadOnlyList<ColumnDefinition> Columns { get; init; }

    /// <summary>
    /// Provider-specific metadata preserved for round-tripping.
    /// For SHN: header bytes, crypt header, default record length, etc.
    /// </summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Metadata { get; init; }
}
