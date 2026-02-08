using System.Text.Json.Serialization;

namespace Mimir.Core.Constraints;

public sealed class ProjectDefinitions
{
    /// <summary>
    /// Table key/ID designations and column annotations.
    /// Key = table name, value = table info.
    /// </summary>
    [JsonPropertyName("tables")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, TableKeyInfo>? Tables { get; init; }

    [JsonPropertyName("constraints")]
    public List<ConstraintRule> Constraints { get; init; } = [];
}

public sealed class TableKeyInfo
{
    /// <summary>
    /// The string identifier column. e.g. "InxName" for ItemInfo.
    /// Gets UNIQUE constraint in SQLite.
    /// When a foreignKey omits its column, this is used as the default FK target.
    /// </summary>
    [JsonPropertyName("keyColumn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KeyColumn { get; init; }

    /// <summary>
    /// The integer ID column. e.g. "ID" for ItemInfo.
    /// Gets PRIMARY KEY constraint in SQLite.
    /// </summary>
    [JsonPropertyName("idColumn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IdColumn { get; init; }

    /// <summary>
    /// Column annotations: display names and descriptions.
    /// Key = column name, value = annotation info.
    /// e.g. "AC" → { displayName: "Armor Class", description: "Defense stat, +N when equipped" }
    /// </summary>
    [JsonPropertyName("columnAnnotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, ColumnAnnotation>? ColumnAnnotations { get; init; }
}

/// <summary>
/// Human-readable annotation for a column.
/// </summary>
public sealed class ColumnAnnotation
{
    /// <summary>
    /// Short display name shown in headers. e.g. "Armor Class"
    /// </summary>
    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }

    /// <summary>
    /// Longer description shown on hover/detail views. e.g. "Defense stat applied to player when equipped"
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }
}

public sealed class ConstraintRule
{
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("match")]
    public required ConstraintMatch Match { get; init; }

    [JsonPropertyName("foreignKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ForeignKeyTarget? ForeignKey { get; init; }

    [JsonPropertyName("emptyValues")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? EmptyValues { get; init; }
}

public sealed class ConstraintMatch
{
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    [JsonPropertyName("table")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Table { get; init; }

    [JsonPropertyName("column")]
    public required string Column { get; init; }
}

public sealed class ForeignKeyTarget
{
    [JsonPropertyName("table")]
    public required string Table { get; init; }

    /// <summary>
    /// Target column. If omitted, uses the target table's keyColumn.
    /// Use "@id" to reference the idColumn instead.
    /// </summary>
    [JsonPropertyName("column")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Column { get; init; }
}
