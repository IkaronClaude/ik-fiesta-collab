using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mimir.Core;
using Mimir.Core.Constraints;
using Mimir.Core.Models;
using Mimir.Core.Project;
using Mimir.Core.Providers;
using Mimir.Core.Templates;
using Mimir.Shn;
using Mimir.ShineTable;
using Mimir.Sql;

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
services.AddMimirCore();
services.AddMimirShn();
services.AddMimirTextTables();
services.AddMimirSql();

var sp = services.BuildServiceProvider();

var rootCommand = new RootCommand("Mimir - Fiesta Online server data toolkit");

// Shared --project/-p option used by commands that also have other positional args
Option<DirectoryInfo?> MakeProjectOption()
{
    var opt = new Option<DirectoryInfo?>("--project",
        "Path to Mimir project directory (defaults to current directory)")
    {
        Arity = ArgumentArity.ZeroOrOne
    };
    opt.AddAlias("-p");
    return opt;
}


// --- init command ---
var initCommand = new Command("init", "Create a new Mimir project directory with a skeleton mimir.json");
var initProjectArg = new Argument<DirectoryInfo>("project", "Path to new Mimir project directory to create");
var initMimirOption = new Option<string>("--mimir",
    () => "mimir",
    "Command or path to the mimir executable baked into generated mimir.bat (default: mimir, assumes PATH)");
initCommand.AddArgument(initProjectArg);
initCommand.AddOption(initMimirOption);

initCommand.SetHandler((DirectoryInfo project, string mimirCmd) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var projectService = sp.GetRequiredService<IProjectService>();

    if (File.Exists(Path.Combine(project.FullName, "mimir.json")))
    {
        logger.LogError("mimir.json already exists in {Dir}. Delete it first or choose a different directory.", project.FullName);
        return Task.CompletedTask;
    }

    Directory.CreateDirectory(project.FullName);
    Directory.CreateDirectory(Path.Combine(project.FullName, "deploy"));
    Directory.CreateDirectory(Path.Combine(project.FullName, "deploy", "patcher"));
    EnvironmentStore.EnsureEnvDir(project.FullName);

    var manifest = new MimirProject();
    return projectService.SaveProjectAsync(project.FullName, manifest).ContinueWith(_ =>
    {
        void WriteIfMissing(string path, string content)
        {
            if (!File.Exists(path)) { File.WriteAllText(path, content); logger.LogInformation("Created {Path}", Path.GetRelativePath(project.FullName, path)); }
        }

        // .gitignore
        WriteIfMissing(Path.Combine(project.FullName, ".gitignore"),
            "# Mimir generated outputs\r\nbuild/\r\npatches/\r\n");

        // mimir.bat — single editable reference to the mimir executable
        WriteIfMissing(Path.Combine(project.FullName, "mimir.bat"),
            "@echo off\r\n" +
            ":: Mimir CLI resolver for this project.\r\n" +
            ":: Edit the line below if mimir is not in your PATH.\r\n" +
            $"{mimirCmd} %*\r\n");

        // deploy/reimport.bat
        WriteIfMissing(Path.Combine(project.FullName, "deploy", "reimport.bat"),
            "@echo off\r\n" +
            ":: Re-import all data and rebuild. Wipes data/ and build/ first.\r\n" +
            ":: NOTE: mimir.template.json is preserved — don't regenerate it unless you mean to.\r\n" +
            "cd /d \"%~dp0..\"\r\n" +
            "call mimir.bat reimport\r\n" +
            "pause\r\n");

        // deploy/deploy.bat — build + pack (no Docker assumptions)
        WriteIfMissing(Path.Combine(project.FullName, "deploy", "deploy.bat"),
            "@echo off\r\n" +
            ":: Build all environments and generate client patches.\r\n" +
            "cd /d \"%~dp0..\"\r\n" +
            "call mimir.bat build --all\r\n" +
            "if errorlevel 1 ( pause & exit /b 1 )\r\n" +
            "call mimir.bat pack patches --env client\r\n" +
            "if errorlevel 1 ( pause & exit /b 1 )\r\n" +
            "echo Done! Patches written to patches/\r\n" +
            "pause\r\n");

        // deploy/patcher/patcher.config
        WriteIfMissing(Path.Combine(project.FullName, "deploy", "patcher", "patcher.config"),
            "PatchUrl=http://localhost:8080/\r\n");

        // deploy/patcher/patch.bat
        WriteIfMissing(Path.Combine(project.FullName, "deploy", "patcher", "patch.bat"),
            "@echo off\r\n" +
            "REM Mimir Client Patcher\r\n" +
            "REM Usage: patch.bat [client-dir]\r\n" +
            "set CLIENT_DIR=%~1\r\n" +
            "if \"%CLIENT_DIR%\"==\"\" set CLIENT_DIR=%CD%\r\n" +
            "powershell -ExecutionPolicy Bypass -File \"%~dp0patch.ps1\" -ClientDir \"%CLIENT_DIR%\"\r\n" +
            "pause\r\n");

        // deploy/patcher/patch.ps1 — full patcher implementation
        WriteIfMissing(Path.Combine(project.FullName, "deploy", "patcher", "patch.ps1"),
            "param(\r\n" +
            "    [Parameter(Mandatory=$true)]\r\n" +
            "    [string]$ClientDir\r\n" +
            ")\r\n" +
            "$ErrorActionPreference = 'Stop'\r\n" +
            "# Trim stray quotes that batch quoting adds when a path ends with a backslash\r\n" +
            "$ClientDir = $ClientDir.Trim('\"')\r\n" +
            "$configPath = Join-Path $PSScriptRoot 'patcher.config'\r\n" +
            "if (-not (Test-Path $configPath)) { Write-Host 'ERROR: patcher.config not found' -ForegroundColor Red; exit 1 }\r\n" +
            "$patchUrl = $null\r\n" +
            "foreach ($line in Get-Content $configPath) { if ($line -match '^PatchUrl=(.+)$') { $patchUrl = $Matches[1].Trim() } }\r\n" +
            "if (-not $patchUrl) { Write-Host 'ERROR: PatchUrl not found in patcher.config' -ForegroundColor Red; exit 1 }\r\n" +
            "if (-not $patchUrl.EndsWith('/')) { $patchUrl += '/' }\r\n" +
            "Write-Host \"Mimir Client Patcher`n  Patch server: $patchUrl`n  Client dir:   $ClientDir`n\"\r\n" +
            "if (-not (Test-Path $ClientDir)) { New-Item -ItemType Directory -Path $ClientDir -Force | Out-Null }\r\n" +
            "$versionFile = Join-Path $ClientDir '.mimir-version'\r\n" +
            "$currentVersion = 0\r\n" +
            "if (Test-Path $versionFile) { $currentVersion = [int](Get-Content $versionFile -Raw).Trim() }\r\n" +
            "Write-Host \"Current version: $currentVersion\"\r\n" +
            "try { $indexJson = (Invoke-WebRequest -Uri \"${patchUrl}patch-index.json\" -UseBasicParsing).Content }\r\n" +
            "catch { Write-Host \"ERROR: Failed to download patch index: $_\" -ForegroundColor Red; exit 1 }\r\n" +
            "$index = $indexJson | ConvertFrom-Json\r\n" +
            "$latestVersion = $index.latestVersion\r\n" +
            "Write-Host \"Latest version:  $latestVersion\"\r\n" +
            "if ($currentVersion -ge $latestVersion) { Write-Host \"`nClient is up to date!\" -ForegroundColor Green; exit 0 }\r\n" +
            "$patches = $index.patches | Where-Object { $_.version -gt $currentVersion } | Sort-Object version\r\n" +
            "Write-Host \"`nApplying $($patches.Count) patch(es)...\"\r\n" +
            "foreach ($patch in $patches) {\r\n" +
            "    $url = $patch.url\r\n" +
            "    if (-not ($url -match '^https?://') -and -not ($url -match '^file:///')) { $url = \"${patchUrl}${url}\" }\r\n" +
            "    Write-Host \"Patch v$($patch.version): $($patch.fileCount) files, $([math]::Round($patch.sizeBytes/1024,1)) KB\"\r\n" +
            "    $tempZip = Join-Path $env:TEMP \"mimir-patch-v$($patch.version).zip\"\r\n" +
            "    try { Invoke-WebRequest -Uri $url -OutFile $tempZip -UseBasicParsing }\r\n" +
            "    catch { Write-Host \"  ERROR: Download failed: $_\" -ForegroundColor Red; exit 1 }\r\n" +
            "    $actualHash = (Get-FileHash -Path $tempZip -Algorithm SHA256).Hash.ToLower()\r\n" +
            "    if ($actualHash -ne $patch.sha256) { Write-Host '  ERROR: SHA-256 mismatch!' -ForegroundColor Red; Remove-Item $tempZip -Force; exit 1 }\r\n" +
            "    Expand-Archive -Path $tempZip -DestinationPath $ClientDir -Force\r\n" +
            "    Set-Content -Path $versionFile -Value $patch.version\r\n" +
            "    Remove-Item $tempZip -Force\r\n" +
            "    Write-Host \"  Applied v$($patch.version).\" -ForegroundColor Green\r\n" +
            "}\r\n" +
            "Write-Host \"`nPatching complete! Client is now at version $latestVersion.\" -ForegroundColor Green\r\n");

        logger.LogInformation("Created project at {Dir}", project.FullName);
        logger.LogInformation("Next steps:");
        logger.LogInformation("  cd {Dir}", project.FullName);
        logger.LogInformation("  mimir env server init Z:/Server");
        logger.LogInformation("  mimir env client init Z:/ClientSource/ressystem --patchable");
        logger.LogInformation("  mimir init-template --passthrough server");
        logger.LogInformation("  mimir import");
        logger.LogInformation("  mimir build --all");
    });

}, initProjectArg, initMimirOption);

// --- import command ---
var importCommand = new Command("import", "Import data files into a Mimir project using environments from mimir.json");
var importProjectOpt = MakeProjectOption();
var importReimportOption = new Option<bool>("--reimport", "Wipe data/ and build/ directories before importing for a clean re-import");
importCommand.AddOption(importProjectOpt);
importCommand.AddOption(importReimportOption);

importCommand.SetHandler(async (DirectoryInfo? projectOpt, bool reimport) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var project = ResolveProjectOrExit(projectOpt, logger);
    var projectService = sp.GetRequiredService<IProjectService>();
    var providers = sp.GetServices<IDataProvider>().ToList();

    var manifest = await projectService.LoadProjectAsync(project.FullName);

    if (reimport)
    {
        var dataDir = Path.Combine(project.FullName, "data");
        var buildDir = Path.Combine(project.FullName, "build");
        if (Directory.Exists(dataDir))
        {
            logger.LogInformation("--reimport: deleting {Dir}", dataDir);
            Directory.Delete(dataDir, recursive: true);
        }
        if (Directory.Exists(buildDir))
        {
            logger.LogInformation("--reimport: deleting {Dir}", buildDir);
            Directory.Delete(buildDir, recursive: true);
        }
        manifest.Tables.Clear();
        logger.LogInformation("--reimport: cleared tables index — starting fresh import");
    }

    var allEnvs = EnvironmentStore.LoadAll(project.FullName);
    if (allEnvs.Count == 0)
    {
        logger.LogError("No environments configured. Use: mimir env <name> init <importPath>");
        return;
    }

    // Load template for merge actions
    var template = await TemplateResolver.LoadAsync(project.FullName);

    // Phase 1: Read all tables from each environment into memory
    var rawTables = new Dictionary<(string tableName, string envName), TableFile>();
    var rawRelDirs = new Dictionary<(string tableName, string envName), string>(); // track source-relative dirs

    foreach (var (envName, envConfig) in allEnvs)
    {
        if (envConfig.ImportPath == null)
        {
            logger.LogWarning("Skipping environment {Env}: import-path not set. Use: mimir env {Env} set import-path <path>", envName, envName);
            continue;
        }
        var sourceDir = new DirectoryInfo(envConfig.ImportPath);
        if (!sourceDir.Exists)
        {
            logger.LogWarning("Import path does not exist for environment {Env}: {Path}", envName, envConfig.ImportPath);
            continue;
        }

        logger.LogInformation("Scanning environment {Env}: {Path}", envName, sourceDir.FullName);
        var tables = await ReadAllTables(sourceDir, providers, logger);

        foreach (var (tableName, (tableFile, relDir)) in tables)
        {
            rawTables[(tableName, envName)] = tableFile;
            rawRelDirs[(tableName, envName)] = relDir;
            logger.LogDebug("Read {Table} from {Env} ({Rows} rows, relDir={Dir})", tableName, envName, tableFile.Data.Count, relDir);
        }

        logger.LogInformation("Read {Count} tables from {Env}", tables.Count, envName);
    }

    // Phase 2: Execute merge actions from template
    var mergedTables = new Dictionary<string, TableFile>();
    var allEnvMetadata = new Dictionary<string, Dictionary<string, EnvMergeMetadata>>(); // tableName → envName → metadata
    int totalConflicts = 0;

    var mergeActions = template.Actions.Where(a => a.Action is "copy" or "merge").ToList();
    var tablesHandledByActions = new HashSet<string>();

    foreach (var action in mergeActions)
    {
        if (action.Action == "copy" && action.From != null && action.To != null)
        {
            var key = (action.From.Table, action.From.Env);
            if (rawTables.TryGetValue(key, out var raw))
            {
                // Tag every row with the source env so that after a subsequent merge,
                // target-only rows are correctly excluded from other envs at build time.
                var taggedRowEnvs = Enumerable.Repeat<List<string>?>([action.From.Env], raw.Data.Count).ToList();
                mergedTables[action.To] = new TableFile
                {
                    Header = raw.Header,
                    Columns = raw.Columns,
                    Data = raw.Data,
                    RowEnvironments = taggedRowEnvs
                };
                tablesHandledByActions.Add(action.To);

                // Store base env column order and source path
                if (!allEnvMetadata.ContainsKey(action.To))
                    allEnvMetadata[action.To] = new();
                allEnvMetadata[action.To][action.From.Env] = new EnvMergeMetadata
                {
                    ColumnOrder = raw.Columns.Select(c => c.Name).ToList(),
                    ColumnOverrides = new(),
                    ColumnRenames = new(),
                    SourceRelDir = rawRelDirs.GetValueOrDefault(key, ""),
                    FormatMetadata = ExtractFormatMetadata(raw.Header.Metadata)
                };

                logger.LogInformation("Copy: {From}@{Env} → {To}", action.From.Table, action.From.Env, action.To);
            }
            else
            {
                logger.LogWarning("Copy action: table {Table} not found in environment {Env}",
                    action.From.Table, action.From.Env);
            }
        }
        else if (action.Action == "merge" && action.From != null && action.Into != null && action.On != null)
        {
            var key = (action.From.Table, action.From.Env);
            if (!rawTables.TryGetValue(key, out var source))
            {
                logger.LogWarning("Merge action: table {Table} not found in environment {Env}",
                    action.From.Table, action.From.Env);
                continue;
            }

            if (!mergedTables.TryGetValue(action.Into, out var target))
            {
                logger.LogWarning("Merge action: target table {Table} not yet created (need a copy action first)",
                    action.Into);
                continue;
            }

            var strategy = action.ColumnStrategy ?? "auto";
            var conflictStrategy = action.ConflictStrategy ?? "report";
            var result = TableMerger.Merge(target, source, action.On, action.From.Env, strategy, conflictStrategy);
            mergedTables[action.Into] = result.Table;

            // Accumulate env metadata
            if (!allEnvMetadata.ContainsKey(action.Into))
                allEnvMetadata[action.Into] = new();
            foreach (var (eName, eMeta) in result.EnvMetadata)
                allEnvMetadata[action.Into][eName] = eMeta;

                // Set SourceRelDir on the env metadata from rawRelDirs
            foreach (var (eName, eMeta) in result.EnvMetadata)
            {
                var relKey = (action.From.Table, eName);
                eMeta.SourceRelDir = rawRelDirs.GetValueOrDefault(relKey, "");
            }

            if (result.Conflicts.Count > 0)
            {
                totalConflicts += result.Conflicts.Count;
                if (conflictStrategy == "split")
                {
                    logger.LogInformation("Resolved {Count} value conflict(s) in {Table} via column splitting",
                        result.Conflicts.Count, action.Into);
                }
                else
                {
                    foreach (var c in result.Conflicts.Take(5))
                        logger.LogError("CONFLICT in {Table}: key={Key} col={Col} target={TVal} source={SVal}",
                            action.Into, c.JoinKey, c.Column, c.TargetValue, c.SourceValue);
                    if (result.Conflicts.Count > 5)
                        logger.LogError("  ... and {More} more conflicts in {Table}",
                            result.Conflicts.Count - 5, action.Into);
                }
            }

            logger.LogInformation("Merge: {From}@{Env} → {Into} ({Conflicts} conflicts)",
                action.From.Table, action.From.Env, action.Into, result.Conflicts.Count);
        }
    }

    // Phase 3: Tables without merge actions — copy directly if only in one env
    var allTableNames = rawTables.Keys.Select(k => k.tableName).Distinct();
    foreach (var tableName in allTableNames)
    {
        if (tablesHandledByActions.Contains(tableName)) continue;

        var envs = rawTables.Keys.Where(k => k.tableName == tableName).Select(k => k.envName).ToList();
        if (envs.Count == 1)
        {
            var key = (tableName, envs[0]);
            mergedTables[tableName] = rawTables[key];

            // Track env metadata for passthrough tables too (for build path reconstruction)
            if (!allEnvMetadata.ContainsKey(tableName))
                allEnvMetadata[tableName] = new();
            allEnvMetadata[tableName][envs[0]] = new EnvMergeMetadata
            {
                ColumnOrder = rawTables[key].Columns.Select(c => c.Name).ToList(),
                ColumnOverrides = new(),
                ColumnRenames = new(),
                SourceRelDir = rawRelDirs.GetValueOrDefault(key, ""),
                FormatMetadata = ExtractFormatMetadata(rawTables[key].Header.Metadata)
            };

            logger.LogDebug("Passthrough: {Table} (only in {Env})", tableName, envs[0]);
        }
        else
        {
            // Multiple envs but no merge action — check if identical
            var first = rawTables[(tableName, envs[0])];
            bool allIdentical = true;
            foreach (var env in envs.Skip(1))
            {
                var diff = TableComparer.FindDifference(first, rawTables[(tableName, env)]);
                if (diff != null)
                {
                    allIdentical = false;
                    logger.LogWarning("Table {Table} exists in multiple envs but has no merge action and differs: {Diff}",
                        tableName, diff);
                    break;
                }
            }
            if (allIdentical)
            {
                mergedTables[tableName] = first;

                // Track env metadata for all envs (identical table, all envs have it)
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
                        SourceRelDir = rawRelDirs.GetValueOrDefault(key, ""),
                        FormatMetadata = ExtractFormatMetadata(rawTables[key].Header.Metadata)
                    };
                }

                logger.LogDebug("Passthrough: {Table} (identical across envs)", tableName);
            }
            else
            {
                // Just take the first env's version and warn
                mergedTables[tableName] = first;
            }
        }
    }

    // Phase 4: Write merged tables and update manifest
    manifest.Tables.Clear();
    int merged = 0;

    foreach (var (tableName, tableFile) in mergedTables.OrderBy(kv => kv.Key))
    {
        // Determine relative path from source format
        var relativePath = $"data/{tableFile.Header.SourceFormat}/{tableFile.Header.TableName}.json";

        // Enrich metadata with env info
        if (allEnvMetadata.TryGetValue(tableName, out var envMetas) && envMetas.Count > 0)
        {
            var metadata = tableFile.Header.Metadata ?? new Dictionary<string, object>();

            // Mark origin: "merged" for multi-env, env name for single-env
            if (envMetas.Count > 1)
                metadata[SourceOrigin.MetadataKey] = EnvironmentInfo.MergedOrigin;
            else
                metadata[SourceOrigin.MetadataKey] = envMetas.Keys.First();

            // Store per-env metadata (including sourceRelDir and format metadata)
            foreach (var (eName, eMeta) in envMetas)
            {
                metadata[eName] = new Dictionary<string, object?>
                {
                    ["columnOrder"] = eMeta.ColumnOrder,
                    ["columnOverrides"] = eMeta.ColumnOverrides.Count > 0 ? eMeta.ColumnOverrides : null,
                    ["columnRenames"] = eMeta.ColumnRenames.Count > 0 ? eMeta.ColumnRenames : null,
                    ["sourceRelDir"] = eMeta.SourceRelDir,
                    ["formatMetadata"] = eMeta.FormatMetadata
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

            await projectService.WriteTableFileAsync(project.FullName, relativePath, enrichedFile);
            if (envMetas.Count > 1) merged++;
        }
        else
        {
            await projectService.WriteTableFileAsync(project.FullName, relativePath, tableFile);
        }

        manifest.Tables[tableName] = relativePath;
    }

    await projectService.SaveProjectAsync(project.FullName, manifest);

    logger.LogInformation("Import complete: {Total} tables ({Merged} merged, {Conflicts} conflicts)",
        manifest.Tables.Count, merged, totalConflicts);


    static Dictionary<string, object>? ExtractFormatMetadata(Dictionary<string, object>? meta)
    {
        if (meta == null) return null;
        var result = new Dictionary<string, object>();
        foreach (var k in new[] { "cryptHeader", "header" })
            if (meta.TryGetValue(k, out var v) && v != null) result[k] = v;
        return result.Count > 0 ? result : null;
    }

}, importProjectOpt, importReimportOption);

// --- reimport command ---
var reimportCommand = new Command("reimport", "Wipe data/ and build/, re-import all environments, and rebuild");
var reimportProjectOpt = MakeProjectOption();
reimportCommand.AddOption(reimportProjectOpt);

reimportCommand.SetHandler(async (DirectoryInfo? projectOpt) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var project = ResolveProjectOrExit(projectOpt, logger);

    logger.LogInformation("Starting reimport for {Dir}", project.FullName);

    var importResult = await rootCommand.InvokeAsync(new[] { "import", "--project", project.FullName, "--reimport" });
    if (importResult != 0)
    {
        logger.LogError("Import step failed, aborting.");
        return;
    }

    // Run: build --all (this will also seed pack baselines for patchable envs)
    await rootCommand.InvokeAsync(new[] { "build", "--project", project.FullName, "--all" });

}, reimportProjectOpt);

// --- build command ---
var buildCommand = new Command("build", "Build data files from a Mimir project for a specific environment");
var buildProjectOption = MakeProjectOption();
var buildOutputOption = new Option<DirectoryInfo?>("--output", "Output directory (overrides configured buildPath; ignored with --all)")
{
    Arity = ArgumentArity.ZeroOrOne
};
buildOutputOption.AddAlias("-o");
var buildEnvOption = new Option<string?>("--env", "Environment to build for (e.g. server, client)");
buildEnvOption.AddAlias("-e");
var buildAllOption = new Option<bool>("--all", "Build all environments to their configured buildPaths");
buildCommand.AddOption(buildProjectOption);
buildCommand.AddOption(buildOutputOption);
buildCommand.AddOption(buildEnvOption);
buildCommand.AddOption(buildAllOption);

buildCommand.SetHandler(async (DirectoryInfo? projectOpt, DirectoryInfo? outputOpt, string? envName, bool buildAll) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var project = ResolveProjectOrExit(projectOpt, logger);
    var projectService = sp.GetRequiredService<IProjectService>();
    var providers = sp.GetServices<IDataProvider>().ToDictionary(p => p.FormatId);

    var manifest = await projectService.LoadProjectAsync(project.FullName);
    var template = await TemplateResolver.LoadAsync(project.FullName);
    var allEnvs = EnvironmentStore.LoadAll(project.FullName);

    // Default to --all when no env or output specified
    if (!buildAll && envName == null && outputOpt == null)
        buildAll = true;

    // Determine which environments to build
    var envsToBuild = new Dictionary<string, string>(); // envName → outputDir
    if (buildAll)
    {
        foreach (var (eName, eConfig) in allEnvs)
        {
            var buildPath = eConfig.BuildPath ?? Path.Combine("build", eName);
            var fullPath = Path.IsPathRooted(buildPath) ? buildPath : Path.Combine(project.FullName, buildPath);
            envsToBuild[eName] = fullPath;
        }
    }
    else if (envName != null)
    {
        string outputDir;
        if (outputOpt != null)
        {
            outputDir = outputOpt.FullName;
        }
        else if (allEnvs.TryGetValue(envName, out var eConfig))
        {
            var buildPath = eConfig.BuildPath ?? Path.Combine("build", envName);
            outputDir = Path.IsPathRooted(buildPath) ? buildPath : Path.Combine(project.FullName, buildPath);
        }
        else
        {
            logger.LogError("Environment '{Env}' not found. Use 'mimir env {Env} init' to configure it.", envName, envName);
            return;
        }
        envsToBuild[envName] = outputDir;
    }
    else
    {
        // Legacy: explicit --output
        envsToBuild[""] = outputOpt!.FullName;
    }

    // Load env metadata from template (for merged table splitting)
    // Build a map of tableName → envName → EnvMergeMetadata from persisted JSON metadata
    var envMetadataMap = new Dictionary<string, Dictionary<string, EnvMergeMetadata>>();

    foreach (var (eName, outputDir) in envsToBuild)
    {
        Directory.CreateDirectory(outputDir);
        int built = 0;

        logger.LogInformation("Building {Env} to {Output}", eName == "" ? "all" : eName, outputDir);

        // Buffer for multi-section files that share a sourceFile (e.g. ServerInfo.txt with multiple #DEFINE sections)
        var groupedEntries = new Dictionary<(IDataProvider provider, string dir, string sourceFile), List<TableEntry>>();

        foreach (var (name, entryPath) in manifest.Tables)
        {
            var tableFile = await projectService.ReadTableFileAsync(project.FullName, entryPath);

            // Check origin metadata to determine how to handle this table
            var origin = tableFile.Header.Metadata?.TryGetValue(SourceOrigin.MetadataKey, out var o) == true
                ? o?.ToString() : null;

            // Extract per-env metadata for this table
            EnvMergeMetadata? envMeta = null;
            if (eName != "" && tableFile.Header.Metadata != null
                && tableFile.Header.Metadata.TryGetValue(eName, out var rawMeta)
                && rawMeta is System.Text.Json.JsonElement je)
            {
                envMeta = EnvMergeMetadata.FromJsonElement(je);
            }

            TableFile outputTable;
            if (origin == EnvironmentInfo.MergedOrigin && eName != "")
            {
                // Multi-env merged table — split for this env
                if (envMeta == null)
                {
                    logger.LogDebug("Skipping {Table} for env {Env} (no env metadata)", name, eName);
                    continue;
                }
                outputTable = TableSplitter.Split(tableFile, eName, envMeta);
            }
            else if (origin != null && origin != EnvironmentInfo.MergedOrigin && origin != eName && eName != "")
            {
                // Single-env table that belongs to a different env — skip
                logger.LogDebug("Skipping {Table} for env {Env} (belongs to {Origin})", name, eName, origin);
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

            if (!providers.TryGetValue(entry.Schema.SourceFormat, out var provider))
            {
                logger.LogWarning("No provider for format {Format}, skipping {Table}",
                    entry.Schema.SourceFormat, name);
                continue;
            }

            try
            {
                var ext = provider.SupportedExtensions[0].TrimStart('.');

                // Use sourceRelDir to reconstruct original directory structure
                var relDir = envMeta?.SourceRelDir;
                var dir = string.IsNullOrEmpty(relDir) ? outputDir : Path.Combine(outputDir, relDir);

                // Check if this table has a sourceFile (multi-section txt files)
                var sourceFile = entry.Schema.Metadata?.TryGetValue("sourceFile", out var sf) == true
                    ? sf?.ToString() : null;

                if (!string.IsNullOrEmpty(sourceFile))
                {
                    // Buffer for grouped write — multiple sections → one file
                    var key = (provider, dir, sourceFile);
                    if (!groupedEntries.TryGetValue(key, out var group))
                    {
                        group = [];
                        groupedEntries[key] = group;
                    }
                    group.Add(entry);
                    built++;
                }
                else
                {
                    // Single-table file (SHN, etc.) — write immediately
                    Directory.CreateDirectory(dir);
                    await provider.WriteAsync(Path.Combine(dir, $"{name}.{ext}"), [entry]);
                    built++;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to build {Table}", name);
            }
        }

        // Write grouped multi-section files (configtable/shinetable with shared sourceFile)
        foreach (var ((gProvider, gDir, gSourceFile), entries) in groupedEntries)
        {
            try
            {
                // Sort by sectionIndex to preserve original file order
                entries.Sort((a, b) =>
                {
                    int idxA = GetSectionIndex(a.Schema.Metadata);
                    int idxB = GetSectionIndex(b.Schema.Metadata);
                    return idxA.CompareTo(idxB);
                });
                Directory.CreateDirectory(gDir);
                await gProvider.WriteAsync(Path.Combine(gDir, gSourceFile), entries);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to build grouped file {File}", gSourceFile);
            }
        }

        logger.LogInformation("Built {Count} files for {Env}", built, eName == "" ? "all" : eName);

        // Process copyFile actions — copy raw passthrough files (e.g. _ServerGroup.txt)
        foreach (var action in template.Actions.Where(a => a.Action == "copyFile"))
        {
            if (action.Env == null || action.Path == null) continue;
            // Only process for the matching env (or legacy "" mode = copy all)
            if (eName != "" && action.Env != eName) continue;

            if (!allEnvs.TryGetValue(action.Env, out var srcEnvConfig))
            {
                logger.LogWarning("CopyFile: environment '{Env}' not found", action.Env);
                continue;
            }

            if (srcEnvConfig.ImportPath == null)
            {
                logger.LogWarning("CopyFile: environment '{Env}' has no import-path configured", action.Env);
                continue;
            }

            var normalizedPath = action.Path.Replace('/', Path.DirectorySeparatorChar);
            var srcFile = Path.Combine(srcEnvConfig.ImportPath, normalizedPath);
            var destFile = Path.Combine(outputDir, normalizedPath);

            if (!File.Exists(srcFile))
            {
                logger.LogWarning("CopyFile: source not found: {Path}", srcFile);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(srcFile, destFile, overwrite: true);
            logger.LogInformation("CopyFile: {Path}", action.Path);
        }

        // Overrides: copy everything from overridesPath verbatim, winning over all other output
        var currentEnvConfig = eName != "" && allEnvs.TryGetValue(eName, out var ec) ? ec : null;
        if (currentEnvConfig?.OverridesPath != null)
        {
            var overridesDir = new DirectoryInfo(currentEnvConfig.OverridesPath);
            if (!overridesDir.Exists)
            {
                logger.LogWarning("Override path does not exist for {Env}: {Path}", eName, currentEnvConfig.OverridesPath);
            }
            else
            {
                var overrideFiles = overridesDir.EnumerateFiles("*", SearchOption.AllDirectories);
                int overrideCount = 0;
                foreach (var f in overrideFiles)
                {
                    var relPath = Path.GetRelativePath(overridesDir.FullName, f.FullName);
                    var destFile = Path.Combine(outputDir, relPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                    File.Copy(f.FullName, destFile, overwrite: true);
                    overrideCount++;
                }
                logger.LogInformation("Overrides: copied {Count} file(s) from {Path}", overrideCount, currentEnvConfig.OverridesPath);
            }
        }

        // Seed pack baseline from source import files for patchable envs.
        // Using source files as baseline ensures players receive Mimir's rebuilt SHN versions
        // (which differ from originals due to roundtrip zeroing of padded string garbage bytes)
        // in the first patch, allowing client integrity checks to pass.
        if (eName != "" && allEnvs.TryGetValue(eName, out var seedEnvConfig) && seedEnvConfig.SeedPackBaseline)
        {
            if (seedEnvConfig.ImportPath == null)
            {
                logger.LogWarning("Cannot seed pack baseline for {Env}: import-path not set.", eName);
            }
            else
            {
                var count = await Mimir.Cli.PackCommand.SeedBaselineAsync(
                    project.FullName, eName, seedEnvConfig.ImportPath, providers.Values.ToList());
                logger.LogInformation("Pack baseline seeded from source files ({Count} files)", count);
            }
        }
    }

}, buildProjectOption, buildOutputOption, buildEnvOption, buildAllOption);

// --- query command ---
var queryCommand = new Command("query", "Run SQL against a Mimir project");
var queryProjectOption = MakeProjectOption();
var sqlArg = new Argument<string>("sql", "SQL query to execute");
queryCommand.AddOption(queryProjectOption);
queryCommand.AddArgument(sqlArg);

queryCommand.SetHandler(async (DirectoryInfo? projectOpt, string sql) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var project = ResolveProjectOrExit(projectOpt, logger);
    var projectService = sp.GetRequiredService<IProjectService>();
    using var engine = sp.GetRequiredService<ISqlEngine>();

    var manifest = await projectService.LoadProjectAsync(project.FullName);
    await LoadTablesWithConstraints(engine, projectService, manifest, project.FullName, logger);

    logger.LogInformation("Loaded {Count} tables, executing query...", manifest.Tables.Count);

    var results = engine.Query(sql);
    foreach (var row in results)
    {
        var values = row.Select(kv => $"{kv.Key}={kv.Value}");
        Console.WriteLine(string.Join(" | ", values));
    }

    Console.WriteLine($"({results.Count} rows)");

}, queryProjectOption, sqlArg);

// --- dump command (diagnostic) ---
var dumpCommand = new Command("dump", "Dump decrypted SHN file structure for debugging");
var dumpFilesArg = new Argument<string[]>("files", "SHN files to dump");
dumpCommand.AddArgument(dumpFilesArg);
dumpCommand.SetHandler((string[] files) => Mimir.Cli.DiagnosticDump.DumpShnFile(files[0]), dumpFilesArg);

// --- analyze-types command (diagnostic) ---
var analyzeCommand = new Command("analyze-types", "Analyze all SHN type codes across a directory");
var analyzeDirArg = new Argument<DirectoryInfo>("directory", "Path to SHN files directory");
analyzeCommand.AddArgument(analyzeDirArg);
analyzeCommand.SetHandler((DirectoryInfo dir) => Mimir.Cli.TypeAnalysis.AnalyzeAllTypes(dir.FullName), analyzeDirArg);

// --- validate command ---
var validateCommand = new Command("validate", "Validate a Mimir project against constraint rules");
var validateProjectOpt = MakeProjectOption();
validateCommand.AddOption(validateProjectOpt);

validateCommand.SetHandler(async (DirectoryInfo? projectOpt) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var project = ResolveProjectOrExit(projectOpt, logger);
    var projectService = sp.GetRequiredService<IProjectService>();
    using var engine = sp.GetRequiredService<ISqlEngine>();

    var manifest = await projectService.LoadProjectAsync(project.FullName);
    var constraints = await LoadDefinitions(project.FullName, logger);

    if (constraints.Constraints.Count == 0)
    {
        logger.LogWarning("No constraints found. Create {File} or {Template} to add constraints.",
            DefinitionResolver.DefinitionsFileName, TemplateResolver.TemplateFileName);
        return;
    }

    // Load with FK constraints in schema (empty values mapped to NULL)
    await LoadTablesWithConstraints(engine, projectService, manifest, project.FullName, logger);

    // Resolve for detailed violation reporting
    var resolved = DefinitionResolver.Resolve(constraints, manifest);
    logger.LogInformation("Resolved {Count} constraint bindings from {RuleCount} rules",
        resolved.Count, constraints.Constraints.Count);

    int violations = 0;
    int checks = 0;

    foreach (var constraint in resolved)
    {
        var sourceTableFile = await projectService.ReadTableFileAsync(
            project.FullName, manifest.Tables[constraint.SourceTable]);
        var matchingColumns = sourceTableFile.Columns
            .Where(c => DefinitionResolver.GlobMatch(c.Name, constraint.ColumnPattern))
            .ToList();

        foreach (var col in matchingColumns)
        {
            checks++;

            // Empty values are already NULL from loading, so just check non-null values
            var sql = $@"SELECT s.[{col.Name}] as value, COUNT(*) as cnt
                FROM [{constraint.SourceTable}] s
                WHERE s.[{col.Name}] IS NOT NULL
                  AND s.[{col.Name}] NOT IN (SELECT [{constraint.TargetColumn}] FROM [{constraint.TargetTable}])
                GROUP BY s.[{col.Name}]
                LIMIT 10";

            try
            {
                var results = engine.Query(sql);
                if (results.Count > 0)
                {
                    violations += results.Count;
                    var desc = constraint.Rule.Description ?? $"{constraint.SourceTable}.{col.Name}";
                    Console.WriteLine($"VIOLATION: {constraint.SourceTable}.{col.Name} -> {constraint.TargetTable}.{constraint.TargetColumn}");
                    Console.WriteLine($"  Rule: {desc}");
                    foreach (var row in results.Take(5))
                    {
                        Console.WriteLine($"  Missing: \"{row["value"]}\" ({row["cnt"]} occurrences)");
                    }
                    if (results.Count > 5)
                        Console.WriteLine($"  ... and {results.Count - 5} more distinct values");
                    Console.WriteLine();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Constraint check failed for {Source}.{Column} -> {Target}.{TargetCol}: {Error}",
                    constraint.SourceTable, col.Name, constraint.TargetTable, constraint.TargetColumn, ex.Message);
            }
        }
    }

    if (violations == 0)
        Console.WriteLine($"All {checks} constraint checks passed.");
    else
        Console.WriteLine($"{violations} violations found across {checks} checks.");

}, validateProjectOpt);

// --- edit command ---
var editCommand = new Command("edit", "Run SQL to modify project data and save changes back to JSON");
var editProjectOption = MakeProjectOption();
var editSqlArg = new Argument<string>("sql", "SQL statement(s) to execute (UPDATE, INSERT, DELETE)");
editCommand.AddOption(editProjectOption);
editCommand.AddArgument(editSqlArg);

editCommand.SetHandler(async (DirectoryInfo? projectOpt, string sql) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var project = ResolveProjectOrExit(projectOpt, logger);
    var projectService = sp.GetRequiredService<IProjectService>();
    using var engine = sp.GetRequiredService<ISqlEngine>();

    var manifest = await projectService.LoadProjectAsync(project.FullName);

    // Load all tables, preserving headers for write-back
    var tableHeaders = new Dictionary<string, TableHeader>();
    var tableSchemas = new Dictionary<string, TableSchema>();

    foreach (var (name, entryPath) in manifest.Tables)
    {
        var tableFile = await projectService.ReadTableFileAsync(project.FullName, entryPath);
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

    logger.LogInformation("Loaded {Count} tables", manifest.Tables.Count);

    // Execute modification SQL
    var affected = engine.Execute(sql);
    logger.LogInformation("Executed: {Affected} rows affected", affected);

    if (affected == 0)
    {
        Console.WriteLine("No rows changed, nothing to save.");
        return;
    }

    // Extract all tables and save back to JSON
    int saved = 0;
    foreach (var (name, entryPath) in manifest.Tables)
    {
        var schema = tableSchemas[name];
        var extracted = engine.ExtractTable(schema);

        var tableFile = new TableFile
        {
            Header = tableHeaders[name],
            Columns = schema.Columns,
            Data = extracted.Rows
        };

        await projectService.WriteTableFileAsync(project.FullName, entryPath, tableFile);
        saved++;
    }

    logger.LogInformation("Saved {Count} tables back to project", saved);
    Console.WriteLine($"{affected} rows modified, {saved} tables saved.");

}, editProjectOption, editSqlArg);

// --- shell command (interactive SQL) ---
var shellCommand = new Command("shell", "Interactive SQL shell against a Mimir project");
var shellProjectOpt = MakeProjectOption();
shellCommand.AddOption(shellProjectOpt);

shellCommand.SetHandler(async (DirectoryInfo? projectOpt) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var project = ResolveProjectOrExit(projectOpt, logger);
    var projectService = sp.GetRequiredService<IProjectService>();
    using var engine = sp.GetRequiredService<ISqlEngine>();

    var manifest = await projectService.LoadProjectAsync(project.FullName);
    var definitions = await LoadDefinitions(project.FullName, logger);

    // Load all tables, preserving headers for write-back
    var tableHeaders = new Dictionary<string, TableHeader>();
    var tableSchemas = new Dictionary<string, TableSchema>();

    foreach (var (name, entryPath) in manifest.Tables)
    {
        var tableFile = await projectService.ReadTableFileAsync(project.FullName, entryPath);
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

    Console.WriteLine($"Loaded {manifest.Tables.Count} tables. Type SQL or a dot-command.");
    Console.WriteLine("  .tables          List all tables");
    Console.WriteLine("  .schema TABLE    Show column definitions");
    Console.WriteLine("  .save            Save all tables back to JSON");
    Console.WriteLine("  .quit / .exit    Quit (prompts to save if unsaved changes)");
    Console.WriteLine();

    bool dirty = false;

    while (true)
    {
        Console.Write("mimir> ");
        var line = Console.ReadLine();
        if (line == null) break; // EOF
        line = line.Trim();
        if (line == "") continue;

        // Dot-commands
        if (line.StartsWith('.'))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLowerInvariant();

            switch (cmd)
            {
                case ".quit" or ".exit":
                    if (dirty)
                    {
                        Console.Write("Unsaved changes. Save before quitting? [y/N] ");
                        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
                        if (answer == "y" || answer == "yes")
                            await SaveAllTables(engine, projectService, manifest, tableHeaders, tableSchemas, project.FullName);
                    }
                    return;

                case ".tables":
                    var tables = engine.ListTables();
                    foreach (var t in tables)
                        Console.WriteLine($"  {t}");
                    Console.WriteLine($"({tables.Count} tables)");
                    break;

                case ".schema" when parts.Length > 1:
                    var tblName = parts[1];
                    if (tableSchemas.TryGetValue(tblName, out var sch))
                    {
                        TableKeyInfo? tblInfo = null;
                        definitions.Tables?.TryGetValue(tblName, out tblInfo);
                        var annotations = tblInfo?.ColumnAnnotations;

                        Console.WriteLine($"-- {tblName} ({sch.SourceFormat})");
                        if (tblInfo?.IdColumn != null) Console.WriteLine($"-- PK: {tblInfo.IdColumn}");
                        if (tblInfo?.KeyColumn != null) Console.WriteLine($"-- Key: {tblInfo.KeyColumn}");

                        foreach (var col in sch.Columns)
                        {
                            var colLine = $"  [{col.Name}] {col.Type} (len={col.Length}, shn={col.SourceTypeCode})";
                            if (annotations?.TryGetValue(col.Name, out var ann) == true)
                            {
                                if (ann.DisplayName != null) colLine += $"  -- {ann.DisplayName}";
                                if (ann.Description != null) colLine += $": {ann.Description}";
                            }
                            Console.WriteLine(colLine);
                        }
                    }
                    else
                        Console.WriteLine($"Unknown table: {tblName}");
                    break;

                case ".save":
                    await SaveAllTables(engine, projectService, manifest, tableHeaders, tableSchemas, project.FullName);
                    dirty = false;
                    break;

                default:
                    Console.WriteLine($"Unknown command: {cmd}");
                    break;
            }
            continue;
        }

        // SQL
        try
        {
            var upper = line.TrimStart().ToUpperInvariant();
            if (upper.StartsWith("SELECT") || upper.StartsWith("PRAGMA") || upper.StartsWith("EXPLAIN"))
            {
                var results = engine.Query(line);
                if (results.Count > 0)
                {
                    // Print header
                    var cols = results[0].Keys.ToList();
                    Console.WriteLine(string.Join(" | ", cols));
                    Console.WriteLine(new string('-', cols.Sum(c => c.Length + 3)));
                    foreach (var row in results)
                    {
                        Console.WriteLine(string.Join(" | ", cols.Select(c => row[c]?.ToString() ?? "NULL")));
                    }
                }
                Console.WriteLine($"({results.Count} rows)");
            }
            else
            {
                var affected = engine.Execute(line);
                Console.WriteLine($"{affected} rows affected");
                if (affected > 0) dirty = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

}, shellProjectOpt);

// --- init-template command ---
var initTemplateCommand = new Command("init-template", "Auto-generate a mimir.template.json from environment scans");
var initTemplateProjectOpt = MakeProjectOption();
var initTemplatePassthroughOption = new Option<string[]>("--passthrough",
    "Copy all non-table files from the named environment(s) to build output (e.g. --passthrough server)")
{
    AllowMultipleArgumentsPerToken = false,
    Arity = ArgumentArity.ZeroOrMore
};
initTemplateCommand.AddOption(initTemplateProjectOpt);
initTemplateCommand.AddOption(initTemplatePassthroughOption);

initTemplateCommand.SetHandler(async (DirectoryInfo? projectOpt, string[] passthroughEnvs) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var project = ResolveProjectOrExit(projectOpt, logger);
    var projectService = sp.GetRequiredService<IProjectService>();
    var providers = sp.GetServices<IDataProvider>().ToList();

    var manifest = await projectService.LoadProjectAsync(project.FullName);

    var allEnvs = EnvironmentStore.LoadAll(project.FullName);
    if (allEnvs.Count == 0)
    {
        logger.LogError("No environments configured. Use: mimir env <name> init <importPath>");
        return;
    }

    // Scan each environment
    var envTables = new Dictionary<(string table, string env), TableFile>();
    var envOrder = allEnvs.Keys.ToList();

    foreach (var (envName, envConfig) in allEnvs)
    {
        if (envConfig.ImportPath == null) { logger.LogWarning("Skipping {Env}: import-path not set.", envName); continue; }
        var sourceDir = new DirectoryInfo(envConfig.ImportPath);
        if (!sourceDir.Exists)
        {
            logger.LogWarning("Import path does not exist for environment {Env}: {Path}", envName, envConfig.ImportPath);
            continue;
        }

        logger.LogInformation("Scanning {Env}: {Path}", envName, sourceDir.FullName);
        var tables = await ReadAllTables(sourceDir, providers, logger);

        foreach (var (tableName, (tableFile, _)) in tables)
            envTables[(tableName, envName)] = tableFile;

        logger.LogInformation("Found {Count} tables in {Env}", tables.Count, envName);
    }

    // Detect passthrough files for envs opted-in via --passthrough
    var passthroughFiles = new List<(string env, string path)>();
    var passthroughEnvSet = passthroughEnvs.ToHashSet(StringComparer.OrdinalIgnoreCase);

    foreach (var (envName, envConfig) in allEnvs)
    {
        if (!passthroughEnvSet.Contains(envName)) continue;
        if (envConfig.ImportPath == null) continue;
        var sourceDir = new DirectoryInfo(envConfig.ImportPath);
        if (!sourceDir.Exists) continue;

        foreach (var file in sourceDir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (providers.Any(p => p.CanHandle(file.FullName))) continue;

            var relPath = Path.GetRelativePath(sourceDir.FullName, file.FullName).Replace('\\', '/');
            passthroughFiles.Add((envName, relPath));
        }
    }

    var template = TemplateGenerator.Generate(envTables, envOrder, passthroughFiles);
    await TemplateResolver.SaveAsync(project.FullName, template);

    var copyCount = template.Actions.Count(a => a.Action == "copy");
    var mergeCount = template.Actions.Count(a => a.Action == "merge");
    var pkCount = template.Actions.Count(a => a.Action == "setPrimaryKey");
    var ukCount = template.Actions.Count(a => a.Action == "setUniqueKey");
    var copyFileCount = template.Actions.Count(a => a.Action == "copyFile");

    logger.LogInformation("Generated {File} with {Actions} actions: {Copy} copy, {Merge} merge, {PK} setPK, {UK} setUK, {CopyFile} copyFile",
        TemplateResolver.TemplateFileName, template.Actions.Count, copyCount, mergeCount, pkCount, ukCount, copyFileCount);

}, initTemplateProjectOpt, initTemplatePassthroughOption);

// --- edit-template command ---
var editTemplateCommand = new Command("edit-template", "Modify merge actions in mimir.template.json");
var editTemplateProjectOpt = MakeProjectOption();
var editTemplateTableOption = new Option<string?>("--table", "Target a specific table (applies to all merge actions if omitted)");
editTemplateTableOption.AddAlias("-t");
var conflictStrategyOption = new Option<string?>("--conflict-strategy", "Set conflict strategy on merge actions (report or split)");
editTemplateCommand.AddOption(editTemplateProjectOpt);
editTemplateCommand.AddOption(editTemplateTableOption);
editTemplateCommand.AddOption(conflictStrategyOption);

editTemplateCommand.SetHandler(async (DirectoryInfo? projectOpt, string? table, string? conflictStrategyVal) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var project = ResolveProjectOrExit(projectOpt, logger);

    var templatePath = Path.Combine(project.FullName, TemplateResolver.TemplateFileName);
    if (!File.Exists(templatePath))
    {
        logger.LogError("No {File} found in {Dir}. Run init-template first.", TemplateResolver.TemplateFileName, project.FullName);
        return;
    }

    var json = await File.ReadAllTextAsync(templatePath);
    var doc = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
    var actions = doc["actions"]!.AsArray();

    int modified = 0;
    foreach (var action in actions)
    {
        var obj = action!.AsObject();
        if (obj["action"]?.GetValue<string>() != "merge") continue;
        if (table != null && obj["into"]?.GetValue<string>() != table) continue;

        if (conflictStrategyVal != null)
        {
            obj["conflictStrategy"] = conflictStrategyVal;
            modified++;
        }
    }

    if (modified == 0)
    {
        logger.LogWarning("No merge actions matched{Table}.", table != null ? $" for table {table}" : "");
        return;
    }

    var writeOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
    await File.WriteAllTextAsync(templatePath, doc.ToJsonString(writeOptions));
    logger.LogInformation("Updated {Count} merge action(s) in {File}", modified, TemplateResolver.TemplateFileName);

}, editTemplateProjectOpt, editTemplateTableOption, conflictStrategyOption);

// --- pack command ---
var packCommand = new Command("pack", "Package client build output into incremental patch zips");
var packProjectOption = MakeProjectOption();
var packOutputArg = new Argument<DirectoryInfo>("output-dir", "Path to output directory for patches and patch-index.json");
var packEnvOption = new Option<string>("--env", () => "client", "Environment to pack (default: client)");
packEnvOption.AddAlias("-e");
var packBaseUrlOption = new Option<string?>("--base-url", "URL prefix for patch URLs in the index (e.g. https://patches.example.com/)");
packCommand.AddOption(packProjectOption);
packCommand.AddArgument(packOutputArg);
packCommand.AddOption(packEnvOption);
packCommand.AddOption(packBaseUrlOption);

packCommand.SetHandler(async (DirectoryInfo? projectOpt, DirectoryInfo outputDir, string envName, string? baseUrl) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var project = ResolveProjectOrExit(projectOpt, logger);
    var projectService = sp.GetRequiredService<IProjectService>();

    var manifest = await projectService.LoadProjectAsync(project.FullName);
    var envConfig = EnvironmentStore.Load(project.FullName, envName);
    if (envConfig == null)
    {
        logger.LogError("Environment '{Env}' not found. Use 'mimir env {Env} init' to configure it.", envName, envName);
        return;
    }

    // Determine build directory: use configured buildPath or default build/<env>
    var buildPath = envConfig.BuildPath ?? Path.Combine("build", envName);
    var buildDir = Path.IsPathRooted(buildPath) ? buildPath : Path.Combine(project.FullName, buildPath);

    if (!Directory.Exists(buildDir))
    {
        logger.LogError("Build directory does not exist: {Path}. Run 'mimir build --all' first.", buildDir);
        return;
    }

    // Use the parent of the env build dir as the build root (pack expects build/<env>/ structure)
    var buildRoot = Path.GetDirectoryName(buildDir)!;

    var (message, version) = await Mimir.Cli.PackCommand.ExecuteAsync(
        project.FullName, buildRoot, outputDir.FullName, envName, baseUrl);

    if (version > 0)
        logger.LogInformation("{Message}", message);
    else
        Console.WriteLine(message);

}, packProjectOption, packOutputArg, packEnvOption, packBaseUrlOption);

// --- snapshot command ---
var snapshotCommand = new Command("snapshot",
    "Build a complete client snapshot by applying all patches on top of source import files");
var snapshotProjectOption = MakeProjectOption();
var snapshotOutputArg = new Argument<DirectoryInfo>("output-dir",
    "Directory to write the snapshot into");
var snapshotEnvOption = new Option<string>("--env", () => "client",
    "Environment to snapshot (default: client)");
snapshotEnvOption.AddAlias("-e");
var snapshotPatchesOption = new Option<DirectoryInfo?>("--patches",
    "Directory containing patch-index.json and patches/ subdir (defaults to <project>/patches/)");
snapshotCommand.AddOption(snapshotProjectOption);
snapshotCommand.AddArgument(snapshotOutputArg);
snapshotCommand.AddOption(snapshotEnvOption);
snapshotCommand.AddOption(snapshotPatchesOption);

snapshotCommand.SetHandler(async (DirectoryInfo? projectOpt, DirectoryInfo outputDir,
    string envName, DirectoryInfo? patchesOpt) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var project = ResolveProjectOrExit(projectOpt, logger);

    var envConfig = EnvironmentStore.Load(project.FullName, envName);
    if (envConfig == null)
    {
        logger.LogError("Environment '{Env}' not found. Use 'mimir env {Env} init' to configure it.",
            envName, envName);
        return;
    }

    if (envConfig.ImportPath == null)
    {
        logger.LogError("Environment '{Env}' has no import-path configured.", envName);
        return;
    }

    var patchesDir = patchesOpt?.FullName ?? Path.Combine(project.FullName, "patches");
    Directory.CreateDirectory(outputDir.FullName);

    logger.LogInformation("Snapshot: {Env} → {Output}", envName, outputDir.FullName);
    logger.LogInformation("  Source: {ImportPath}", envConfig.ImportPath);
    logger.LogInformation("  Patches: {PatchesDir}", patchesDir);

    var result = await Mimir.Cli.SnapshotCommand.ExecuteAsync(
        envConfig.ImportPath, patchesDir, outputDir.FullName);

    if (result.MissingPatches > 0)
        logger.LogWarning("{Missing} patch file(s) not found on disk and were skipped.", result.MissingPatches);

    if (result.AppliedPatches == 0 && result.MissingPatches == 0)
        logger.LogInformation("Snapshot complete: {Source} source files copied (no patches found).",
            result.SourceFiles);
    else
        logger.LogInformation(
            "Snapshot complete: {Source} source files + {Patches} patches applied ({Files} file updates), version {Version}.",
            result.SourceFiles, result.AppliedPatches, result.PatchedFiles, result.LatestVersion);

}, snapshotProjectOption, snapshotOutputArg, snapshotEnvOption, snapshotPatchesOption);

// --- env command ---
var envCommand = new Command("env", "Manage project environments (environments/<name>.json)");
var envProjectOption = MakeProjectOption();
var envArgsArg = new Argument<string[]>("args", "env <name|all> <verb> [args...]")
{
    Arity = ArgumentArity.ZeroOrMore
};
envCommand.AddOption(envProjectOption);
envCommand.AddArgument(envArgsArg);
envCommand.SetHandler(async (DirectoryInfo? projectOpt, string[] tokens) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var project = ResolveProjectOrExit(projectOpt, logger);
    await Mimir.Cli.EnvCommand.HandleAsync(project.FullName, tokens, logger);
}, envProjectOption, envArgsArg);

rootCommand.AddCommand(envCommand);
rootCommand.AddCommand(initCommand);
rootCommand.AddCommand(importCommand);
rootCommand.AddCommand(reimportCommand);
rootCommand.AddCommand(buildCommand);
rootCommand.AddCommand(queryCommand);
rootCommand.AddCommand(editCommand);
rootCommand.AddCommand(shellCommand);
rootCommand.AddCommand(validateCommand);
rootCommand.AddCommand(initTemplateCommand);
rootCommand.AddCommand(editTemplateCommand);
rootCommand.AddCommand(packCommand);
rootCommand.AddCommand(snapshotCommand);
rootCommand.AddCommand(dumpCommand);
rootCommand.AddCommand(analyzeCommand);

return await rootCommand.InvokeAsync(args);

// --- import helper ---

async Task<Dictionary<string, (TableFile file, string relDir)>> ReadAllTables(
    DirectoryInfo sourceDir, List<IDataProvider> providers, ILogger logger)
{
    var tables = new Dictionary<string, (TableFile file, string relDir)>();
    var files = sourceDir.EnumerateFiles("*", SearchOption.AllDirectories);

    foreach (var file in files)
    {
        var provider = providers.FirstOrDefault(p => p.CanHandle(file.FullName));

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

// --- shared helpers ---

async Task LoadTablesWithConstraints(
    ISqlEngine engine, IProjectService projectService, MimirProject manifest,
    string projectDir, ILogger logger)
{
    // Load constraints from template (preferred) or definitions file (legacy)
    var definitions = await LoadDefinitions(projectDir, logger);
    var resolved = DefinitionResolver.Resolve(definitions, manifest);

    if (resolved.Count > 0 || definitions.Tables?.Count > 0)
    {
        engine.SetConstraints(resolved, definitions.Tables);
        logger.LogInformation("Registered {Count} FK constraints, {KeyCount} table keys",
            resolved.Count, definitions.Tables?.Count ?? 0);
    }

    // Determine load order (referenced tables first)
    var loadOrder = engine.GetLoadOrder(manifest.Tables.Keys);

    foreach (var tableName in loadOrder)
    {
        var entryPath = manifest.Tables[tableName];
        var tableFile = await projectService.ReadTableFileAsync(projectDir, entryPath);

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
        engine.LoadTable(entry);
    }

    if (resolved.Count > 0 || definitions.Tables?.Count > 0)
        engine.EnableForeignKeys();

    logger.LogInformation("Loaded {Count} tables into SQLite", manifest.Tables.Count);
}

async Task<ProjectDefinitions> LoadDefinitions(string projectDir, ILogger logger)
{
    // Prefer template if it exists
    var template = await TemplateResolver.LoadAsync(projectDir);
    if (template.Actions.Count > 0)
    {
        logger.LogDebug("Using template for constraints/keys");
        return TemplateDefinitionBridge.ToDefinitions(template);
    }

    // Fall back to legacy definitions file
    return await DefinitionResolver.LoadAsync(projectDir);
}

async Task SaveAllTables(
    ISqlEngine engine, IProjectService projectService, MimirProject manifest,
    Dictionary<string, TableHeader> headers, Dictionary<string, TableSchema> schemas,
    string projectDir)
{
    int saved = 0;
    foreach (var (name, entryPath) in manifest.Tables)
    {
        var schema = schemas[name];
        var extracted = engine.ExtractTable(schema);

        var tableFile = new TableFile
        {
            Header = headers[name],
            Columns = schema.Columns,
            Data = extracted.Rows
        };

        await projectService.WriteTableFileAsync(projectDir, entryPath, tableFile);
        saved++;
    }
    Console.WriteLine($"Saved {saved} tables.");
}

static int GetSectionIndex(IDictionary<string, object>? metadata)
{
    if (metadata?.TryGetValue("sectionIndex", out var val) != true) return int.MaxValue;
    return val switch
    {
        int i => i,
        long l => (int)l,
        System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetInt32(),
        _ => int.TryParse(val?.ToString(), out int parsed) ? parsed : int.MaxValue
    };
}

// --- project discovery (git-like CWD detection) ---

static string? FindProjectRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "mimir.json")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}

static DirectoryInfo ResolveProjectOrExit(DirectoryInfo? provided, ILogger logger)
{
    if (provided != null) return provided;

    var found = FindProjectRoot();
    if (found != null)
    {
        logger.LogDebug("Using project at {Dir} (discovered from CWD)", found);
        return new DirectoryInfo(found);
    }

    Console.Error.WriteLine("error: not a mimir project (or any parent up to root: mimir.json not found)");
    Console.Error.WriteLine("hint: create a project with 'mimir init <name> --env server=<path>'");
    Environment.Exit(1);
    return null!; // unreachable
}
