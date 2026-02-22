using System.IO.Compression;
using System.Text.Json;

namespace Mimir.Cli;

/// <summary>
/// Builds a complete client snapshot by overlaying all patches on top of the original
/// source import files. The result is a directory representing what a fully-patched
/// client looks like on disk — useful for distributing a fresh client or re-seeding
/// a baseline after many incremental patches have accumulated.
/// </summary>
public static class SnapshotCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Creates a snapshot at <paramref name="outputDir"/> by:
    /// 1. Copying all files from <paramref name="importPath"/> (vanilla client).
    /// 2. Applying every patch in <paramref name="patchesDir"/>/patch-index.json in version order.
    /// </summary>
    public static async Task<SnapshotResult> ExecuteAsync(
        string importPath, string patchesDir, string outputDir)
    {
        // Step 1: Copy all source files → output
        int sourceFiles = 0;
        if (Directory.Exists(importPath))
        {
            foreach (var file in Directory.EnumerateFiles(importPath, "*", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(importPath, file);
                var dest = Path.Combine(outputDir, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
                sourceFiles++;
            }
        }

        // Step 2: Load patch-index.json
        var indexPath = Path.Combine(patchesDir, "patch-index.json");
        if (!File.Exists(indexPath))
            return new SnapshotResult(sourceFiles, 0, 0, LatestVersion: 0, MissingPatches: 0);

        var index = JsonSerializer.Deserialize<PatchIndex>(
            await File.ReadAllTextAsync(indexPath), JsonOptions) ?? new PatchIndex();

        if (index.Patches.Count == 0)
            return new SnapshotResult(sourceFiles, 0, 0, LatestVersion: 0, MissingPatches: 0);

        // Step 3: Apply patches in ascending version order
        int appliedPatches = 0;
        int patchedFiles = 0;
        int missingPatches = 0;

        foreach (var patch in index.Patches.OrderBy(p => p.Version))
        {
            var zipPath = Path.Combine(patchesDir, "patches", $"patch-v{patch.Version}.zip");
            if (!File.Exists(zipPath))
            {
                missingPatches++;
                continue;
            }

            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                // Skip directory entries (name is empty when FullName ends with /)
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var dest = Path.Combine(outputDir,
                    entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                patchedFiles++;
            }

            appliedPatches++;
        }

        return new SnapshotResult(sourceFiles, appliedPatches, patchedFiles,
            index.LatestVersion, missingPatches);
    }
}

public sealed record SnapshotResult(
    int SourceFiles,
    int AppliedPatches,
    int PatchedFiles,
    int LatestVersion,
    int MissingPatches);
