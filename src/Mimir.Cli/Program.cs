using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mimir.Core;
using Mimir.Core.Constraints;
using Mimir.Core.Models;
using Mimir.Core.Project;
using Mimir.Core.Providers;
using Mimir.Shn;
using Mimir.RawTables;
using Mimir.Sql;

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
services.AddMimirCore();
services.AddMimirShn();
services.AddMimirRawTables();
services.AddMimirSql();

var sp = services.BuildServiceProvider();

var rootCommand = new RootCommand("Mimir - Fiesta Online server data toolkit");

// --- import command ---
var importCommand = new Command("import", "Import server data files into a Mimir project");
var sourceArg = new Argument<DirectoryInfo>("source", "Path to server 9Data/Shine directory");
var projectArg = new Argument<DirectoryInfo>("project", "Path to output Mimir project directory");
importCommand.AddArgument(sourceArg);
importCommand.AddArgument(projectArg);

importCommand.SetHandler(async (DirectoryInfo source, DirectoryInfo project) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var projectService = sp.GetRequiredService<IProjectService>();
    var providers = sp.GetServices<IDataProvider>().ToList();

    logger.LogInformation("Importing from {Source} into {Project}", source.FullName, project.FullName);

    var manifest = new MimirProject();
    var files = source.EnumerateFiles("*", SearchOption.AllDirectories);

    foreach (var file in files)
    {
        var provider = providers.FirstOrDefault(p =>
            p.SupportedExtensions.Contains(file.Extension.ToLowerInvariant()));

        if (provider is null)
        {
            logger.LogDebug("Skipping {File} (no provider for {Extension})", file.Name, file.Extension);
            continue;
        }

        try
        {
            var entries = await provider.ReadAsync(file.FullName);
            var sourceRelDir = Path.GetDirectoryName(Path.GetRelativePath(source.FullName, file.FullName)) ?? "";
            foreach (var entry in entries)
            {
                var relativePath = Path.Combine("data", entry.Schema.SourceFormat, sourceRelDir,
                    $"{entry.Schema.TableName}.json").Replace('\\', '/');

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

                await projectService.WriteTableFileAsync(project.FullName, relativePath, tableFile);
                manifest.Tables[entry.Schema.TableName] = relativePath;

                logger.LogInformation("Imported {TableName} ({RowCount} rows)",
                    entry.Schema.TableName, entry.Rows.Count);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to import {File}", file.Name);
        }
    }

    await projectService.SaveProjectAsync(project.FullName, manifest);
    logger.LogInformation("Import complete: {Count} tables", manifest.Tables.Count);

}, sourceArg, projectArg);

// --- build command ---
var buildCommand = new Command("build", "Build server data files from a Mimir project");
var buildProjectArg = new Argument<DirectoryInfo>("project", "Path to Mimir project directory");
var buildOutputArg = new Argument<DirectoryInfo>("output", "Path to output server data directory");
buildCommand.AddArgument(buildProjectArg);
buildCommand.AddArgument(buildOutputArg);

buildCommand.SetHandler(async (DirectoryInfo project, DirectoryInfo output) =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var projectService = sp.GetRequiredService<IProjectService>();
    var providers = sp.GetServices<IDataProvider>().ToDictionary(p => p.FormatId);

    logger.LogInformation("Building from {Project} to {Output}", project.FullName, output.FullName);

    var manifest = await projectService.LoadProjectAsync(project.FullName);
    Directory.CreateDirectory(output.FullName);

    foreach (var (name, entryPath) in manifest.Tables)
    {
        var tableFile = await projectService.ReadTableFileAsync(project.FullName, entryPath);

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

        if (!providers.TryGetValue(entry.Schema.SourceFormat, out var provider))
        {
            logger.LogWarning("No provider for format {Format}, skipping {Table}",
                entry.Schema.SourceFormat, name);
            continue;
        }

        try
        {
            // Reconstruct original directory structure from manifest path
            // entryPath looks like: data/shn/Shine/ItemInfo.json → Shine/ItemInfo.shn
            // Strip "data/{format}/" prefix to get the relative source path
            var ext = provider.SupportedExtensions[0].TrimStart('.');
            var parts = entryPath.Replace('\\', '/').Split('/');
            // Skip "data" and format prefix (e.g. "shn", "rawtable")
            var relParts = parts.Length > 2 ? parts[2..] : parts;
            var relDir = string.Join(Path.DirectorySeparatorChar.ToString(),
                relParts.Take(relParts.Length - 1));
            var outputDir = Path.Combine(output.FullName, relDir);
            Directory.CreateDirectory(outputDir);

            var outputPath = Path.Combine(outputDir, $"{name}.{ext}");
            await provider.WriteAsync(outputPath, [entry]);

            logger.LogInformation("Built {TableName}", name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to build {Table}", name);
        }
    }

    logger.LogInformation("Build complete");

}, buildProjectArg, buildOutputArg);

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
    var constraints = await DefinitionResolver.LoadAsync(project.FullName);

    if (constraints.Constraints.Count == 0)
    {
        logger.LogWarning("No constraints file found or no rules defined. Create {File} to add constraints.",
            DefinitionResolver.DefinitionsFileName);
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
    var definitions = await DefinitionResolver.LoadAsync(project.FullName);

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

rootCommand.AddCommand(importCommand);
rootCommand.AddCommand(buildCommand);
rootCommand.AddCommand(queryCommand);
rootCommand.AddCommand(editCommand);
rootCommand.AddCommand(shellCommand);
rootCommand.AddCommand(validateCommand);
rootCommand.AddCommand(dumpCommand);
rootCommand.AddCommand(analyzeCommand);

return await rootCommand.InvokeAsync(args);

// --- shared helpers ---

async Task LoadTablesWithConstraints(
    ISqlEngine engine, IProjectService projectService, MimirProject manifest,
    string projectDir, ILogger logger)
{
    // Load and resolve constraints (if any)
    var constraintFile = await DefinitionResolver.LoadAsync(projectDir);
    var resolved = DefinitionResolver.Resolve(constraintFile, manifest);

    if (resolved.Count > 0 || constraintFile.Tables?.Count > 0)
    {
        engine.SetConstraints(resolved, constraintFile.Tables);
        logger.LogInformation("Registered {Count} FK constraints from {RuleCount} rules, {KeyCount} table keys",
            resolved.Count, constraintFile.Constraints.Count, constraintFile.Tables?.Count ?? 0);
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

    if (resolved.Count > 0 || constraintFile.Tables?.Count > 0)
        engine.EnableForeignKeys();

    logger.LogInformation("Loaded {Count} tables into SQLite", manifest.Tables.Count);
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
