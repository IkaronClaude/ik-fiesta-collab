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
    /// Optional path to server binary files (exes, DLLs, GamigoZR, etc.) that live
    /// outside the data directory. Separates "data Mimir builds" from "binaries Mimir
    /// doesn't touch". Used by deploy scripts to locate binaries for the server image.
    /// Typically set to the server root (e.g. Z:/Server) while <c>buildPath</c> targets
    /// the 9Data subdir (e.g. build/server/9Data).
    /// </summary>
    [JsonPropertyName("deployPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeployPath { get; set; }

    /// <summary>
    /// When true, <c>mimir init-template</c> treats this environment as a passthrough
    /// source: all non-table files (binaries, config files, etc.) found under
    /// <c>importPath</c> are added as <c>copyFile</c> actions in the template so they
    /// are copied verbatim to build output. Automatically set for <c>--type server</c>.
    /// </summary>
    [JsonPropertyName("passthrough")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Passthrough { get; set; }

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
