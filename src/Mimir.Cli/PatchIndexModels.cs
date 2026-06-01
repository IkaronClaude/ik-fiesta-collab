using System.Text.Json.Serialization;

namespace Mimir.Cli;

// Patch-index models. The pack/seed logic moved to the standalone
// ik-fiesta-patch-server repo, but the `snapshot` command still *reads* a
// patch-index.json (to reconstruct a patched client), so these read-side DTOs
// stay here. They mirror the index the external packer writes.

/// <summary>Hosted patch index listing all available patches.</summary>
public sealed class PatchIndex
{
    [JsonPropertyName("latestVersion")]
    public int LatestVersion { get; set; }

    [JsonPropertyName("patches")]
    public List<PatchEntry> Patches { get; set; } = new();

    [JsonPropertyName("masterPatch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MasterPatchEntry? MasterPatch { get; set; }

    [JsonPropertyName("minIncrementalVersion")]
    public int MinIncrementalVersion { get; set; } = 1;
}

public sealed class PatchEntry
{
    [JsonPropertyName("version")]   public int Version    { get; set; }
    [JsonPropertyName("url")]       public string Url     { get; set; } = "";
    [JsonPropertyName("sha256")]    public string Sha256  { get; set; } = "";
    [JsonPropertyName("fileCount")] public int FileCount  { get; set; }
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
}

public sealed class MasterPatchEntry
{
    [JsonPropertyName("version")]   public int Version    { get; set; }
    [JsonPropertyName("url")]       public string Url     { get; set; } = "";
    [JsonPropertyName("sha256")]    public string Sha256  { get; set; } = "";
    [JsonPropertyName("fileCount")] public int FileCount  { get; set; }
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
}
