using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mimir.Cli;
using Mimir.Core;
using Mimir.Core.Models;
using Mimir.Core.Project;
using Mimir.Core.Providers;
using Mimir.Core.Templates;
using Mimir.Shn;
using Mimir.ShineTable;
using Mimir.Sql;
using Shouldly;
using Xunit;

namespace Mimir.Integration.Tests;

/// <summary>
/// Tests for the `mimir pack` command — incremental client patch packaging.
/// Creates a synthetic project with client-env tables, builds, packs, modifies, rebuilds, repacks.
/// </summary>
public class PackCommandTests : IAsyncLifetime
{
    private string _rootDir = null!;
    private string _projectDir = null!;
    private string _buildDir = null!;
    private string _packOutputDir = null!;
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), $"mimir-pack-test-{Guid.NewGuid():N}");
        var envDir = Path.Combine(_rootDir, "client-source");
        _projectDir = Path.Combine(_rootDir, "project");
        _buildDir = Path.Combine(_rootDir, "build");
        _packOutputDir = Path.Combine(_rootDir, "pack-output");

        Directory.CreateDirectory(Path.Combine(envDir, "ressystem"));
        Directory.CreateDirectory(_projectDir);

        _sp = BuildServices();
        var shnProvider = _sp.GetServices<IDataProvider>().First(p => p.FormatId == "shn");

        // Create 3 SHN files in the client source
        await WriteShn(shnProvider, Path.Combine(envDir, "ressystem", "ItemInfo.shn"),
            "ItemInfo",
            [Col("ID", ColumnType.UInt32, 4, 3), Col("Name", ColumnType.String, 32, 9)],
            [Row(("ID", (uint)1), ("Name", "Sword")), Row(("ID", (uint)2), ("Name", "Shield"))]);

        await WriteShn(shnProvider, Path.Combine(envDir, "ressystem", "MobInfo.shn"),
            "MobInfo",
            [Col("ID", ColumnType.UInt32, 4, 3), Col("Level", ColumnType.UInt16, 2, 2)],
            [Row(("ID", (uint)1), ("Level", (ushort)10))]);

        await WriteShn(shnProvider, Path.Combine(envDir, "ressystem", "ColorInfo.shn"),
            "ColorInfo",
            [Col("ID", ColumnType.UInt32, 4, 3), Col("ColorR", ColumnType.Byte, 1, 1)],
            [Row(("ID", (uint)1), ("ColorR", (byte)255))]);

        // Write mimir.json and environment config
        var projectService = _sp.GetRequiredService<IProjectService>();
        var project = new MimirProject();
        await projectService.SaveProjectAsync(_projectDir, project);
        EnvironmentStore.Save(_projectDir, "client", new EnvironmentConfig { ImportPath = envDir });

        // Run init-template → import pipeline
        await RunInitTemplate();
        await RunImport();
    }

    public Task DisposeAsync()
    {
        _sp.Dispose();
        try { Directory.Delete(_rootDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    // ==================== Pack Tests ====================

    [Fact]
    public async Task FirstPack_CreatesVersion1Zip_WithAllFiles()
    {
        await RunBuildClient();
        await RunPack();

        var zipPath = Path.Combine(_packOutputDir, "patches", "patch-v1.zip");
        File.Exists(zipPath).ShouldBeTrue($"Expected {zipPath} to exist");

        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet();
        entries.ShouldContain("ressystem/ItemInfo.shn");
        entries.ShouldContain("ressystem/MobInfo.shn");
        entries.ShouldContain("ressystem/ColorInfo.shn");
        entries.Count.ShouldBe(3);
    }

    [Fact]
    public async Task FirstPack_CreatesManifestVersion1()
    {
        await RunBuildClient();
        await RunPack();

        var manifestPath = Path.Combine(_projectDir, ".mimir-pack-manifest-client.json");
        File.Exists(manifestPath).ShouldBeTrue();

        var manifest = JsonSerializer.Deserialize<PackManifest>(
            await File.ReadAllTextAsync(manifestPath))!;
        manifest.Version.ShouldBe(1);
        manifest.Files.Count.ShouldBe(3);
        manifest.Files.Keys.ShouldContain("ressystem/ItemInfo.shn");
        manifest.Files.Keys.ShouldContain("ressystem/MobInfo.shn");
        manifest.Files.Keys.ShouldContain("ressystem/ColorInfo.shn");
    }

    [Fact]
    public async Task FirstPack_CreatesPatchIndex_WithOneEntry()
    {
        await RunBuildClient();
        await RunPack();

        var indexPath = Path.Combine(_packOutputDir, "patch-index.json");
        File.Exists(indexPath).ShouldBeTrue();

        var index = JsonSerializer.Deserialize<PatchIndex>(
            await File.ReadAllTextAsync(indexPath))!;
        index.LatestVersion.ShouldBe(1);
        index.Patches.Count.ShouldBe(1);
        index.Patches[0].Version.ShouldBe(1);
        index.Patches[0].Url.ShouldBe("patches/patch-v1.zip");
        index.Patches[0].FileCount.ShouldBe(3);
        index.Patches[0].SizeBytes.ShouldBeGreaterThan(0);
        index.Patches[0].Sha256.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task SecondPack_AfterChange_CreatesVersion2Zip_WithOnlyChangedFile()
    {
        // First pack
        await RunBuildClient();
        await RunPack();

        // Modify one table via SQL edit
        await RunEdit("UPDATE ColorInfo SET ColorR = 128 WHERE ID = 1");

        // Rebuild and repack
        await RunBuildClient();
        await RunPack();

        // v2 zip should exist and contain only ColorInfo
        var zipPath = Path.Combine(_packOutputDir, "patches", "patch-v2.zip");
        File.Exists(zipPath).ShouldBeTrue($"Expected {zipPath} to exist");

        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet();
        entries.ShouldContain("ressystem/ColorInfo.shn");
        entries.Count.ShouldBe(1); // only the changed file
    }

    [Fact]
    public async Task SecondPack_PatchIndex_HasTwoEntries()
    {
        await RunBuildClient();
        await RunPack();

        await RunEdit("UPDATE ColorInfo SET ColorR = 128 WHERE ID = 1");
        await RunBuildClient();
        await RunPack();

        var index = JsonSerializer.Deserialize<PatchIndex>(
            await File.ReadAllTextAsync(Path.Combine(_packOutputDir, "patch-index.json")))!;
        index.LatestVersion.ShouldBe(2);
        index.Patches.Count.ShouldBe(2);
        index.Patches[0].Version.ShouldBe(1);
        index.Patches[1].Version.ShouldBe(2);
    }

    [Fact]
    public async Task SecondPack_ManifestVersion_IsUpdated()
    {
        await RunBuildClient();
        await RunPack();

        await RunEdit("UPDATE ColorInfo SET ColorR = 128 WHERE ID = 1");
        await RunBuildClient();
        await RunPack();

        var manifest = JsonSerializer.Deserialize<PackManifest>(
            await File.ReadAllTextAsync(Path.Combine(_projectDir, ".mimir-pack-manifest-client.json")))!;
        manifest.Version.ShouldBe(2);
        manifest.Files.Count.ShouldBe(3); // still tracks all files
    }

    [Fact]
    public async Task NoChangePack_ProducesNoNewZip()
    {
        await RunBuildClient();
        await RunPack();

        // Pack again without changes
        var (exitMessage, _) = await RunPack();
        exitMessage.ShouldContain("No changes");

        // Should still be only v1
        File.Exists(Path.Combine(_packOutputDir, "patches", "patch-v2.zip")).ShouldBeFalse();
    }

    [Fact]
    public async Task Pack_PatchIndexSha256_MatchesActualZip()
    {
        await RunBuildClient();
        await RunPack();

        var index = JsonSerializer.Deserialize<PatchIndex>(
            await File.ReadAllTextAsync(Path.Combine(_packOutputDir, "patch-index.json")))!;
        var zipBytes = await File.ReadAllBytesAsync(
            Path.Combine(_packOutputDir, "patches", "patch-v1.zip"));
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(zipBytes));

        index.Patches[0].Sha256.ShouldBe(actualHash);
    }

    [Fact]
    public async Task Pack_WithBaseUrl_PrefixesUrls()
    {
        await RunBuildClient();
        await RunPack(baseUrl: "https://patches.example.com/");

        var index = JsonSerializer.Deserialize<PatchIndex>(
            await File.ReadAllTextAsync(Path.Combine(_packOutputDir, "patch-index.json")))!;
        index.Patches[0].Url.ShouldBe("https://patches.example.com/patches/patch-v1.zip");
    }

    // ==================== Override Tests ====================

    [Fact]
    public async Task Pack_IncludesOverrideFiles()
    {
        await RunBuildClient();

        // Add an override file (e.g., a texture)
        var overrideDir = Path.Combine(_projectDir, "overrides", "client", "ressystem");
        Directory.CreateDirectory(overrideDir);
        await File.WriteAllBytesAsync(Path.Combine(overrideDir, "custom_texture.nif"), [0xDE, 0xAD, 0xBE, 0xEF]);

        await RunPack();

        var zipPath = Path.Combine(_packOutputDir, "patches", "patch-v1.zip");
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet();
        entries.ShouldContain("ressystem/custom_texture.nif");
        entries.Count.ShouldBe(4); // 3 SHN + 1 override
    }

    [Fact]
    public async Task Pack_OverrideFileOverwritesBuildFile()
    {
        await RunBuildClient();

        // Override a built SHN file with custom content
        var overrideDir = Path.Combine(_projectDir, "overrides", "client", "ressystem");
        Directory.CreateDirectory(overrideDir);
        var overrideContent = new byte[] { 0x01, 0x02, 0x03 };
        await File.WriteAllBytesAsync(Path.Combine(overrideDir, "ColorInfo.shn"), overrideContent);

        await RunPack();

        // Extract the zip and verify the override content won
        var zipPath = Path.Combine(_packOutputDir, "patches", "patch-v1.zip");
        using var zip = ZipFile.OpenRead(zipPath);
        var colorEntry = zip.GetEntry("ressystem/ColorInfo.shn");
        colorEntry.ShouldNotBeNull();
        using var stream = colorEntry!.Open();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        ms.ToArray().ShouldBe(overrideContent);
    }

    [Fact]
    public async Task Pack_OverrideManifestTracksOverrideFiles()
    {
        await RunBuildClient();

        var overrideDir = Path.Combine(_projectDir, "overrides", "client", "ressystem");
        Directory.CreateDirectory(overrideDir);
        await File.WriteAllBytesAsync(Path.Combine(overrideDir, "icon.png"), [0xFF, 0xD8]);

        await RunPack();

        var manifest = JsonSerializer.Deserialize<PackManifest>(
            await File.ReadAllTextAsync(Path.Combine(_projectDir, ".mimir-pack-manifest-client.json")))!;
        manifest.Files.Keys.ShouldContain("ressystem/icon.png");
        manifest.Files.Count.ShouldBe(4); // 3 SHN + 1 override
    }

    [Fact]
    public async Task Pack_ChangedOverride_CreatesIncrementalPatch()
    {
        await RunBuildClient();

        // First pack with an override
        var overrideDir = Path.Combine(_projectDir, "overrides", "client", "ressystem");
        Directory.CreateDirectory(overrideDir);
        await File.WriteAllBytesAsync(Path.Combine(overrideDir, "splash.png"), [0x01]);
        await RunPack();

        // Modify the override
        await File.WriteAllBytesAsync(Path.Combine(overrideDir, "splash.png"), [0x02]);
        await RunPack();

        // v2 should contain only the changed override
        var zipPath = Path.Combine(_packOutputDir, "patches", "patch-v2.zip");
        File.Exists(zipPath).ShouldBeTrue();
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet();
        entries.ShouldContain("ressystem/splash.png");
        entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Pack_OverrideInSubdir_PreservesPath()
    {
        await RunBuildClient();

        // Override in a nested subdirectory
        var overrideDir = Path.Combine(_projectDir, "overrides", "client", "ressystem", "textures", "ui");
        Directory.CreateDirectory(overrideDir);
        await File.WriteAllBytesAsync(Path.Combine(overrideDir, "button.dds"), [0xAA, 0xBB]);

        await RunPack();

        var zipPath = Path.Combine(_packOutputDir, "patches", "patch-v1.zip");
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet();
        entries.ShouldContain("ressystem/textures/ui/button.dds");
    }

    // ==================== Pipeline Helpers ====================

    private async Task RunInitTemplate()
    {
        var providers = _sp.GetServices<IDataProvider>().ToList();
        var logger = _sp.GetRequiredService<ILogger<PackCommandTests>>();
        var allEnvs = EnvironmentStore.LoadAll(_projectDir);

        var envTables = new Dictionary<(string table, string env), TableFile>();
        foreach (var (envName, envConfig) in allEnvs)
        {
            if (envConfig.ImportPath == null) continue;
            var sourceDir = new DirectoryInfo(envConfig.ImportPath);
            var tables = await ReadAllTables(sourceDir, providers, logger);
            foreach (var (tableName, (tableFile, _)) in tables)
                envTables[(tableName, envName)] = tableFile;
        }

        var template = TemplateGenerator.Generate(envTables, allEnvs.Keys.ToList());
        await TemplateResolver.SaveAsync(_projectDir, template);
    }

    private async Task RunImport()
    {
        var projectService = _sp.GetRequiredService<IProjectService>();
        var providers = _sp.GetServices<IDataProvider>().ToList();
        var logger = _sp.GetRequiredService<ILogger<PackCommandTests>>();
        var manifest = await projectService.LoadProjectAsync(_projectDir);
        var template = await TemplateResolver.LoadAsync(_projectDir);
        var allEnvs = EnvironmentStore.LoadAll(_projectDir);

        var rawTables = new Dictionary<(string tableName, string envName), TableFile>();
        var rawRelDirs = new Dictionary<(string tableName, string envName), string>();

        foreach (var (envName, envConfig) in allEnvs)
        {
            if (envConfig.ImportPath == null) continue;
            var sourceDir = new DirectoryInfo(envConfig.ImportPath);
            var tables = await ReadAllTables(sourceDir, providers, logger);
            foreach (var (tableName, (tableFile, relDir)) in tables)
            {
                rawTables[(tableName, envName)] = tableFile;
                rawRelDirs[(tableName, envName)] = relDir;
            }
        }

        // Execute merge actions
        var mergedTables = new Dictionary<string, TableFile>();
        var allEnvMetadata = new Dictionary<string, Dictionary<string, EnvMergeMetadata>>();
        var tablesHandledByActions = new HashSet<string>();

        foreach (var action in template.Actions.Where(a => a.Action is "copy" or "merge"))
        {
            if (action.Action == "copy" && action.From != null && action.To != null)
            {
                var key = (action.From.Table, action.From.Env);
                if (rawTables.TryGetValue(key, out var raw))
                {
                    mergedTables[action.To] = raw;
                    tablesHandledByActions.Add(action.To);
                    if (!allEnvMetadata.ContainsKey(action.To)) allEnvMetadata[action.To] = new();
                    allEnvMetadata[action.To][action.From.Env] = new EnvMergeMetadata
                    {
                        ColumnOrder = raw.Columns.Select(c => c.Name).ToList(),
                        ColumnOverrides = new(), ColumnRenames = new(),
                        SourceRelDir = rawRelDirs.GetValueOrDefault(key, "")
                    };
                }
            }
            else if (action.Action == "merge" && action.From != null && action.Into != null && action.On != null)
            {
                var key = (action.From.Table, action.From.Env);
                if (!rawTables.TryGetValue(key, out var source)) continue;
                if (!mergedTables.TryGetValue(action.Into, out var target)) continue;

                var result = TableMerger.Merge(target, source, action.On, action.From.Env,
                    action.ColumnStrategy ?? "auto", action.ConflictStrategy ?? "report");
                mergedTables[action.Into] = result.Table;
                if (!allEnvMetadata.ContainsKey(action.Into)) allEnvMetadata[action.Into] = new();
                foreach (var (eName, eMeta) in result.EnvMetadata)
                {
                    allEnvMetadata[action.Into][eName] = eMeta;
                    eMeta.SourceRelDir = rawRelDirs.GetValueOrDefault((action.From.Table, eName), "");
                }
            }
        }

        // Passthrough tables
        foreach (var tableName in rawTables.Keys.Select(k => k.tableName).Distinct())
        {
            if (tablesHandledByActions.Contains(tableName)) continue;
            var envs = rawTables.Keys.Where(k => k.tableName == tableName).Select(k => k.envName).ToList();
            if (envs.Count == 1)
            {
                var key = (tableName, envs[0]);
                mergedTables[tableName] = rawTables[key];
                if (!allEnvMetadata.ContainsKey(tableName)) allEnvMetadata[tableName] = new();
                allEnvMetadata[tableName][envs[0]] = new EnvMergeMetadata
                {
                    ColumnOrder = rawTables[key].Columns.Select(c => c.Name).ToList(),
                    ColumnOverrides = new(), ColumnRenames = new(),
                    SourceRelDir = rawRelDirs.GetValueOrDefault(key, "")
                };
            }
        }

        // Write tables
        manifest.Tables.Clear();
        foreach (var (tableName, tableFile) in mergedTables.OrderBy(kv => kv.Key))
        {
            var relativePath = $"data/{tableFile.Header.SourceFormat}/{tableFile.Header.TableName}.json";
            if (allEnvMetadata.TryGetValue(tableName, out var envMetas))
            {
                var metadata = tableFile.Header.Metadata ?? new Dictionary<string, object>();
                metadata[SourceOrigin.MetadataKey] = envMetas.Count > 1
                    ? EnvironmentInfo.MergedOrigin : envMetas.Keys.First();

                foreach (var (eName, eMeta) in envMetas)
                {
                    metadata[eName] = new Dictionary<string, object?>
                    {
                        ["columnOrder"] = eMeta.ColumnOrder,
                        ["columnOverrides"] = eMeta.ColumnOverrides.Count > 0 ? eMeta.ColumnOverrides : null,
                        ["columnRenames"] = eMeta.ColumnRenames.Count > 0 ? eMeta.ColumnRenames : null,
                        ["sourceRelDir"] = eMeta.SourceRelDir
                    };
                }

                var enriched = new TableFile
                {
                    Header = new TableHeader
                    {
                        TableName = tableFile.Header.TableName,
                        SourceFormat = tableFile.Header.SourceFormat,
                        Metadata = metadata
                    },
                    Columns = tableFile.Columns,
                    Data = tableFile.Data,
                    RowEnvironments = tableFile.RowEnvironments
                };
                await projectService.WriteTableFileAsync(_projectDir, relativePath, enriched);
            }
            else
            {
                await projectService.WriteTableFileAsync(_projectDir, relativePath, tableFile);
            }
            manifest.Tables[tableName] = relativePath;
        }
        await projectService.SaveProjectAsync(_projectDir, manifest);
    }

    private async Task RunBuildClient()
    {
        var projectService = _sp.GetRequiredService<IProjectService>();
        var providers = _sp.GetServices<IDataProvider>().ToDictionary(p => p.FormatId);
        var manifest = await projectService.LoadProjectAsync(_projectDir);

        var outputDir = Path.Combine(_buildDir, "client");
        Directory.CreateDirectory(outputDir);

        foreach (var (name, entryPath) in manifest.Tables)
        {
            var tableFile = await projectService.ReadTableFileAsync(_projectDir, entryPath);
            var origin = tableFile.Header.Metadata?.TryGetValue(SourceOrigin.MetadataKey, out var o) == true
                ? o?.ToString() : null;

            EnvMergeMetadata? envMeta = null;
            if (tableFile.Header.Metadata != null
                && tableFile.Header.Metadata.TryGetValue("client", out var rawMeta)
                && rawMeta is JsonElement je)
            {
                envMeta = EnvMergeMetadata.FromJsonElement(je);
            }

            TableFile outputTable;
            if (origin == EnvironmentInfo.MergedOrigin)
            {
                if (envMeta == null) continue;
                outputTable = TableSplitter.Split(tableFile, "client", envMeta);
            }
            else if (origin != null && origin != EnvironmentInfo.MergedOrigin && origin != "client")
            {
                continue;
            }
            else
            {
                outputTable = tableFile;
            }

            var entry = new TableEntry
            {
                Schema = new TableSchema
                {
                    TableName = outputTable.Header.TableName,
                    SourceFormat = outputTable.Header.SourceFormat,
                    Columns = outputTable.Columns,
                    Metadata = outputTable.Header.Metadata
                },
                Rows = outputTable.Data
            };

            if (!providers.TryGetValue(entry.Schema.SourceFormat, out var provider)) continue;
            var ext = provider.SupportedExtensions[0].TrimStart('.');
            var relDir = envMeta?.SourceRelDir;
            var dir = string.IsNullOrEmpty(relDir) ? outputDir : Path.Combine(outputDir, relDir);
            Directory.CreateDirectory(dir);
            await provider.WriteAsync(Path.Combine(dir, $"{name}.{ext}"), [entry]);
        }
    }

    private async Task RunEdit(string sql)
    {
        var projectService = _sp.GetRequiredService<IProjectService>();
        using var engine = _sp.GetRequiredService<Mimir.Sql.ISqlEngine>();
        var manifest = await projectService.LoadProjectAsync(_projectDir);

        var tableHeaders = new Dictionary<string, TableHeader>();
        var tableSchemas = new Dictionary<string, TableSchema>();

        foreach (var (name, entryPath) in manifest.Tables)
        {
            var tableFile = await projectService.ReadTableFileAsync(_projectDir, entryPath);
            var schema = new TableSchema
            {
                TableName = tableFile.Header.TableName,
                SourceFormat = tableFile.Header.SourceFormat,
                Columns = tableFile.Columns,
                Metadata = tableFile.Header.Metadata
            };
            tableHeaders[name] = tableFile.Header;
            tableSchemas[name] = schema;
            engine.LoadTable(new TableEntry { Schema = schema, Rows = tableFile.Data });
        }

        engine.Execute(sql);

        foreach (var (name, entryPath) in manifest.Tables)
        {
            var schema = tableSchemas[name];
            var extracted = engine.ExtractTable(schema);
            var tf = new TableFile
            {
                Header = tableHeaders[name],
                Columns = schema.Columns,
                Data = extracted.Rows
            };
            await projectService.WriteTableFileAsync(_projectDir, entryPath, tf);
        }
    }

    private async Task<(string message, int version)> RunPack(string? baseUrl = null)
    {
        return await PackCommand.ExecuteAsync(_projectDir, _buildDir, _packOutputDir, "client", baseUrl);
    }

    // ==================== Helpers ====================

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddMimirCore();
        services.AddMimirShn();
        services.AddMimirTextTables();
        services.AddMimirSql();
        return services.BuildServiceProvider();
    }

    private static ColumnDefinition Col(string name, ColumnType type, int length, int sourceTypeCode) =>
        new() { Name = name, Type = type, Length = length, SourceTypeCode = sourceTypeCode };

    private static Dictionary<string, object?> Row(params (string key, object? val)[] pairs) =>
        pairs.ToDictionary(p => p.key, p => p.val);

    private static async Task WriteShn(IDataProvider provider, string path, string tableName,
        IReadOnlyList<ColumnDefinition> columns, IReadOnlyList<Dictionary<string, object?>> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var entry = new TableEntry
        {
            Schema = new TableSchema
            {
                TableName = tableName,
                SourceFormat = "shn",
                Columns = columns,
                Metadata = new Dictionary<string, object>
                {
                    ["cryptHeader"] = Convert.ToBase64String(new byte[32]),
                    ["header"] = (uint)1,
                    ["defaultRecordLength"] = CalculateRecordLength(columns)
                }
            },
            Rows = rows
        };
        await provider.WriteAsync(path, [entry]);
    }

    private static uint CalculateRecordLength(IReadOnlyList<ColumnDefinition> columns)
    {
        uint length = 2;
        foreach (var col in columns) length += (uint)col.Length;
        return length;
    }

    private static async Task<Dictionary<string, (TableFile file, string relDir)>> ReadAllTables(
        DirectoryInfo sourceDir, List<IDataProvider> providers, ILogger logger)
    {
        var tables = new Dictionary<string, (TableFile file, string relDir)>();
        foreach (var file in sourceDir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var provider = providers.FirstOrDefault(p => p.CanHandle(file.FullName));
            if (provider is null) continue;
            var entries = await provider.ReadAsync(file.FullName);
            var relDir = Path.GetDirectoryName(
                Path.GetRelativePath(sourceDir.FullName, file.FullName))?.Replace('\\', '/') ?? "";
            foreach (var entry in entries)
            {
                var tableFile = new TableFile
                {
                    Header = new TableHeader
                    {
                        TableName = entry.Schema.TableName,
                        SourceFormat = entry.Schema.SourceFormat,
                        Metadata = entry.Schema.Metadata
                    },
                    Columns = entry.Schema.Columns,
                    Data = entry.Rows
                };
                tables[entry.Schema.TableName] = (tableFile, relDir);
            }
        }
        return tables;
    }
}
