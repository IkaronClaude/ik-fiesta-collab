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

// --- import command ---
var importCommand = new Command("import", "Import data files into a Mimir project using environments from mimir.json");
var importProjectArg = new Argument<DirectoryInfo>("project", "Path to Mimir project directory (must have mimir.json with environments)");
importCommand.AddArgument(importProjectArg);

importCommand.SetHandler(async (DirectoryInfo project) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var projectService = sp.GetRequiredService<IProjectService>();
    var providers = sp.GetServices<IDataProvider>().ToList();

    var manifest = await projectService.LoadProjectAsync(project.FullName);

    if (manifest.Environments is null || manifest.Environments.Count == 0)
    {
        logger.LogError("No environments defined in mimir.json. Add an 'environments' section with import paths.");
        return;
    }

    // Load template for merge actions
    var template = await TemplateResolver.LoadAsync(project.FullName);

    // Phase 1: Read all tables from each environment into memory
    var rawTables = new Dictionary<(string tableName, string envName), TableFile>();
    var rawRelDirs = new Dictionary<(string tableName, string envName), string>(); // track source-relative dirs

    foreach (var (envName, envConfig) in manifest.Environments)
    {
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
                mergedTables[action.To] = raw;
                tablesHandledByActions.Add(action.To);

                // Store base env column order and source path
                if (!allEnvMetadata.ContainsKey(action.To))
                    allEnvMetadata[action.To] = new();
                allEnvMetadata[action.To][action.From.Env] = new EnvMergeMetadata
                {
                    ColumnOrder = raw.Columns.Select(c => c.Name).ToList(),
                    ColumnOverrides = new(),
                    ColumnRenames = new(),
                    SourceRelDir = rawRelDirs.GetValueOrDefault(key, "")
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
                SourceRelDir = rawRelDirs.GetValueOrDefault(key, "")
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
                        SourceRelDir = rawRelDirs.GetValueOrDefault(key, "")
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

            // Store per-env metadata (including sourceRelDir)
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

}, importProjectArg);

// --- build command ---
var buildCommand = new Command("build", "Build data files from a Mimir project for a specific environment");
var buildProjectArg = new Argument<DirectoryInfo>("project", "Path to Mimir project directory");
var buildOutputArg = new Argument<DirectoryInfo>("output", "Path to output data directory");
var buildEnvOption = new Option<string?>("--env", "Environment to build for (e.g. server, client)");
buildEnvOption.AddAlias("-e");
var buildAllOption = new Option<bool>("--all", "Build all environments to their configured buildPaths");
buildCommand.AddArgument(buildProjectArg);
buildCommand.AddArgument(buildOutputArg);
buildCommand.AddOption(buildEnvOption);
buildCommand.AddOption(buildAllOption);

buildCommand.SetHandler(async (DirectoryInfo project, DirectoryInfo output, string? envName, bool buildAll) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var projectService = sp.GetRequiredService<IProjectService>();
    var providers = sp.GetServices<IDataProvider>().ToDictionary(p => p.FormatId);

    var manifest = await projectService.LoadProjectAsync(project.FullName);
    var template = await TemplateResolver.LoadAsync(project.FullName);

    // Determine which environments to build
    var envsToBuild = new Dictionary<string, string>(); // envName → outputDir
    if (buildAll && manifest.Environments != null)
    {
        foreach (var (eName, eConfig) in manifest.Environments)
        {
            var buildPath = eConfig.BuildPath ?? Path.Combine("build", eName);
            var fullPath = Path.IsPathRooted(buildPath) ? buildPath : Path.Combine(project.FullName, buildPath);
            envsToBuild[eName] = fullPath;
        }
    }
    else if (envName != null)
    {
        envsToBuild[envName] = output.FullName;
    }
    else
    {
        // Legacy mode: build everything to output without splitting
        envsToBuild[""] = output.FullName;
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
    }

}, buildProjectArg, buildOutputArg, buildEnvOption, buildAllOption);

// --- query command ---
var queryCommand = new Command("query", "Run SQL against a Mimir project");
var queryProjectArg = new Argument<DirectoryInfo>("project", "Path to Mimir project directory");
var sqlArg = new Argument<string>("sql", "SQL query to execute");
queryCommand.AddArgument(queryProjectArg);
queryCommand.AddArgument(sqlArg);

queryCommand.SetHandler(async (DirectoryInfo project, string sql) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
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

}, queryProjectArg, sqlArg);

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
var validateProjectArg = new Argument<DirectoryInfo>("project", "Path to Mimir project directory");
validateCommand.AddArgument(validateProjectArg);

validateCommand.SetHandler(async (DirectoryInfo project) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
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

}, validateProjectArg);

// --- edit command ---
var editCommand = new Command("edit", "Run SQL to modify project data and save changes back to JSON");
var editProjectArg = new Argument<DirectoryInfo>("project", "Path to Mimir project directory");
var editSqlArg = new Argument<string>("sql", "SQL statement(s) to execute (UPDATE, INSERT, DELETE)");
editCommand.AddArgument(editProjectArg);
editCommand.AddArgument(editSqlArg);

editCommand.SetHandler(async (DirectoryInfo project, string sql) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
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

}, editProjectArg, editSqlArg);

// --- shell command (interactive SQL) ---
var shellCommand = new Command("shell", "Interactive SQL shell against a Mimir project");
var shellProjectArg = new Argument<DirectoryInfo>("project", "Path to Mimir project directory");
shellCommand.AddArgument(shellProjectArg);

shellCommand.SetHandler(async (DirectoryInfo project) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
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

}, shellProjectArg);

// --- init-template command ---
var initTemplateCommand = new Command("init-template", "Auto-generate a mimir.template.json from environment scans");
var initTemplateProjectArg = new Argument<DirectoryInfo>("project", "Path to Mimir project directory");
initTemplateCommand.AddArgument(initTemplateProjectArg);

initTemplateCommand.SetHandler(async (DirectoryInfo project) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var projectService = sp.GetRequiredService<IProjectService>();
    var providers = sp.GetServices<IDataProvider>().ToList();

    var manifest = await projectService.LoadProjectAsync(project.FullName);

    if (manifest.Environments is null || manifest.Environments.Count == 0)
    {
        logger.LogError("No environments defined in mimir.json. Add an 'environments' section first.");
        return;
    }

    // Scan each environment
    var envTables = new Dictionary<(string table, string env), TableFile>();
    var envOrder = manifest.Environments.Keys.ToList();

    foreach (var (envName, envConfig) in manifest.Environments)
    {
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

    var template = TemplateGenerator.Generate(envTables, envOrder);
    await TemplateResolver.SaveAsync(project.FullName, template);

    var copyCount = template.Actions.Count(a => a.Action == "copy");
    var mergeCount = template.Actions.Count(a => a.Action == "merge");
    var pkCount = template.Actions.Count(a => a.Action == "setPrimaryKey");
    var ukCount = template.Actions.Count(a => a.Action == "setUniqueKey");

    logger.LogInformation("Generated {File} with {Actions} actions: {Copy} copy, {Merge} merge, {PK} setPK, {UK} setUK",
        TemplateResolver.TemplateFileName, template.Actions.Count, copyCount, mergeCount, pkCount, ukCount);

}, initTemplateProjectArg);

// --- edit-template command ---
var editTemplateCommand = new Command("edit-template", "Modify merge actions in mimir.template.json");
var editTemplateProjectArg = new Argument<DirectoryInfo>("project", "Path to Mimir project directory");
var editTemplateTableOption = new Option<string?>("--table", "Target a specific table (applies to all merge actions if omitted)");
editTemplateTableOption.AddAlias("-t");
var conflictStrategyOption = new Option<string?>("--conflict-strategy", "Set conflict strategy on merge actions (report or split)");
editTemplateCommand.AddArgument(editTemplateProjectArg);
editTemplateCommand.AddOption(editTemplateTableOption);
editTemplateCommand.AddOption(conflictStrategyOption);

editTemplateCommand.SetHandler(async (DirectoryInfo project, string? table, string? conflictStrategyVal) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();

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

}, editTemplateProjectArg, editTemplateTableOption, conflictStrategyOption);

// --- pack command ---
var packCommand = new Command("pack", "Package client build output into incremental patch zips");
var packProjectArg = new Argument<DirectoryInfo>("project", "Path to Mimir project directory");
var packOutputArg = new Argument<DirectoryInfo>("output-dir", "Path to output directory for patches and patch-index.json");
var packEnvOption = new Option<string>("--env", () => "client", "Environment to pack (default: client)");
packEnvOption.AddAlias("-e");
var packBaseUrlOption = new Option<string?>("--base-url", "URL prefix for patch URLs in the index (e.g. https://patches.example.com/)");
packCommand.AddArgument(packProjectArg);
packCommand.AddArgument(packOutputArg);
packCommand.AddOption(packEnvOption);
packCommand.AddOption(packBaseUrlOption);

packCommand.SetHandler(async (DirectoryInfo project, DirectoryInfo outputDir, string envName, string? baseUrl) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var projectService = sp.GetRequiredService<IProjectService>();

    var manifest = await projectService.LoadProjectAsync(project.FullName);
    if (manifest.Environments is null || !manifest.Environments.ContainsKey(envName))
    {
        logger.LogError("Environment '{Env}' not found in mimir.json.", envName);
        return;
    }

    // Determine build directory: use configured buildPath or default build/<env>
    var envConfig = manifest.Environments[envName];
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

}, packProjectArg, packOutputArg, packEnvOption, packBaseUrlOption);

rootCommand.AddCommand(importCommand);
rootCommand.AddCommand(buildCommand);
rootCommand.AddCommand(queryCommand);
rootCommand.AddCommand(editCommand);
rootCommand.AddCommand(shellCommand);
rootCommand.AddCommand(validateCommand);
rootCommand.AddCommand(initTemplateCommand);
rootCommand.AddCommand(editTemplateCommand);
rootCommand.AddCommand(packCommand);
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

