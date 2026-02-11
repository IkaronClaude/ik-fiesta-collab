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
/// Full pipeline integration tests using synthetic SHN data.
/// Creates temp env directories with SHN files, runs init-template → import → build,
/// then verifies data equality and SHN bit-equality on re-import.
/// </summary>
public class SyntheticRoundtripTests : IAsyncLifetime
{
    private string _rootDir = null!;
    private string _envADir = null!;
    private string _envBDir = null!;
    private string _projectDir = null!;
    private string _buildDir = null!;
    private ServiceProvider _sp = null!;

    // Pipeline results populated during InitializeAsync
    private ProjectTemplate _template = null!;
    private MimirProject _manifest = null!;

    // Original file bytes for bit-equality checks (SHN + TXT)
    private readonly Dictionary<string, byte[]> _originalFileBytes = new();

    public async Task InitializeAsync()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), $"mimir-test-{Guid.NewGuid():N}");
        _envADir = Path.Combine(_rootDir, "env-a");
        _envBDir = Path.Combine(_rootDir, "env-b");
        _projectDir = Path.Combine(_rootDir, "project");
        _buildDir = Path.Combine(_rootDir, "build");

        Directory.CreateDirectory(Path.Combine(_envADir, "Shine"));
        Directory.CreateDirectory(_envBDir);
        Directory.CreateDirectory(_projectDir);

        _sp = BuildServices();

        var shnProvider = _sp.GetServices<IDataProvider>().First(p => p.FormatId == "shn");

        // --- Create synthetic SHN files ---

        // SharedTable: in both envs, same schema, overlapping + unique rows
        var sharedCols = new ColumnDefinition[]
        {
            Col("ID", ColumnType.UInt32, 4, 3),
            Col("Name", ColumnType.String, 32, 9),
            Col("Value", ColumnType.UInt16, 2, 2)
        };

        var sharedRowsA = new Dictionary<string, object?>[]
        {
            Row(("ID", (uint)1), ("Name", "Alpha"), ("Value", (ushort)100)),
            Row(("ID", (uint)2), ("Name", "Beta"), ("Value", (ushort)200)),
            Row(("ID", (uint)10), ("Name", "EnvAOnly"), ("Value", (ushort)999))
        };

        var sharedRowsB = new Dictionary<string, object?>[]
        {
            Row(("ID", (uint)1), ("Name", "Alpha"), ("Value", (ushort)100)),
            Row(("ID", (uint)2), ("Name", "Beta"), ("Value", (ushort)200)),
            Row(("ID", (uint)20), ("Name", "EnvBOnly"), ("Value", (ushort)888))
        };

        await WriteShn(shnProvider, Path.Combine(_envADir, "Shine", "SharedTable.shn"),
            "SharedTable", sharedCols, sharedRowsA);
        await WriteShn(shnProvider, Path.Combine(_envBDir, "SharedTable.shn"),
            "SharedTable", sharedCols, sharedRowsB);

        // EnvAOnly: only in env-a/Shine/
        var envAOnlyCols = new ColumnDefinition[]
        {
            Col("ID", ColumnType.UInt32, 4, 3),
            Col("Desc", ColumnType.String, 48, 9)
        };
        var envAOnlyRows = new Dictionary<string, object?>[]
        {
            Row(("ID", (uint)1), ("Desc", "First")),
            Row(("ID", (uint)2), ("Desc", "Second"))
        };
        await WriteShn(shnProvider, Path.Combine(_envADir, "Shine", "EnvAOnly.shn"),
            "EnvAOnly", envAOnlyCols, envAOnlyRows);

        // EnvBOnly: only in env-b/
        var envBOnlyCols = new ColumnDefinition[]
        {
            Col("ID", ColumnType.UInt32, 4, 3),
            Col("Tag", ColumnType.String, 16, 9)
        };
        var envBOnlyRows = new Dictionary<string, object?>[]
        {
            Row(("ID", (uint)1), ("Tag", "X")),
            Row(("ID", (uint)2), ("Tag", "Y"))
        };
        await WriteShn(shnProvider, Path.Combine(_envBDir, "EnvBOnly.shn"),
            "EnvBOnly", envBOnlyCols, envBOnlyRows);

        // ConflictTable: same schema, matched row with different Value
        var conflictCols = new ColumnDefinition[]
        {
            Col("ID", ColumnType.UInt32, 4, 3),
            Col("Score", ColumnType.UInt16, 2, 2)
        };
        var conflictRowsA = new Dictionary<string, object?>[]
        {
            Row(("ID", (uint)1), ("Score", (ushort)50))
        };
        var conflictRowsB = new Dictionary<string, object?>[]
        {
            Row(("ID", (uint)1), ("Score", (ushort)99))
        };
        await WriteShn(shnProvider, Path.Combine(_envADir, "Shine", "ConflictTable.shn"),
            "ConflictTable", conflictCols, conflictRowsA);
        await WriteShn(shnProvider, Path.Combine(_envBDir, "ConflictTable.shn"),
            "ConflictTable", conflictCols, conflictRowsB);

        // --- Create synthetic text table files ---

        var shineTableProvider = _sp.GetServices<IDataProvider>().First(p => p.FormatId == "shinetable");
        var configTableProvider = _sp.GetServices<IDataProvider>().First(p => p.FormatId == "configtable");

        // MobData.txt: #table format, env-a only
        await WriteTxt(shineTableProvider, Path.Combine(_envADir, "Shine", "MobData.txt"),
            "shinetable", "table",
            new Dictionary<string, object> { ["sourceFile"] = "MobData.txt", ["tableName"] = "Spawn", ["format"] = "table" },
            new ColumnDefinition[]
            {
                new() { Name = "ID", Type = ColumnType.UInt32, Length = 4 },
                new() { Name = "Name", Type = ColumnType.String, Length = 32 },
                new() { Name = "Level", Type = ColumnType.UInt16, Length = 2 }
            },
            [
                Row(("ID", (uint)1), ("Name", "Fighter"), ("Level", (ushort)10)),
                Row(("ID", (uint)2), ("Name", "Mage"), ("Level", (ushort)20))
            ]);

        // ServerConfig.txt: #DEFINE format, env-b only
        await WriteTxt(configTableProvider, Path.Combine(_envBDir, "ServerConfig.txt"),
            "configtable", "define",
            new Dictionary<string, object> { ["sourceFile"] = "ServerConfig.txt", ["typeName"] = "CONFIG", ["format"] = "define" },
            new ColumnDefinition[]
            {
                new() { Name = "Name", Type = ColumnType.String, Length = 256 },
                new() { Name = "Value", Type = ColumnType.Int32, Length = 4 },
                new() { Name = "Rate", Type = ColumnType.Float, Length = 4 }
            },
            [
                Row(("Name", "MaxPlayers"), ("Value", 100), ("Rate", 1.5f)),
                Row(("Name", "SpawnRate"), ("Value", 50), ("Rate", 0.8f))
            ]);

        // SharedText.txt: #table format, in both envs (identical)
        var sharedTextCols = new ColumnDefinition[]
        {
            new() { Name = "ID", Type = ColumnType.UInt32, Length = 4 },
            new() { Name = "Tag", Type = ColumnType.String, Length = 32 }
        };
        var sharedTextRows = new Dictionary<string, object?>[]
        {
            Row(("ID", (uint)1), ("Tag", "TagA")),
            Row(("ID", (uint)2), ("Tag", "TagB"))
        };
        var sharedTextMeta = new Dictionary<string, object>
        {
            ["sourceFile"] = "SharedText.txt", ["tableName"] = "Data", ["format"] = "table"
        };

        await WriteTxt(shineTableProvider, Path.Combine(_envADir, "Shine", "SharedText.txt"),
            "shinetable", "table", sharedTextMeta, sharedTextCols, sharedTextRows);
        await WriteTxt(shineTableProvider, Path.Combine(_envBDir, "SharedText.txt"),
            "shinetable", "table", sharedTextMeta, sharedTextCols, sharedTextRows);

        // Snapshot original file bytes for bit-equality checks
        foreach (var ext in new[] { "*.shn", "*.txt" })
        {
            foreach (var file in Directory.EnumerateFiles(_envADir, ext, SearchOption.AllDirectories))
                _originalFileBytes[$"env-a/{Path.GetRelativePath(_envADir, file).Replace('\\', '/')}"] = await File.ReadAllBytesAsync(file);
            foreach (var file in Directory.EnumerateFiles(_envBDir, ext, SearchOption.AllDirectories))
                _originalFileBytes[$"env-b/{Path.GetRelativePath(_envBDir, file).Replace('\\', '/')}"] = await File.ReadAllBytesAsync(file);
        }

        // --- Write mimir.json ---
        var projectService = _sp.GetRequiredService<IProjectService>();
        var project = new MimirProject
        {
            Environments = new Dictionary<string, EnvironmentConfig>
            {
                ["env-a"] = new() { ImportPath = _envADir },
                ["env-b"] = new() { ImportPath = _envBDir }
            }
        };
        await projectService.SaveProjectAsync(_projectDir, project);

        // --- Run pipeline: same code paths as CLI ---
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
    public void InitTemplate_GeneratesCopyActions_ForAllTables()
    {
        var copyActions = _template.Actions.Where(a => a.Action == "copy").ToList();
        var copyTargets = copyActions.Select(a => a.To).ToHashSet();

        copyTargets.ShouldContain("SharedTable");
        copyTargets.ShouldContain("EnvAOnly");
        copyTargets.ShouldContain("EnvBOnly");
        copyTargets.ShouldContain("ConflictTable");
        copyTargets.ShouldContain("MobData_Spawn");
        copyTargets.ShouldContain("ServerConfig_CONFIG");
        copyTargets.ShouldContain("SharedText_Data");
    }

    [Fact]
    public void InitTemplate_GeneratesMergeActions_ForSharedTables()
    {
        var mergeActions = _template.Actions.Where(a => a.Action == "merge").ToList();
        var mergeTargets = mergeActions.Select(a => a.Into).ToHashSet();

        // SharedTable, ConflictTable, and SharedText_Data exist in both envs → should have merge actions
        mergeTargets.ShouldContain("SharedTable");
        mergeTargets.ShouldContain("ConflictTable");
        mergeTargets.ShouldContain("SharedText_Data");

        // Env-only tables should NOT have merge actions
        mergeTargets.ShouldNotContain("EnvAOnly");
        mergeTargets.ShouldNotContain("EnvBOnly");
        mergeTargets.ShouldNotContain("MobData_Spawn");
        mergeTargets.ShouldNotContain("ServerConfig_CONFIG");
    }

    // ==================== Import Tests ====================

    [Fact]
    public void Import_AllTablesInManifest()
    {
        _manifest.Tables.Count.ShouldBe(7);
        _manifest.Tables.Keys.ShouldContain("SharedTable");
        _manifest.Tables.Keys.ShouldContain("EnvAOnly");
        _manifest.Tables.Keys.ShouldContain("EnvBOnly");
        _manifest.Tables.Keys.ShouldContain("ConflictTable");
        _manifest.Tables.Keys.ShouldContain("MobData_Spawn");
        _manifest.Tables.Keys.ShouldContain("ServerConfig_CONFIG");
        _manifest.Tables.Keys.ShouldContain("SharedText_Data");
    }

    [Fact]
    public async Task Import_MergedTable_HasMergedSourceOrigin()
    {
        var projectService = _sp.GetRequiredService<IProjectService>();
        var tableFile = await projectService.ReadTableFileAsync(_projectDir, _manifest.Tables["SharedTable"]);

        var origin = tableFile.Header.Metadata?[SourceOrigin.MetadataKey]?.ToString();
        origin.ShouldBe(EnvironmentInfo.MergedOrigin);
    }

    [Fact]
    public async Task Import_SingleEnvTable_HasEnvSourceOrigin()
    {
        var projectService = _sp.GetRequiredService<IProjectService>();
        var tableFile = await projectService.ReadTableFileAsync(_projectDir, _manifest.Tables["EnvAOnly"]);

        var origin = tableFile.Header.Metadata?[SourceOrigin.MetadataKey]?.ToString();
        origin.ShouldBe("env-a");
    }

    [Fact]
    public async Task Import_MergedTable_SourceRelDirPreserved()
    {
        var projectService = _sp.GetRequiredService<IProjectService>();
        var tableFile = await projectService.ReadTableFileAsync(_projectDir, _manifest.Tables["SharedTable"]);

        // env-a was in Shine/ subdir, env-b was at root
        var envAMeta = GetEnvMetadata(tableFile, "env-a");
        envAMeta.ShouldNotBeNull();
        envAMeta!.SourceRelDir.ShouldBe("Shine");

        var envBMeta = GetEnvMetadata(tableFile, "env-b");
        envBMeta.ShouldNotBeNull();
        envBMeta!.SourceRelDir.ShouldBe("");
    }

    [Fact]
    public async Task Import_MergedTable_ContainsAllRows()
    {
        var projectService = _sp.GetRequiredService<IProjectService>();
        var tableFile = await projectService.ReadTableFileAsync(_projectDir, _manifest.Tables["SharedTable"]);

        // 2 shared + 1 env-a-only + 1 env-b-only = 4 rows
        tableFile.Data.Count.ShouldBe(4);
    }

    // ==================== Build Tests ====================

    [Fact]
    public void Build_EnvA_OutputsToCorrectSubdir()
    {
        // env-a tables were in Shine/ subdir → build should preserve that
        var shinePath = Path.Combine(_buildDir, "env-a", "Shine");
        Directory.Exists(shinePath).ShouldBeTrue($"Expected {shinePath} to exist");

        File.Exists(Path.Combine(shinePath, "SharedTable.shn")).ShouldBeTrue();
        File.Exists(Path.Combine(shinePath, "EnvAOnly.shn")).ShouldBeTrue();
        File.Exists(Path.Combine(shinePath, "ConflictTable.shn")).ShouldBeTrue();
    }

    [Fact]
    public void Build_EnvB_SkipsEnvAOnlyTable()
    {
        var envBDir = Path.Combine(_buildDir, "env-b");

        // EnvAOnly should NOT be in env-b build
        var allFiles = Directory.EnumerateFiles(envBDir, "*.shn", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet();

        allFiles.ShouldNotContain("EnvAOnly");

        // EnvBOnly should be there
        allFiles.ShouldContain("EnvBOnly");

        // SharedTable + ConflictTable should also be there (merged)
        allFiles.ShouldContain("SharedTable");
        allFiles.ShouldContain("ConflictTable");
    }

    [Fact]
    public async Task Build_RoundtripPreservesData()
    {
        var shnProvider = _sp.GetServices<IDataProvider>().First(p => p.FormatId == "shn");

        // Read back SharedTable from env-a build
        var builtPath = Path.Combine(_buildDir, "env-a", "Shine", "SharedTable.shn");
        var entries = await shnProvider.ReadAsync(builtPath);

        entries.Count.ShouldBe(1);
        var table = entries[0];

        // Should have env-a's rows: IDs 1, 2, 10 (shared + env-a-only)
        table.Rows.Count.ShouldBe(3);
        var ids = table.Rows.Select(r => Convert.ToUInt32(r["ID"])).OrderBy(x => x).ToList();
        ids.ShouldBe(new uint[] { 1, 2, 10 });

        // Verify specific values
        var row1 = table.Rows.First(r => Convert.ToUInt32(r["ID"]) == 1);
        row1["Name"]!.ToString().ShouldBe("Alpha");
        Convert.ToUInt16(row1["Value"]).ShouldBe((ushort)100);
    }

    [Fact]
    public async Task Build_EnvA_ShnBitIdentical()
    {
        // For each original env-a SHN file, the built version should be bit-identical
        foreach (var (relKey, originalBytes) in _originalFileBytes
            .Where(kv => kv.Key.StartsWith("env-a/") && kv.Key.EndsWith(".shn")))
        {
            var relPath = relKey["env-a/".Length..]; // e.g. "Shine/SharedTable.shn"
            var tableName = Path.GetFileNameWithoutExtension(relPath);

            // Skip merged tables — their row set changes (gains env-b rows then splits back)
            // For non-merged tables, the bytes should be identical
            if (tableName is "SharedTable" or "ConflictTable") continue;

            var builtPath = Path.Combine(_buildDir, "env-a", relPath);
            File.Exists(builtPath).ShouldBeTrue($"Built file missing: {builtPath}");

            var builtBytes = await File.ReadAllBytesAsync(builtPath);
            builtBytes.ShouldBe(originalBytes,
                $"SHN bit-equality failed for env-a {relPath}");
        }
    }

    [Fact]
    public async Task Build_EnvB_ShnBitIdentical()
    {
        foreach (var (relKey, originalBytes) in _originalFileBytes
            .Where(kv => kv.Key.StartsWith("env-b/") && kv.Key.EndsWith(".shn")))
        {
            var relPath = relKey["env-b/".Length..];
            var tableName = Path.GetFileNameWithoutExtension(relPath);

            if (tableName is "SharedTable" or "ConflictTable") continue;

            var builtPath = Path.Combine(_buildDir, "env-b", relPath);
            File.Exists(builtPath).ShouldBeTrue($"Built file missing: {builtPath}");

            var builtBytes = await File.ReadAllBytesAsync(builtPath);
            builtBytes.ShouldBe(originalBytes,
                $"SHN bit-equality failed for env-b {relPath}");
        }
    }

    [Fact]
    public async Task Build_ReimportProducesEqualData()
    {
        // Full equality check: re-import the built files and compare to originals
        var shnProvider = _sp.GetServices<IDataProvider>().First(p => p.FormatId == "shn");

        foreach (var envName in new[] { "env-a", "env-b" })
        {
            var envBuildDir = Path.Combine(_buildDir, envName);
            var builtFiles = Directory.EnumerateFiles(envBuildDir, "*.shn", SearchOption.AllDirectories);

            foreach (var builtFile in builtFiles)
            {
                var tableName = Path.GetFileNameWithoutExtension(builtFile);
                var builtEntries = await shnProvider.ReadAsync(builtFile);
                builtEntries.Count.ShouldBe(1, $"Expected 1 table in {builtFile}");

                // Find the corresponding original file
                var originalDir = envName == "env-a" ? _envADir : _envBDir;
                var originalFile = Directory.EnumerateFiles(originalDir, $"{tableName}.shn", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (originalFile == null)
                    continue; // Table came from the other env via merge, no original to compare

                var originalEntries = await shnProvider.ReadAsync(originalFile);
                var original = originalEntries[0];
                var built = builtEntries[0];

                // Compare schema
                built.Schema.Columns.Count.ShouldBe(original.Schema.Columns.Count,
                    $"Column count mismatch for {tableName} in {envName}");
                for (int i = 0; i < original.Schema.Columns.Count; i++)
                {
                    built.Schema.Columns[i].Name.ShouldBe(original.Schema.Columns[i].Name,
                        $"Column {i} name mismatch for {tableName}");
                    built.Schema.Columns[i].Type.ShouldBe(original.Schema.Columns[i].Type,
                        $"Column {i} type mismatch for {tableName}");
                }

                // For merged tables, row count and values may change (conflicts keep target value,
                // row sets change). Only do exact comparison for non-merged tables.
                bool isMerged = tableName is "SharedTable" or "ConflictTable";

                if (!isMerged)
                {
                    built.Rows.Count.ShouldBe(original.Rows.Count,
                        $"Row count mismatch for {tableName} in {envName}");
                }

                // Compare row data for matching rows
                foreach (var origRow in original.Rows)
                {
                    var idVal = origRow.ContainsKey("ID") ? Convert.ToUInt32(origRow["ID"]) : (uint?)null;
                    if (idVal == null) continue;

                    var builtRow = built.Rows.FirstOrDefault(r =>
                        r.ContainsKey("ID") && Convert.ToUInt32(r["ID"]) == idVal);
                    builtRow.ShouldNotBeNull(
                        $"Row with ID={idVal} missing from built {tableName} in {envName}");

                    // Skip value comparison for merged tables — conflicts keep target value
                    if (isMerged) continue;

                    foreach (var col in original.Schema.Columns)
                    {
                        var origVal = origRow.GetValueOrDefault(col.Name)?.ToString();
                        var builtVal = builtRow![col.Name]?.ToString();
                        builtVal.ShouldBe(origVal,
                            $"Value mismatch for {tableName}.{col.Name} ID={idVal} in {envName}");
                    }
                }
            }
        }
    }

    // ==================== Text Table Tests ====================

    [Fact]
    public void Import_TextTables_InManifest()
    {
        _manifest.Tables.Keys.ShouldContain("MobData_Spawn");
        _manifest.Tables.Keys.ShouldContain("ServerConfig_CONFIG");
        _manifest.Tables.Keys.ShouldContain("SharedText_Data");
    }

    [Fact]
    public async Task Import_TextTable_SourceFormatPreserved()
    {
        var projectService = _sp.GetRequiredService<IProjectService>();

        var mobData = await projectService.ReadTableFileAsync(_projectDir, _manifest.Tables["MobData_Spawn"]);
        mobData.Header.SourceFormat.ShouldBe("shinetable");

        var serverConfig = await projectService.ReadTableFileAsync(_projectDir, _manifest.Tables["ServerConfig_CONFIG"]);
        serverConfig.Header.SourceFormat.ShouldBe("configtable");
    }

    [Fact]
    public void Build_TextTable_WrittenAsTxt()
    {
        // MobData_Spawn: env-a only, in Shine/ subdir
        var mobDataPath = Path.Combine(_buildDir, "env-a", "Shine", "MobData_Spawn.txt");
        File.Exists(mobDataPath).ShouldBeTrue($"Expected {mobDataPath} to exist");

        // ServerConfig_CONFIG: env-b only, at root
        var configPath = Path.Combine(_buildDir, "env-b", "ServerConfig_CONFIG.txt");
        File.Exists(configPath).ShouldBeTrue($"Expected {configPath} to exist");

        // SharedText_Data: merged, should be in both envs
        var sharedA = Path.Combine(_buildDir, "env-a", "Shine", "SharedText_Data.txt");
        File.Exists(sharedA).ShouldBeTrue($"Expected {sharedA} to exist");

        var sharedB = Path.Combine(_buildDir, "env-b", "SharedText_Data.txt");
        File.Exists(sharedB).ShouldBeTrue($"Expected {sharedB} to exist");

        // MobData should NOT be in env-b, ServerConfig should NOT be in env-a
        Directory.EnumerateFiles(Path.Combine(_buildDir, "env-b"), "MobData*", SearchOption.AllDirectories)
            .ShouldBeEmpty();
        Directory.EnumerateFiles(Path.Combine(_buildDir, "env-a"), "ServerConfig*", SearchOption.AllDirectories)
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Build_TextTable_ByteIdenticalRoundtrip()
    {
        // For non-merged, single-env text tables, built output should match original canonical bytes.
        // MobData_Spawn (env-a only) and ServerConfig_CONFIG (env-b only).

        // MobData_Spawn: original was env-a/Shine/MobData.txt, built is env-a/Shine/MobData_Spawn.txt
        var mobOriginal = _originalFileBytes["env-a/Shine/MobData.txt"];
        var mobBuilt = await File.ReadAllBytesAsync(
            Path.Combine(_buildDir, "env-a", "Shine", "MobData_Spawn.txt"));
        mobBuilt.ShouldBe(mobOriginal,
            "ShineTable byte-equality failed for MobData_Spawn roundtrip");

        // ServerConfig_CONFIG: original was env-b/ServerConfig.txt, built is env-b/ServerConfig_CONFIG.txt
        var configOriginal = _originalFileBytes["env-b/ServerConfig.txt"];
        var configBuilt = await File.ReadAllBytesAsync(
            Path.Combine(_buildDir, "env-b", "ServerConfig_CONFIG.txt"));
        configBuilt.ShouldBe(configOriginal,
            "ConfigTable byte-equality failed for ServerConfig_CONFIG roundtrip");
    }

    // ==================== Pipeline helpers (mirror CLI logic) ====================

    private async Task RunInitTemplate()
    {
        var projectService = _sp.GetRequiredService<IProjectService>();
        var providers = _sp.GetServices<IDataProvider>().ToList();
        var logger = _sp.GetRequiredService<ILogger<SyntheticRoundtripTests>>();

        var manifest = await projectService.LoadProjectAsync(_projectDir);

        var envTables = new Dictionary<(string table, string env), TableFile>();
        var envOrder = manifest.Environments!.Keys.ToList();

        foreach (var (envName, envConfig) in manifest.Environments!)
        {
            var sourceDir = new DirectoryInfo(envConfig.ImportPath);
            var tables = await ReadAllTables(sourceDir, providers, logger);
            foreach (var (tableName, (tableFile, _)) in tables)
                envTables[(tableName, envName)] = tableFile;
        }

        _template = TemplateGenerator.Generate(envTables, envOrder);
        await TemplateResolver.SaveAsync(_projectDir, _template);
    }

    private async Task RunImport()
    {
        var projectService = _sp.GetRequiredService<IProjectService>();
        var providers = _sp.GetServices<IDataProvider>().ToList();
        var logger = _sp.GetRequiredService<ILogger<SyntheticRoundtripTests>>();

        var manifest = await projectService.LoadProjectAsync(_projectDir);
        var template = await TemplateResolver.LoadAsync(_projectDir);

        // Phase 1: Read all tables
        var rawTables = new Dictionary<(string tableName, string envName), TableFile>();
        var rawRelDirs = new Dictionary<(string tableName, string envName), string>();

        foreach (var (envName, envConfig) in manifest.Environments!)
        {
            var sourceDir = new DirectoryInfo(envConfig.ImportPath);
            var tables = await ReadAllTables(sourceDir, providers, logger);
            foreach (var (tableName, (tableFile, relDir)) in tables)
            {
                rawTables[(tableName, envName)] = tableFile;
                rawRelDirs[(tableName, envName)] = relDir;
            }
        }

        // Phase 2: Execute merge actions
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
                    var relKey = (action.From.Table, eName);
                    eMeta.SourceRelDir = rawRelDirs.GetValueOrDefault(relKey, "");
                }
            }
        }

        // Phase 3: Passthrough tables
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
        }

        // Phase 4: Write tables and manifest
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
        var logger = _sp.GetRequiredService<ILogger<SyntheticRoundtripTests>>();

        var manifest = await projectService.LoadProjectAsync(_projectDir);

        foreach (var envName in new[] { "env-a", "env-b" })
        {
            var outputDir = Path.Combine(_buildDir, envName);
            Directory.CreateDirectory(outputDir);

            foreach (var (name, entryPath) in manifest.Tables)
            {
                var tableFile = await projectService.ReadTableFileAsync(_projectDir, entryPath);

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

                if (!providers.TryGetValue(entry.Schema.SourceFormat, out var provider)) continue;

                var ext = provider.SupportedExtensions[0].TrimStart('.');
                var relDir = envMeta?.SourceRelDir;
                var dir = string.IsNullOrEmpty(relDir) ? outputDir : Path.Combine(outputDir, relDir);
                Directory.CreateDirectory(dir);
                await provider.WriteAsync(Path.Combine(dir, $"{name}.{ext}"), [entry]);
            }
        }
    }

    // ==================== Helpers ====================

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddMimirCore();
        services.AddMimirShn();
        services.AddMimirTextTables();
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

    private static async Task WriteTxt(IDataProvider provider, string path, string sourceFormat,
        string format, Dictionary<string, object> metadata,
        IReadOnlyList<ColumnDefinition> columns, IReadOnlyList<Dictionary<string, object?>> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var entry = new TableEntry
        {
            Schema = new TableSchema
            {
                TableName = Path.GetFileNameWithoutExtension(path),
                SourceFormat = sourceFormat,
                Columns = columns,
                Metadata = metadata
            },
            Rows = rows
        };
        await provider.WriteAsync(path, [entry]);
    }

    private static uint CalculateRecordLength(IReadOnlyList<ColumnDefinition> columns)
    {
        uint length = 2; // row length prefix
        foreach (var col in columns)
            length += (uint)col.Length;
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

    private static EnvMergeMetadata? GetEnvMetadata(TableFile tableFile, string envName)
    {
        if (tableFile.Header.Metadata == null) return null;
        if (!tableFile.Header.Metadata.TryGetValue(envName, out var raw)) return null;
        if (raw is not JsonElement je) return null;
        return EnvMergeMetadata.FromJsonElement(je);
    }
}
