namespace Mimir.Core.Models;

/// <summary>
/// Where a table was imported from. Stored in TableHeader.Metadata["sourceOrigin"].
/// </summary>
public static class SourceOrigin
{
    public const string Server = "server";
    public const string Client = "client";
    public const string Shared = "shared";
    public const string MetadataKey = "sourceOrigin";
}
