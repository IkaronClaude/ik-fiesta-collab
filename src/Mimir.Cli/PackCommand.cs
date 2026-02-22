using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mimir.Core.Providers;

namespace Mimir.Cli;

/// <summary>
/// Tracks file hashes from the last pack, stored as .mimir-pack-manifest.json in the project dir.
/// </summary>
public sealed class PackManifest
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("files")]
    public Dictionary<string, string> Files { get; set; } = new();
}

/// <summary>
/// Hosted patch index listing all available patches.
/// </summary>
public sealed class PatchIndex
{
    [JsonPropertyName("latestVersion")]
    public int LatestVersion { get; set; }

    [JsonPropertyName("patches")]
    public List<PatchEntry> Patches { get; set; } = new();
}

public sealed class PatchEntry
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
}

/// <summary>
/// The pack command logic, extracted for testability.
/// Builds incremental patch zips from client build output.
/// </summary>
public static class PackCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<(string message, int version)> ExecuteAsync(
        string projectDir, string buildDir, string outputDir, string envName, string? baseUrl = null)
    {
        // Step 1: Find build output for the environment
        var clientBuildDir = Path.Combine(buildDir, envName);
        if (!Directory.Exists(clientBuildDir))
            return ("Build directory does not exist. Run 'mimir build --all' first.", 0);

        // Step 2: Hash all files in the build output
        // Maps relative path → (sha256, absolute source path)
        var currentFiles = new Dictionary<string, (string hash, string sourcePath)>();
        foreach (var file in Directory.EnumerateFiles(clientBuildDir, "*", SearchOption.AllDirectories))
        {
            var relPath = Path.GetRelativePath(clientBuildDir, file).Replace('\\', '/');
            var hash = await HashFileAsync(file);
            currentFiles[relPath] = (hash, file);
        }

        // Step 2b: Overlay override files from project overrides/<env>/
        var overrideDir = Path.Combine(projectDir, "overrides", envName);
        if (Directory.Exists(overrideDir))
        {
            foreach (var file in Directory.EnumerateFiles(overrideDir, "*", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(overrideDir, file).Replace('\\', '/');
                var hash = await HashFileAsync(file);
                currentFiles[relPath] = (hash, file); // override wins over build output
            }
        }

        if (currentFiles.Count == 0)
            return ("No files found in build output.", 0);

        // Step 3: Load previous manifest
        // The baseline (v0) is established by `mimir import` — run with --retain-pack-baseline to skip reseed.
        var manifestPath = Path.Combine(projectDir, ".mimir-pack-manifest.json");
        PackManifest? previousManifest = null;
        if (File.Exists(manifestPath))
        {
            previousManifest = JsonSerializer.Deserialize<PackManifest>(
                await File.ReadAllTextAsync(manifestPath), JsonOptions);
        }

        // Step 4: Find changed files
        var changedFiles = new List<string>();
        foreach (var (relPath, (hash, _)) in currentFiles)
        {
            if (previousManifest == null
                || !previousManifest.Files.TryGetValue(relPath, out var prevHash)
                || prevHash != hash)
            {
                changedFiles.Add(relPath);
            }
        }

        if (changedFiles.Count == 0)
            return ("No changes to pack.", previousManifest?.Version ?? 0);

        // Step 5: Determine version
        var nextVersion = (previousManifest?.Version ?? 0) + 1;

        // Step 6: Create zip with only changed files
        var patchesDir = Path.Combine(outputDir, "patches");
        Directory.CreateDirectory(patchesDir);
        var zipFileName = $"patch-v{nextVersion}.zip";
        var zipPath = Path.Combine(patchesDir, zipFileName);

        // Delete if exists (re-packing same version)
        if (File.Exists(zipPath)) File.Delete(zipPath);

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var relPath in changedFiles.OrderBy(f => f))
            {
                var sourcePath = currentFiles[relPath].sourcePath;
                zip.CreateEntryFromFile(sourcePath, relPath);
            }
        }

        // Step 7: Hash the zip
        var zipHash = await HashFileAsync(zipPath);
        var zipSize = new FileInfo(zipPath).Length;

        // Step 8: Update patch-index.json
        var indexPath = Path.Combine(outputDir, "patch-index.json");
        PatchIndex index;
        if (File.Exists(indexPath))
        {
            index = JsonSerializer.Deserialize<PatchIndex>(
                await File.ReadAllTextAsync(indexPath), JsonOptions) ?? new PatchIndex();
        }
        else
        {
            index = new PatchIndex();
        }

        var patchUrl = $"patches/{zipFileName}";
        if (!string.IsNullOrEmpty(baseUrl))
        {
            var prefix = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
            patchUrl = prefix + patchUrl;
        }

        index.Patches.Add(new PatchEntry
        {
            Version = nextVersion,
            Url = patchUrl,
            Sha256 = zipHash,
            FileCount = changedFiles.Count,
            SizeBytes = zipSize
        });
        index.LatestVersion = nextVersion;

        await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(index, JsonOptions));

        // Step 9: Update pack manifest
        var newManifest = new PackManifest
        {
            Version = nextVersion,
            Files = currentFiles.ToDictionary(kv => kv.Key, kv => kv.Value.hash)
        };
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(newManifest, JsonOptions));

        return ($"Packed version {nextVersion}: {changedFiles.Count} files, {zipSize} bytes", nextVersion);
    }

    /// <summary>
    /// Seeds the pack manifest (version 0) from source import paths so that the first
    /// <c>mimir pack</c> only includes files that actually differ from what players already have.
    /// Called by the import command after a successful import.
    /// Only hashes files that at least one provider can handle — same filter as import.
    /// </summary>
    /// <param name="manifestPath">Path to write .mimir-pack-manifest.json</param>
    /// <param name="envImportPaths">env name → importPath from mimir.json environments</param>
    /// <param name="providers">Data providers used to filter files (same as import)</param>
    public static async Task<int> SeedBaselineAsync(
        string manifestPath,
        Dictionary<string, string> envImportPaths,
        IReadOnlyList<IDataProvider> providers)
    {
        var allFiles = new Dictionary<string, string>();
        foreach (var (_, importPath) in envImportPaths)
        {
            if (!Directory.Exists(importPath))
                continue;

            // Manifest root determines the relpath prefix in manifest keys.
            // If the parent is not a drive root, use it so the importPath dir name is included
            // as a prefix — matching how build output lays out files.
            // e.g. Z:/ClientSource/ressystem → root Z:/ClientSource → "ressystem/ItemInfo.shn"
            //      Z:/Server               → root Z:/Server        → "9Data/Shine/ItemInfo.shn"
            // We enumerate importPath only (not the parent) to avoid hashing unrelated files.
            var manifestRoot = GetManifestRoot(importPath);

            foreach (var file in Directory.EnumerateFiles(importPath, "*", SearchOption.AllDirectories))
            {
                // Apply the same file filter as import — skip files no provider handles
                if (!providers.Any(p => p.CanHandle(file)))
                    continue;

                var relPath = Path.GetRelativePath(manifestRoot, file).Replace('\\', '/');
                allFiles[relPath] = await HashFileAsync(file);
            }
        }

        var manifest = new PackManifest { Version = 0, Files = allFiles };
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        return allFiles.Count;
    }

    /// <summary>
    /// Computes the manifest root for a given importPath.
    /// If the parent directory is not a drive root, the parent is the root (so the last
    /// directory component is included as a path prefix in manifest keys, matching build output).
    /// e.g. "Z:/ClientSource/ressystem" → root "Z:/ClientSource" → relpath "ressystem/ItemInfo.shn"
    ///      "Z:/Server" → root "Z:/Server" → relpath "9Data/Shine/ItemInfo.shn"
    /// </summary>
    private static string GetManifestRoot(string importPath)
    {
        var normalized = importPath.TrimEnd('/', '\\');
        var parent = Path.GetDirectoryName(normalized);
        // Drive roots like "Z:\" have length 3; treat importPath itself as root in that case
        return (parent == null || parent.Length <= 3) ? normalized : parent;
    }

    private static async Task<string> HashFileAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
