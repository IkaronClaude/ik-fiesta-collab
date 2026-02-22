using System.Text.Json.Serialization;

namespace Mimir.Core.Project;

/// <summary>
/// The mimir.json project manifest. Tracks all table files in the project.
/// Schemas and data live in the individual table .json files.
/// Environment config lives in environments/<name>.json files (see EnvironmentStore).
/// </summary>
public sealed class MimirProject
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>
    /// Import sources used to create this project (label → path).
    /// Legacy field — superseded by environments/<name>.json files.
    /// </summary>
    [JsonPropertyName("sources")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Sources { get; set; }

    /// <summary>
    /// Table name -> relative path to the .json table file.
    /// </summary>
    [JsonPropertyName("tables")]
    public Dictionary<string, string> Tables { get; init; } = [];
}
