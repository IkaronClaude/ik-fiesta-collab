using System.Text.Json.Serialization;

namespace Fiesta.Collab.Core.Models;

public sealed class ColumnDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("type")]
    public required ColumnType Type { get; init; }

    [JsonPropertyName("length")]
    public required int Length { get; init; }

    /// <summary>
    /// Original provider-specific type code (e.g. SHN type byte).
    /// Preserved for lossless round-tripping.
    /// </summary>
    [JsonPropertyName("sourceTypeCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SourceTypeCode { get; init; }

    /// <summary>
    /// The exact column name in the source file when it differs from <see cref="Name"/> (SHN files carry
    /// names like "R", " " or "" that get a generated Undefined{n} name). Null when identical.
    /// Preserved for lossless round-tripping.
    /// </summary>
    [JsonPropertyName("sourceName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceName { get; init; }

    /// <summary>
    /// Which environments this column belongs to.
    /// null = present in ALL environments (shared).
    /// ["server"] = server-only column.
    /// </summary>
    [JsonPropertyName("environments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Environments { get; set; }
}
