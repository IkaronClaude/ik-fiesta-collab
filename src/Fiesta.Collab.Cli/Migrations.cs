using Fiesta.Collab.Core.Models;
using Fiesta.Collab.Core.Project;
using Fiesta.Collab.Sql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fiesta.Collab.Cli;

/// <summary>
/// Replayable SQL edits.
///
/// The project model is import-once, then edit the JSON and commit it. But a re-import is sometimes
/// unavoidable - the upstream data moved, or a template rule changed - and on its own it rebuilds data/
/// from the sources and silently discards every edit made since. That makes `import` a command people
/// avoid rather than use, and it pushes edits back into whatever produced the import instead.
///
/// Recording each edit as an ordered .sql file and replaying them after every import makes importing
/// repeatable rather than destructive, and puts the edits in git next to the data they produce.
///
///     migrations/0001-&lt;slug&gt;.sql        applied in filename order
///     fiesta edit "&lt;sql&gt;" --record slug  runs it AND records it
///     fiesta migrate                      re-applies all of them
///     fiesta import                       applies them once the import finishes
/// </summary>
public static class Migrations
{
    public static string Dir(string projectPath) => Path.Combine(projectPath, "migrations");

    /// <summary>The migration files in the order they must run. Filename order IS the contract, which is
    /// why the numbering is zero-padded - "10" must not sort before "2".</summary>
    public static List<string> Files(string projectPath)
    {
        var dir = Dir(projectPath);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.sql")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
    }

    public static string NextPath(string projectPath, string slug)
    {
        Directory.CreateDirectory(Dir(projectPath));
        var n = Files(projectPath).Count + 1;
        var safe = new string(slug.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray()).Trim('-');
        if (safe.Length == 0) safe = "edit";
        return Path.Combine(Dir(projectPath), $"{n:D4}-{safe}.sql");
    }

    /// <summary>Load every table, run the SQL, and write back the ones that changed. Returns rows affected.</summary>
    public static async Task<int> ApplySqlAsync(string projectPath, IProjectService projectService,
                                                ISqlEngine engine, FiestaProject manifest, string sql)
    {
        var headers = new Dictionary<string, TableHeader>();
        var schemas = new Dictionary<string, TableSchema>();
        var rowEnvs = new Dictionary<string, IReadOnlyList<List<string>?>?>();

        foreach (var (name, entryPath) in manifest.Tables)
        {
            var tableFile = await projectService.ReadTableFileAsync(projectPath, entryPath);
            var schema = new TableSchema
            {
                TableName = name,   // the manifest key: a header TableName can collide across envs
                SourceFormat = tableFile.Header.SourceFormat,
                Columns = tableFile.Columns,
                Metadata = tableFile.Header.Metadata
            };
            headers[name] = tableFile.Header;
            schemas[name] = schema;
            rowEnvs[name] = tableFile.RowEnvironments;
            engine.LoadTable(new TableEntry { Schema = schema, Rows = tableFile.Data });
        }

        var affected = engine.Execute(sql);
        if (affected == 0) return 0;

        foreach (var (name, entryPath) in manifest.Tables)
        {
            var schema = schemas[name];
            var extracted = engine.ExtractTable(schema);
            await projectService.WriteTableFileAsync(projectPath, entryPath, new TableFile
            {
                Header = headers[name],
                Columns = schema.Columns,
                Data = extracted.Rows,
                RowEnvironments = rowEnvs.GetValueOrDefault(name)
            });
        }
        return affected;
    }

    /// <summary>Apply every migration, in order. A failing one stops the run: later migrations are written
    /// against the state the earlier ones produced, so continuing past a failure corrupts that state.</summary>
    public static async Task RunAsync(string projectPath, IServiceProvider services, ILogger logger)
    {
        var files = Files(projectPath);
        if (files.Count == 0) return;

        var projectService = services.GetRequiredService<IProjectService>();
        var manifest = await projectService.LoadProjectAsync(projectPath);
        logger.LogInformation("Applying {Count} migration(s)", files.Count);

        foreach (var f in files)
        {
            var sql = File.ReadAllText(f);
            if (string.IsNullOrWhiteSpace(sql)) continue;
            var name = Path.GetFileName(f);
            try
            {
                using var engine = services.GetRequiredService<ISqlEngine>();
                var affected = await ApplySqlAsync(projectPath, projectService, engine, manifest, sql);
                logger.LogInformation("  {File}: {Affected} row(s) affected", name, affected);
            }
            catch (Exception ex)
            {
                logger.LogError("  {File}: FAILED - {Message}", name, ex.Message);
                throw new InvalidOperationException(
                    $"migration {name} failed; the project is left part-migrated. Fix the migration (or the " +
                    $"data it expects) and run `fiesta migrate` again.", ex);
            }
        }
    }
}
