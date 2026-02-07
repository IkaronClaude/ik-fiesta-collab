using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mimir.Core;
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
            var data = await provider.ReadAsync(file.FullName);
            var relativePath = $"data/{provider.FormatId}/{data.Schema.TableName}.jsonl";

            var entry = new TableEntry
            {
                DataPath = relativePath,
                Schema = data.Schema
            };

            await projectService.WriteTableDataAsync(project.FullName, entry, data);
            manifest.Tables[data.Schema.TableName] = entry;

            logger.LogInformation("Imported {TableName} ({RowCount} rows)",
                data.Schema.TableName, data.Rows.Count);
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

    foreach (var (name, entry) in manifest.Tables)
    {
        if (!providers.TryGetValue(entry.Schema.SourceFormat, out var provider))
        {
            logger.LogWarning("No provider for format {Format}, skipping {Table}",
                entry.Schema.SourceFormat, name);
            continue;
        }

        try
        {
            var data = await projectService.ReadTableDataAsync(project.FullName, entry);
            var outputPath = Path.Combine(output.FullName, $"{name}.{provider.SupportedExtensions[0].TrimStart('.')}");
            await provider.WriteAsync(outputPath, data);

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

    foreach (var (name, entry) in manifest.Tables)
    {
        var data = await projectService.ReadTableDataAsync(project.FullName, entry);
        engine.LoadTable(data);
    }

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

rootCommand.AddCommand(importCommand);
rootCommand.AddCommand(buildCommand);
rootCommand.AddCommand(queryCommand);
rootCommand.AddCommand(dumpCommand);
rootCommand.AddCommand(analyzeCommand);

return await rootCommand.InvokeAsync(args);
