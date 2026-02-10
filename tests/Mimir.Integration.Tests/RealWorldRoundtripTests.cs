using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mimir.Core;
using Mimir.Core.Models;
using Mimir.Core.Project;
using Mimir.Core.Providers;
using Mimir.Core.Templates;
using Mimir.Shn;
using Mimir.ShineTable;
using Shouldly;
using Xunit;

namespace Mimir.Integration.Tests;

/// <summary>
/// Integration tests that use real game data from external paths.
/// Skips gracefully if MIMIR_SERVER_PATH and MIMIR_CLIENT_PATH are not set.
/// </summary>
public class RealWorldRoundtripTests
{
    private readonly string? _serverPath;
    private readonly string? _clientPath;
    private readonly bool _available;

    public RealWorldRoundtripTests()
    {
        _serverPath = Environment.GetEnvironmentVariable("MIMIR_SERVER_PATH");
        _clientPath = Environment.GetEnvironmentVariable("MIMIR_CLIENT_PATH");
        _available = _serverPath != null && _clientPath != null
            && Directory.Exists(_serverPath) && Directory.Exists(_clientPath);
    }

    [Fact]
    public async Task InitTemplate_RealData_GeneratesActions()
    {
        if (!_available) return;

        using var sp = BuildServices();
        var providers = sp.GetServices<IDataProvider>().ToList();
        var logger = sp.GetRequiredService<ILogger<RealWorldRoundtripTests>>();

        var envTables = new Dictionary<(string table, string env), TableFile>();
        var envOrder = new List<string> { "server", "client" };

        var serverTables = await ReadAllTables(new DirectoryInfo(_serverPath!), providers, logger);
        foreach (var (name, (file, _)) in serverTables)
            envTables[(name, "server")] = file;

        var clientTables = await ReadAllTables(new DirectoryInfo(_clientPath!), providers, logger);
        foreach (var (name, (file, _)) in clientTables)
            envTables[(name, "client")] = file;

        var template = TemplateGenerator.Generate(envTables, envOrder);

        var copyCount = template.Actions.Count(a => a.Action == "copy");
        var mergeCount = template.Actions.Count(a => a.Action == "merge");

        copyCount.ShouldBeGreaterThan(100);
        mergeCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Import_RealData_AllTablesImported()
    {
        if (!_available) return;

        var (manifest, _, sp) = await RunFullPipeline();
        using var _ = sp;

        manifest.Tables.Count.ShouldBeGreaterThan(1000);

        // Some tables should be merged
        var projectService = sp.GetRequiredService<IProjectService>();
        var projectDir = GetProjectDir(sp);
        int mergedCount = 0;
        foreach (var (name, path) in manifest.Tables.Take(200))
        {
            var tf = await projectService.ReadTableFileAsync(projectDir, path);
            var origin = tf.Header.Metadata?.TryGetValue(SourceOrigin.MetadataKey, out var o) == true
                ? o?.ToString() : null;
            if (origin == EnvironmentInfo.MergedOrigin)
                mergedCount++;
        }
        mergedCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Import_RealData_MergedTableHasSourceRelDir()
    {
        if (!_available) return;

        var (manifest, _, sp) = await RunFullPipeline();
        using var _ = sp;

        // Find ItemInfo (a table known to exist in both server and client)
        manifest.Tables.Keys.ShouldContain("ItemInfo");

        var projectService = sp.GetRequiredService<IProjectService>();
        var projectDir = GetProjectDir(sp);
        var tableFile = await projectService.ReadTableFileAsync(projectDir, manifest.Tables["ItemInfo"]);

        var origin = tableFile.Header.Metadata?[SourceOrigin.MetadataKey]?.ToString();
        origin.ShouldBe(EnvironmentInfo.MergedOrigin);

        // Server sourceRelDir should be a Shine-containing path
        var serverMeta = GetEnvMetadata(tableFile, "server");
        serverMeta.ShouldNotBeNull();
        serverMeta!.SourceRelDir.ShouldNotBeNull();

        // Client sourceRelDir should be "ressystem" (SHN files live in ressystem/ under the client root)
        var clientMeta = GetEnvMetadata(tableFile, "client");
        clientMeta.ShouldNotBeNull();
        clientMeta!.SourceRelDir.ShouldBe("ressystem");
    }

    [Fact]
    public async Task Build_RealData_ServerPreservesStructure()
    {
        if (!_available) return;

        var (_, buildDir, sp) = await RunFullPipeline();
        using var _ = sp;

        var serverBuildDir = Path.Combine(buildDir, "server");
        Directory.Exists(serverBuildDir).ShouldBeTrue();

        // Should have SHN files somewhere under the server build dir
        var shnFiles = Directory.EnumerateFiles(serverBuildDir, "*.shn", SearchOption.AllDirectories).ToList();
        shnFiles.Count.ShouldBeGreaterThan(100);
    }

    [Fact]
    public async Task Build_RealData_ClientPreservesRessystemDir()
    {
        if (!_available) return;

        var (_, buildDir, sp) = await RunFullPipeline();
        using var _ = sp;

        var clientBuildDir = Path.Combine(buildDir, "client");
        Directory.Exists(clientBuildDir).ShouldBeTrue();

        // Client SHN files should be under ressystem/ subdirectory
        var ressystemDir = Path.Combine(clientBuildDir, "ressystem");
        Directory.Exists(ressystemDir).ShouldBeTrue($"Expected {ressystemDir} to exist");

        var shnFiles = Directory.EnumerateFiles(ressystemDir, "*.shn", SearchOption.AllDirectories).ToList();
        shnFiles.Count.ShouldBeGreaterThan(50);
    }

    [Fact]
    public async Task Build_RealData_BuiltFilesReadableAndBitIdentical()
    {
        if (!_available) return;

        var (manifest, buildDir, sp) = await RunFullPipeline();
        using var _ = sp;

        var shnProvider = sp.GetServices<IDataProvider>().First(p => p.FormatId == "shn");
        var projectService = sp.GetRequiredService<IProjectService>();
        var projectDir = GetProjectDir(sp);

        // Check a sample of built SHN files from each env
        foreach (var envName in new[] { "server", "client" })
        {
            var envBuildDir = Path.Combine(buildDir, envName);
            if (!Directory.Exists(envBuildDir)) continue;

            var builtShnFiles = Directory.EnumerateFiles(envBuildDir, "*.shn", SearchOption.AllDirectories)
                .Take(10).ToList();

            foreach (var builtFile in builtShnFiles)
            {
                var tableName = Path.GetFileNameWithoutExtension(builtFile);

                // Should be readable without exceptions
                var entries = await shnProvider.ReadAsync(builtFile);
                entries.Count.ShouldBe(1, $"Expected 1 table entry in {builtFile}");
                entries[0].Rows.ShouldNotBeNull($"Rows null in {builtFile}");
            }
        }

        // Bit-equality check: for non-merged single-env SHN tables, built file should match original
        var originalSourceDir = new DirectoryInfo(_serverPath!);
        var serverBuildDir2 = Path.Combine(buildDir, "server");

        // Find tables that are server-only (not merged)
        int bitIdenticalCount = 0;
        foreach (var (name, path) in manifest.Tables.Take(100))
        {
            var tf = await projectService.ReadTableFileAsync(projectDir, path);
            if (tf.Header.SourceFormat != "shn") continue;

            var origin = tf.Header.Metadata?.TryGetValue(SourceOrigin.MetadataKey, out var o) == true
                ? o?.ToString() : null;
            if (origin != "server") continue; // only check single-env server tables

            var envMeta = GetEnvMetadata(tf, "server");
            if (envMeta == null) continue;

            var relDir = envMeta.SourceRelDir ?? "";
            var builtPath = string.IsNullOrEmpty(relDir)
                ? Path.Combine(serverBuildDir2, $"{name}.shn")
                : Path.Combine(serverBuildDir2, relDir, $"{name}.shn");
            var originalPath = string.IsNullOrEmpty(relDir)
                ? Path.Combine(_serverPath!, $"{name}.shn")
                : Path.Combine(_serverPath!, relDir, $"{name}.shn");

            if (!File.Exists(builtPath) || !File.Exists(originalPath)) continue;

            var builtBytes = await File.ReadAllBytesAsync(builtPath);
            var originalBytes = await File.ReadAllBytesAsync(originalPath);

            builtBytes.Length.ShouldBe(originalBytes.Length,
                $"SHN size mismatch for {name}: built={builtBytes.Length} vs original={originalBytes.Length}");
            builtBytes.ShouldBe(originalBytes,
                $"SHN bit-equality failed for server-only table {name}");
            bitIdenticalCount++;
        }

        bitIdenticalCount.ShouldBeGreaterThan(0,
            "Expected at least one server-only SHN table to be bit-identical after roundtrip");
    }

    // ==================== Pipeline ====================

    private async Task<(MimirProject manifest, string buildDir, ServiceProvider sp)> RunFullPipeline()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), $"mimir-real-{Guid.NewGuid():N}");
        var projectDir = Path.Combine(rootDir, "project");
        var buildDir = Path.Combine(rootDir, "build");
        Directory.CreateDirectory(projectDir);

        var sp = BuildServices();
        // Store projectDir in a way we can retrieve it
        _projectDirs[sp] = projectDir;

        var projectService = sp.GetRequiredService<IProjectService>();
        var providers = sp.GetServices<IDataProvider>().ToList();
        var logger = sp.GetRequiredService<ILogger<RealWorldRoundtripTests>>();

        // Write mimir.json
        var project = new MimirProject
        {
            Environments = new Dictionary<string, EnvironmentConfig>
            {
                ["server"] = new() { ImportPath = _serverPath! },
                ["client"] = new() { ImportPath = _clientPath! }
            }
        };
        await projectService.SaveProjectAsync(projectDir, project);

        // Init template
        var envTables = new Dictionary<(string table, string env), TableFile>();
        var envOrder = new List<string> { "server", "client" };

        foreach (var (envName, envConfig) in project.Environments)
        {
            var sourceDir = new DirectoryInfo(envConfig.ImportPath);
            if (!sourceDir.Exists) continue;
            var tables = await ReadAllTables(sourceDir, providers, logger);
            foreach (var (name, (file, _)) in tables)
                envTables[(name, envName)] = file;
        }

        var template = TemplateGenerator.Generate(envTables, envOrder);
        await TemplateResolver.SaveAsync(projectDir, template);

        // Import
        var manifest = await projectService.LoadProjectAsync(projectDir);
        template = await TemplateResolver.LoadAsync(projectDir);

        var rawTables = new Dictionary<(string tableName, string envName), TableFile>();
        var rawRelDirs = new Dictionary<(string tableName, string envName), string>();

        foreach (var (envName, envConfig) in manifest.Environments!)
        {
            var sourceDir = new DirectoryInfo(envConfig.ImportPath);
            if (!sourceDir.Exists) continue;
            var tables = await ReadAllTables(sourceDir, providers, logger);
            foreach (var (name, (file, relDir)) in tables)
            {
                rawTables[(name, envName)] = file;
                rawRelDirs[(name, envName)] = relDir;
            }
        }

        var mergedTables = new Dictionary<string, TableFile>();
        var allEnvMetadata = new Dictionary<string, Dictionary<string, EnvMergeMetadata>>();
        var mergeActions = template.Actions.Where(a => a.Action is "copy" or "merge").ToList();
        var tablesHandledByActions = new HashSet<string>();

        foreach (var action in mergeActions)
        {
            if (action.Action == "copy" && action.From != null && action.To != null)
            {
                var key = (action.From.Table, action.From.Env);
                if (rawTables.TryGetValue(key, out var raw))
                {
                    mergedTables[action.To] = raw;
                    tablesHandledByActions.Add(action.To);
                    if (!allEnvMetadata.ContainsKey(action.To))
                        allEnvMetadata[action.To] = new();
                    allEnvMetadata[action.To][action.From.Env] = new EnvMergeMetadata
                    {
                        ColumnOrder = raw.Columns.Select(c => c.Name).ToList(),
                        ColumnOverrides = new(),
                        ColumnRenames = new(),
                        SourceRelDir = rawRelDirs.GetValueOrDefault(key, "")
                    };
                }
            }
            else if (action.Action == "merge" && action.From != null && action.Into != null && action.On != null)
            {
                var key = (action.From.Table, action.From.Env);
                if (!rawTables.TryGetValue(key, out var source)) continue;
                if (!mergedTables.TryGetValue(action.Into, out var target)) continue;

                var result = TableMerger.Merge(target, source, action.On, action.From.Env, action.ColumnStrategy ?? "auto");
                mergedTables[action.Into] = result.Table;

                if (!allEnvMetadata.ContainsKey(action.Into))
                    allEnvMetadata[action.Into] = new();
                foreach (var (eName, eMeta) in result.EnvMetadata)
                {
                    allEnvMetadata[action.Into][eName] = eMeta;
                    eMeta.SourceRelDir = rawRelDirs.GetValueOrDefault((action.From.Table, eName), "");
                }
            }
        }

        // Passthrough
        var allTableNames = rawTables.Keys.Select(k => k.tableName).Distinct();
        foreach (var tableName in allTableNames)
        {
            if (tablesHandledByActions.Contains(tableName)) continue;
            var envs = rawTables.Keys.Where(k => k.tableName == tableName).Select(k => k.envName).ToList();
            if (envs.Count == 1)
            {
                var key = (tableName, envs[0]);
                mergedTables[tableName] = rawTables[key];
                if (!allEnvMetadata.ContainsKey(tableName))
                    allEnvMetadata[tableName] = new();
                allEnvMetadata[tableName][envs[0]] = new EnvMergeMetadata
                {
                    ColumnOrder = rawTables[key].Columns.Select(c => c.Name).ToList(),
                    ColumnOverrides = new(),
                    ColumnRenames = new(),
                    SourceRelDir = rawRelDirs.GetValueOrDefault(key, "")
                };
            }
            else
            {
                var first = rawTables[(tableName, envs[0])];
                bool allIdentical = envs.Skip(1).All(env =>
                    TableComparer.FindDifference(first, rawTables[(tableName, env)]) == null);
                if (allIdentical)
                {
                    mergedTables[tableName] = first;
                    if (!allEnvMetadata.ContainsKey(tableName))
                        allEnvMetadata[tableName] = new();
                    foreach (var env in envs)
                    {
                        var key = (tableName, env);
                        allEnvMetadata[tableName][env] = new EnvMergeMetadata
                        {
                            ColumnOrder = first.Columns.Select(c => c.Name).ToList(),
                            ColumnOverrides = new(),
                            ColumnRenames = new(),
                            SourceRelDir = rawRelDirs.GetValueOrDefault(key, "")
                        };
                    }
                }
                else
                {
                    mergedTables[tableName] = first;
                }
            }
        }

        // Write tables
        manifest.Tables.Clear();
        foreach (var (tableName, tableFile) in mergedTables.OrderBy(kv => kv.Key))
        {
            var relativePath = $"data/{tableFile.Header.SourceFormat}/{tableFile.Header.TableName}.json";

            if (allEnvMetadata.TryGetValue(tableName, out var envMetas) && envMetas.Count > 0)
            {
                var metadata = tableFile.Header.Metadata ?? new Dictionary<string, object>();
                metadata[SourceOrigin.MetadataKey] = envMetas.Count > 1
                    ? EnvironmentInfo.MergedOrigin
                    : envMetas.Keys.First();

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

                var enrichedFile = new TableFile
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
                await projectService.WriteTableFileAsync(projectDir, relativePath, enrichedFile);
            }
            else
            {
                await projectService.WriteTableFileAsync(projectDir, relativePath, tableFile);
            }

            manifest.Tables[tableName] = relativePath;
        }

        await projectService.SaveProjectAsync(projectDir, manifest);

        // Build all envs
        foreach (var envName in envOrder)
        {
            var outputDir = Path.Combine(buildDir, envName);
            Directory.CreateDirectory(outputDir);

            foreach (var (name, entryPath) in manifest.Tables)
            {
                var tableFile = await projectService.ReadTableFileAsync(projectDir, entryPath);

                var origin = tableFile.Header.Metadata?.TryGetValue(SourceOrigin.MetadataKey, out var o) == true
                    ? o?.ToString() : null;

                EnvMergeMetadata? envMeta = null;
                if (tableFile.Header.Metadata != null
                    && tableFile.Header.Metadata.TryGetValue(envName, out var rawMeta)
                    && rawMeta is JsonElement je)
                {
                    envMeta = EnvMergeMetadata.FromJsonElement(je);
                }

                TableFile outputTable;
                if (origin == EnvironmentInfo.MergedOrigin)
                {
                    if (envMeta == null) continue;
                    outputTable = TableSplitter.Split(tableFile, envName, envMeta);
                }
                else if (origin != null && origin != EnvironmentInfo.MergedOrigin && origin != envName)
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

                var providerDict = sp.GetServices<IDataProvider>().ToDictionary(p => p.FormatId);
                if (!providerDict.TryGetValue(entry.Schema.SourceFormat, out var provider)) continue;

                var ext = provider.SupportedExtensions[0].TrimStart('.');
                var relDir = envMeta?.SourceRelDir;
                var dir = string.IsNullOrEmpty(relDir) ? outputDir : Path.Combine(outputDir, relDir);
                Directory.CreateDirectory(dir);
                await provider.WriteAsync(Path.Combine(dir, $"{name}.{ext}"), [entry]);
            }
        }

        return (manifest, buildDir, sp);
    }

    // ==================== Helpers ====================

    private readonly Dictionary<ServiceProvider, string> _projectDirs = new();

    private string GetProjectDir(ServiceProvider sp) => _projectDirs[sp];

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddMimirCore();
        services.AddMimirShn();
        services.AddMimirShineTable();
        return services.BuildServiceProvider();
    }

    private static async Task<Dictionary<string, (TableFile file, string relDir)>> ReadAllTables(
        DirectoryInfo sourceDir, List<IDataProvider> providers, ILogger logger)
    {
        var tables = new Dictionary<string, (TableFile file, string relDir)>();
        foreach (var file in sourceDir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var provider = providers.FirstOrDefault(p =>
                p.SupportedExtensions.Contains(file.Extension.ToLowerInvariant()));
            if (provider is null) continue;

            try
            {
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
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to read {File}", file.Name);
            }
        }
        return tables;
    }

    private static EnvMergeMetadata? GetEnvMetadata(TableFile tableFile, string envName)
    {
        if (tableFile.Header.Metadata == null) return null;
        if (!tableFile.Header.Metadata.TryGetValue(envName, out var raw)) return null;
        if (raw is not JsonElement je) return null;
        return EnvMergeMetadata.FromJsonElement(je);
    }
}
