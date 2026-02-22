using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mimir.Core.Providers;

namespace Mimir.Cli;

/// <summary>
/// Tracks file hashes from the last pack, stored as .mimir-pack-manifest-{env}.json in the project dir.
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

        // Step 3: Load previous manifest (per-env).
        // The baseline (v0) is established by `mimir import` — run with --retain-pack-baseline to skip reseed.
        var manifestPath = Path.Combine(projectDir, $".mimir-pack-manifest-{envName}.json");
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
    /// Seeds the per-env pack manifest (version 0) by hashing the original source files at
    /// <paramref name="importPath"/>. Called by the build command after a successful build.
    /// The baseline represents what players currently have (the stock client), so the first
    /// <c>mimir pack</c> includes every file that Mimir's output differs from — including
    /// files that differ only due to roundtrip fidelity (e.g. zeroed string padding).
    /// This is intentional: players must receive Mimir's rebuilt versions to pass client
    /// integrity checks, even for files with no intentional data changes.
    /// After the first patch lands, subsequent packs only include actually-changed files.
    /// </summary>
    public static async Task<int> SeedBaselineAsync(
        string projectDir,
        string envName,
        string importPath,
        IReadOnlyList<IDataProvider> providers)
    {
        var manifestPath = Path.Combine(projectDir, $".mimir-pack-manifest-{envName}.json");

        var allFiles = new Dictionary<string, string>();
        if (Directory.Exists(importPath))
        {
            foreach (var file in Directory.EnumerateFiles(importPath, "*", SearchOption.AllDirectories))
            {
                if (!providers.Any(p => p.CanHandle(file)))
                    continue;

                // Relpath relative to importPath matches build output layout:
                // e.g. importPath=Z:/ClientSource, file=.../ClientSource/ressystem/ItemInfo.shn
                //      → relpath "ressystem/ItemInfo.shn" == pack relpath in build/client/
                var relPath = Path.GetRelativePath(importPath, file).Replace('\\', '/');
                allFiles[relPath] = await HashFileAsync(file);
            }
        }

        var manifest = new PackManifest { Version = 0, Files = allFiles };
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        return allFiles.Count;
    }

    private static async Task<string> HashFileAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
