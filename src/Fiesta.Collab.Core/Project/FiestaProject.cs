using System.Text.Json.Serialization;

namespace Fiesta.Collab.Core.Project;

/// <summary>
/// The fiesta.json project manifest. Tracks all table files in the project.
/// Schemas and data live in the individual table .json files.
/// Environment config lives in environments/<name>.json files (see EnvironmentStore).
/// </summary>
public sealed class FiestaProject
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

    /// <summary>
    /// Build variants: name -> the layer directories (relative to the project) whose migrations are applied, in this
    /// order, on top of data/ when building that variant (`fiesta build --variant name`). data/ never carries them.
    /// </summary>
    [JsonPropertyName("variants")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<string>>? Variants { get; set; }
}
