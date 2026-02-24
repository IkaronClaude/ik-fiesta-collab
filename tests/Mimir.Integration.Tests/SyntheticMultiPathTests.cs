using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mimir.Core;
using Mimir.Core.Models;
using Mimir.Core.Project;
using Mimir.Core.Providers;
using Mimir.Core.Templates;
using Mimir.Shn;
using Shouldly;
using Xunit;

namespace Mimir.Integration.Tests;

/// <summary>
/// Integration tests for the same-filename-at-multiple-paths scenario.
/// Verifies that when a SHN file exists at both Shine/ActionViewInfo.shn and
/// Shine/View/ActionViewInfo.shn, both copies are preserved with correct internal names
/// and both build to the correct paths with correct filenames.
/// </summary>
public class SyntheticMultiPathTests : IAsyncLifetime
{
    private string _rootDir = null!;
    private string _envDir = null!;
    private string _projectDir = null!;
    private string _buildDir = null!;
    private ServiceProvider _sp = null!;
    private MimirProject _manifest = null!;
    private byte[] _primaryFileBytes = null!;
    private byte[] _deeperFileBytes = null!;

    public async Task InitializeAsync()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), $"mimir-multipath-{Guid.NewGuid():N}");
        _envDir = Path.Combine(_rootDir, "env");
        _projectDir = Path.Combine(_rootDir, "project");
        _buildDir = Path.Combine(_rootDir, "build");

        Directory.CreateDirectory(Path.Combine(_envDir, "Shine", "View"));
        Directory.CreateDirectory(_projectDir);

        _sp = BuildServices();

        var shnProvider = _sp.GetServices<IDataProvider>().First(p => p.FormatId == "shn");

        // ActionViewInfo at two paths (same table name, like the real game where
        // 9Data/Shine/ActionViewInfo.shn and 9Data/Shine/View/ActionViewInfo.shn both exist)
        var cols = new ColumnDefinition[]
        {
            Col("ID", ColumnType.UInt32, 4, 3),
            Col("InxName", ColumnType.String, 32, 9),
        };
        var rows = new Dictionary<string, object?>[]
        {
            Row(("ID", (uint)1), ("InxName", "Idle")),
            Row(("ID", (uint)2), ("InxName", "Walk")),
        };

        await WriteShn(shnProvider, Path.Combine(_envDir, "Shine", "ActionViewInfo.shn"),
            "ActionViewInfo", cols, rows);
        await WriteShn(shnProvider, Path.Combine(_envDir, "Shine", "View", "ActionViewInfo.shn"),
            "ActionViewInfo", cols, rows);

        // Also add a normal table with no path conflict (control)
        var normalCols = new ColumnDefinition[]
        {
            Col("ID", ColumnType.UInt32, 4, 3),
            Col("Name", ColumnType.String, 32, 9),
        };
        var normalRows = new Dictionary<string, object?>[]
        {
            Row(("ID", (uint)1), ("Name", "Fighter")),
        };
        await WriteShn(shnProvider, Path.Combine(_envDir, "Shine", "MonsterInfo.shn"),
            "MonsterInfo", normalCols, normalRows);

        _primaryFileBytes = await File.ReadAllBytesAsync(
            Path.Combine(_envDir, "Shine", "ActionViewInfo.shn"));
        _deeperFileBytes = await File.ReadAllBytesAsync(
            Path.Combine(_envDir, "Shine", "View", "ActionViewInfo.shn"));

        // Set up project
        var projectService = _sp.GetRequiredService<IProjectService>();
        var project = new MimirProject();
        await projectService.SaveProjectAsync(_projectDir, project);
        EnvironmentStore.Save(_projectDir, "server", new EnvironmentConfig { ImportPath = _envDir });

        // Run full pipeline
        await RunInitTemplate();
        await RunImport();
        await RunBuild();
    }

    public Task DisposeAsync()
    {
        _sp.Dispose();
        try { Directory.Delete(_rootDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    // ==================== Init-Template Tests ====================

    [Fact]
    public async Task InitTemplate_DeeperDuplicate_HasOutputName()
    {
        var template = await TemplateResolver.LoadAsync(_projectDir);

        // The deeper copy should have OutputName set to the source table name
        var deeperCopy = template.Actions.FirstOrDefault(a =>
            a.Action == "copy" && a.To == "View.ActionViewInfo");
        deeperCopy.ShouldNotBeNull("Expected copy action for View.ActionViewInfo");
        deeperCopy!.OutputName.ShouldBe("ActionViewInfo",
            "Deeper copy must have OutputName so build writes ActionViewInfo.shn not View.ActionViewInfo.shn");
    }

    [Fact]
    public async Task InitTemplate_PrimaryEntry_NoOutputName()
    {
        var template = await TemplateResolver.LoadAsync(_projectDir);

        var primaryCopy = template.Actions.FirstOrDefault(a =>
            a.Action == "copy" && a.To == "ActionViewInfo");
        primaryCopy.ShouldNotBeNull("Expected copy action for ActionViewInfo");
        primaryCopy!.OutputName.ShouldBeNull(
            "Primary (shallow) copy should not need OutputName override");
    }

    // ==================== Import Tests ====================

    [Fact]
    public void Import_BothPathsInManifest()
    {
        // Shallow (primary) keeps original name; deeper gets "View." prefix
        _manifest.Tables.Keys.ShouldContain("ActionViewInfo",
            "Primary (Shine/) table must be in manifest");
        _manifest.Tables.Keys.ShouldContain("View.ActionViewInfo",
            "Deeper (Shine/View/) table must be in manifest as View.ActionViewInfo");
    }

    [Fact]
    public void Import_NormalTable_Unaffected()
    {
        _manifest.Tables.Keys.ShouldContain("MonsterInfo");
    }

    // ==================== Build Tests ====================

    [Fact]
    public void Build_PrimaryFile_AtCorrectPath()
    {
        // Shallow table: Shine/ActionViewInfo.shn
        var path = Path.Combine(_buildDir, "server", "Shine", "ActionViewInfo.shn");
        File.Exists(path).ShouldBeTrue($"Expected primary file at {path}");
    }

    [Fact]
    public void Build_DeeperFile_AtCorrectPath()
    {
        // Deeper table: Shine/View/ActionViewInfo.shn (NOT Shine/View/View.ActionViewInfo.shn)
        var path = Path.Combine(_buildDir, "server", "Shine", "View", "ActionViewInfo.shn");
        File.Exists(path).ShouldBeTrue($"Expected deeper file at {path}");
    }

    [Fact]
    public void Build_DeeperFile_NoWrongName()
    {
        // Must NOT produce "View.ActionViewInfo.shn" — that would be wrong
        var wrongPath = Path.Combine(_buildDir, "server", "Shine", "View", "View.ActionViewInfo.shn");
        File.Exists(wrongPath).ShouldBeFalse(
            "Build must not produce View.ActionViewInfo.shn — it should use outputName to write ActionViewInfo.shn");
    }

    [Fact]
    public async Task Build_PrimaryFile_BitIdentical()
    {
        var builtPath = Path.Combine(_buildDir, "server", "Shine", "ActionViewInfo.shn");
        var builtBytes = await File.ReadAllBytesAsync(builtPath);
        builtBytes.ShouldBe(_primaryFileBytes,
            "Primary ActionViewInfo.shn must roundtrip bit-identically");
    }

    [Fact]
    public async Task Build_DeeperFile_BitIdentical()
    {
        var builtPath = Path.Combine(_buildDir, "server", "Shine", "View", "ActionViewInfo.shn");
        var builtBytes = await File.ReadAllBytesAsync(builtPath);
        builtBytes.ShouldBe(_deeperFileBytes,
            "Deeper View/ActionViewInfo.shn must roundtrip bit-identically");
    }

    // ==================== Pipeline helpers ====================

    private async Task RunInitTemplate()
    {
        var providers = _sp.GetServices<IDataProvider>().ToList();
        var logger = _sp.GetRequiredService<ILogger<SyntheticMultiPathTests>>();
        var allEnvs = EnvironmentStore.LoadAll(_projectDir);

        var envTables = new Dictionary<(string table, string env), TableFile>();
        var envOrder = allEnvs.Keys.ToList();

        foreach (var (envName, envConfig) in allEnvs)
        {
            if (envConfig.ImportPath == null) continue;
            var sourceDir = new DirectoryInfo(envConfig.ImportPath);
            var tables = await ReadAllTables(sourceDir, providers, logger);
            foreach (var (tableName, (tableFile, _)) in tables)
                envTables[(tableName, envName)] = tableFile;
        }

        var template = TemplateGenerator.Generate(envTables, envOrder);
        await TemplateResolver.SaveAsync(_projectDir, template);
    }

    private async Task RunImport()
    {
        var projectService = _sp.GetRequiredService<IProjectService>();
        var providers = _sp.GetServices<IDataProvider>().ToList();
        var logger = _sp.GetRequiredService<ILogger<SyntheticMultiPathTests>>();

        var manifest = await projectService.LoadProjectAsync(_projectDir);
        var template = await TemplateResolver.LoadAsync(_projectDir);
        var allEnvs = EnvironmentStore.LoadAll(_projectDir);

        // Phase 1: Read all tables
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

        // Phase 2: Execute template copy/merge actions
        var mergedTables = new Dictionary<string, TableFile>();
        var allEnvMetadata = new Dictionary<string, Dictionary<string, EnvMergeMetadata>>();
        var mergeActions = template.Actions.Where(a => a.Action is "copy" or "merge").ToList();
        var tablesHandledByActions = new HashSet<string>();

        foreach (var action in mergeActions)
        {
            if (action.Action == "copy" && action.From != null && action.To != null)
            {
                var key = (action.From.Table, action.From.Env);
                if (!rawTables.TryGetValue(key, out var raw)) continue;

                mergedTables[action.To] = raw;
                tablesHandledByActions.Add(action.To);

                if (!allEnvMetadata.ContainsKey(action.To))
                    allEnvMetadata[action.To] = new();
                allEnvMetadata[action.To][action.From.Env] = new EnvMergeMetadata
                {
                    ColumnOrder = raw.Columns.Select(c => c.Name).ToList(),
                    ColumnOverrides = new(),
                    ColumnRenames = new(),
                    SourceRelDir = rawRelDirs.GetValueOrDefault(key, ""),
                    OutputName = action.OutputName
                };
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

        // Phase 3: Write tables and manifest
        manifest.Tables.Clear();
        foreach (var (tableName, tableFile) in mergedTables.OrderBy(kv => kv.Key))
        {
            // Use internal tableName (not source TableName) so multi-path tables get distinct JSON files
            var relativePath = $"data/{tableFile.Header.SourceFormat}/{tableName}.json";

            if (allEnvMetadata.TryGetValue(tableName, out var envMetas) && envMetas.Count > 0)
            {
                var metadata = tableFile.Header.Metadata ?? new Dictionary<string, object>();
                metadata[SourceOrigin.MetadataKey] = envMetas.Keys.First();

                foreach (var (eName, eMeta) in envMetas)
                {
                    metadata[eName] = new Dictionary<string, object?>
                    {
                        ["columnOrder"] = eMeta.ColumnOrder,
                        ["columnOverrides"] = eMeta.ColumnOverrides.Count > 0 ? eMeta.ColumnOverrides : null,
                        ["columnRenames"] = eMeta.ColumnRenames.Count > 0 ? eMeta.ColumnRenames : null,
                        ["sourceRelDir"] = eMeta.SourceRelDir,
                        ["outputName"] = eMeta.OutputName   // critical for multi-path tables
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
                await projectService.WriteTableFileAsync(_projectDir, relativePath, enrichedFile);
            }
            else
            {
                await projectService.WriteTableFileAsync(_projectDir, relativePath, tableFile);
            }

            manifest.Tables[tableName] = relativePath;
        }

        await projectService.SaveProjectAsync(_projectDir, manifest);
        _manifest = manifest;
    }

    private async Task RunBuild()
    {
        var projectService = _sp.GetRequiredService<IProjectService>();
        var providers = _sp.GetServices<IDataProvider>().ToDictionary(p => p.FormatId);

        var manifest = await projectService.LoadProjectAsync(_projectDir);
        var outputDir = Path.Combine(_buildDir, "server");
        Directory.CreateDirectory(outputDir);

        foreach (var (name, entryPath) in manifest.Tables)
        {
            var tableFile = await projectService.ReadTableFileAsync(_projectDir, entryPath);

            EnvMergeMetadata? envMeta = null;
            if (tableFile.Header.Metadata != null
                && tableFile.Header.Metadata.TryGetValue("server", out var rawMeta)
                && rawMeta is JsonElement je)
            {
                envMeta = EnvMergeMetadata.FromJsonElement(je);
            }

            var entry = new TableEntry
            {
                Schema = new TableSchema
                {
                    TableName = tableFile.Header.TableName,
                    SourceFormat = tableFile.Header.SourceFormat,
                    Columns = tableFile.Columns,
                    Metadata = tableFile.Header.Metadata
                },
                Rows = tableFile.Data
            };

            if (!providers.TryGetValue(entry.Schema.SourceFormat, out var provider)) continue;

            var ext = provider.SupportedExtensions[0].TrimStart('.');
            var relDir = envMeta?.SourceRelDir;
            var dir = string.IsNullOrEmpty(relDir) ? outputDir : Path.Combine(outputDir, relDir);

            // Use outputName override when set (e.g. "View.ActionViewInfo" → "ActionViewInfo.shn")
            var outputFileName = envMeta?.OutputName ?? name;
            Directory.CreateDirectory(dir);
            await provider.WriteAsync(Path.Combine(dir, $"{outputFileName}.{ext}"), [entry]);
        }
    }

    // ==================== Helpers ====================

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddMimirCore();
        services.AddMimirShn();
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
        uint recordLength = 2; // row length prefix
        foreach (var col in columns) recordLength += (uint)col.Length;
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
                    ["defaultRecordLength"] = recordLength
                }
            },
            Rows = rows
        };
        await provider.WriteAsync(path, [entry]);
    }

    /// <summary>
    /// Two-pass ReadAllTables matching the CLI implementation.
    /// Same-name files at different depths get a prefix (e.g. "View.ActionViewInfo").
    /// </summary>
    private static async Task<Dictionary<string, (TableFile file, string relDir)>> ReadAllTables(
        DirectoryInfo sourceDir, List<IDataProvider> providers, ILogger logger)
    {
        // Pass 1: collect all entries with their source paths
        var allEntries = new List<(string sourceName, string relDir, TableFile tableFile)>();

        foreach (var file in sourceDir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var provider = providers.FirstOrDefault(p => p.CanHandle(file.FullName));
            if (provider is null) continue;

            var entries = await provider.ReadAsync(file.FullName);
            var relDir = Path.GetDirectoryName(
                Path.GetRelativePath(sourceDir.FullName, file.FullName))?.Replace('\\', '/') ?? "";

            foreach (var entry in entries)
            {
                allEntries.Add((entry.Schema.TableName, relDir, new TableFile
                {
                    Header = new TableHeader
                    {
                        TableName = entry.Schema.TableName,
                        SourceFormat = entry.Schema.SourceFormat,
                        Metadata = entry.Schema.Metadata
                    },
                    Columns = entry.Schema.Columns,
                    Data = entry.Rows
                }));
            }
        }

        // Pass 2: group by source name, shallower path keeps original name,
        // deeper copies get a prefix derived from extra path components.
        var result = new Dictionary<string, (TableFile file, string relDir)>();

        foreach (var group in allEntries.GroupBy(e => e.sourceName))
        {
            var ordered = group
                .OrderBy(e => e.relDir.Count(c => c == '/'))
                .ThenBy(e => e.relDir)
                .ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                var (sourceName, relDir, tableFile) = ordered[i];
                string internalName;

                if (i == 0)
                {
                    internalName = sourceName;
                }
                else
                {
                    var primaryParts = ordered[0].relDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var deeperParts = relDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var extraParts = deeperParts.Length > primaryParts.Length
                        ? deeperParts.Skip(primaryParts.Length).ToArray()
                        : new[] { deeperParts.LastOrDefault() ?? "dup" };
                    var prefix = string.Join(".", extraParts);
                    internalName = $"{prefix}.{sourceName}";
                }

                result[internalName] = (tableFile, relDir);
            }
        }

        return result;
    }
}
