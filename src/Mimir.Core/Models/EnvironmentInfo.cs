namespace Mimir.Core.Models;

/// <summary>
/// Constants for environment-based table tracking.
/// Replaces the old SourceOrigin class.
/// </summary>
public static class EnvironmentInfo
{
    /// <summary>
    /// Metadata key for environment info in table headers.
    /// </summary>
    public const string MetadataKey = "environments";

    /// <summary>
    /// sourceOrigin value indicating a merged table.
    /// </summary>
    public const string MergedOrigin = "merged";
}
