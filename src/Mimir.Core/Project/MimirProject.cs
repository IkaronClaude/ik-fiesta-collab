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
    /// Import sources used to create this project (label → path).
    /// e.g. { "server": "Z:\\Server\\9Data", "client": "Z:\\Client\\9Data" }
    /// </summary>
    [JsonPropertyName("sources")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Sources { get; set; }

    /// <summary>
    /// Named environments with import/build paths.
    /// Replaces Sources dict for multi-environment projects.
    /// </summary>
    [JsonPropertyName("environments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, EnvironmentConfig>? Environments { get; set; }

    /// <summary>
    /// Table name -> relative path to the .json table file.
    /// </summary>
    [JsonPropertyName("tables")]
    public Dictionary<string, string> Tables { get; init; } = [];
}

public sealed class EnvironmentConfig
{
    [JsonPropertyName("importPath")]
    public required string ImportPath { get; init; }

    [JsonPropertyName("buildPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuildPath { get; init; }

    /// <summary>
    /// Optional directory of files to copy verbatim into build output,
    /// overwriting anything already placed there by table builds or copyFile actions.
    /// </summary>
    [JsonPropertyName("overridesPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OverridesPath { get; init; }

    /// <summary>
    /// When true, <c>mimir import</c> seeds the pack baseline manifest from this
    /// environment's importPath after each import, so that <c>mimir pack</c> only
    /// distributes files that differ from the stock source. Set this on client envs;
    /// leave unset (false) for server envs that are never packed.
    /// </summary>
    [JsonPropertyName("seedPackBaseline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SeedPackBaseline { get; init; }
}
