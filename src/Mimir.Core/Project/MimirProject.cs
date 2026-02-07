using System.Text.Json.Serialization;

namespace Mimir.Core.Project;

/// <summary>
/// The mimir.json project manifest. Tracks all table files in the project.
/// Schemas and data live in the individual table .json files.
/// </summary>
public sealed class MimirProject
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>
    /// Table name -> relative path to the .json table file.
    /// </summary>
    [JsonPropertyName("tables")]
    public Dictionary<string, string> Tables { get; init; } = [];
}
