using System.Text.Json.Serialization;

namespace Mimir.Core.Project;

public sealed class EnvironmentConfig
{
    [JsonPropertyName("importPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImportPath { get; set; }

    [JsonPropertyName("buildPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuildPath { get; set; }

    /// <summary>
    /// Optional directory of files to copy verbatim into build output,
    /// overwriting anything already placed there by table builds or copyFile actions.
    /// </summary>
    [JsonPropertyName("overridesPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OverridesPath { get; set; }

    /// <summary>
    /// When true, <c>mimir import</c> seeds the pack baseline manifest from this
    /// environment's importPath after each import, so that <c>mimir pack</c> only
    /// distributes files that differ from the stock source. Set this on client envs;
    /// leave unset (false) for server envs that are never packed.
    /// </summary>
    [JsonPropertyName("seedPackBaseline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SeedPackBaseline { get; set; }
}
