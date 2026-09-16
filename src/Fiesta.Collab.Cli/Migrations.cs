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

            // Row environments are positional. A migration that inserts, deletes or reorders rows makes the
            // old list meaningless - writing it back would hand row 500's visibility to a different row.
            // There is no way to re-derive it from SQL output, so when the count changes the annotations are
            // dropped and every row becomes visible to every environment, which is what an unannotated
            // table means anyway. Migrations that must keep rows environment-specific have to be written
            // against a table whose row count they do not change.
            var envs = rowEnvs.GetValueOrDefault(name);
            if (envs != null && envs.Count != extracted.Rows.Count) envs = null;

            await projectService.WriteTableFileAsync(projectPath, entryPath, new TableFile
            {
                Header = headers[name],
                Columns = schema.Columns,
                Data = extracted.Rows,
                RowEnvironments = envs
            });
        }
        return affected;
    }

    /// <summary>Apply every migration, in order, in ONE session.
    ///
    /// Migrations are written against the state the earlier ones produced, so they share a set of loaded
    /// tables and are saved once at the end. Loading every table per migration would also be N x T work -
    /// 31 migrations over 1,417 tables is 44,000 table loads to change a few thousand rows.
    ///
    /// A failing migration aborts the run and saves nothing, so a half-applied set never reaches disk.</summary>
    public static async Task RunAsync(string projectPath, IServiceProvider services, ILogger logger)
    {
        var files = Files(projectPath);
        if (files.Count == 0) return;

        var projectService = services.GetRequiredService<IProjectService>();
        var manifest = await projectService.LoadProjectAsync(projectPath);
        using var engine = services.GetRequiredService<ISqlEngine>();

        var headers = new Dictionary<string, TableHeader>();
        var schemas = new Dictionary<string, TableSchema>();
        var rowEnvs = new Dictionary<string, IReadOnlyList<List<string>?>?>();

        foreach (var (name, entryPath) in manifest.Tables)
        {
            var tableFile = await projectService.ReadTableFileAsync(projectPath, entryPath);
            var schema = new TableSchema
            {
                TableName = name,
                SourceFormat = tableFile.Header.SourceFormat,
                Columns = tableFile.Columns,
                Metadata = tableFile.Header.Metadata
            };
            headers[name] = tableFile.Header;
            schemas[name] = schema;
            rowEnvs[name] = tableFile.RowEnvironments;
            engine.LoadTable(new TableEntry { Schema = schema, Rows = tableFile.Data });
        }

        logger.LogInformation("Applying {Count} migration(s) to {Tables} tables", files.Count, manifest.Tables.Count);
        var total = 0;
        foreach (var f in files)
        {
            var sql = File.ReadAllText(f);
            if (string.IsNullOrWhiteSpace(sql)) continue;
            var name = Path.GetFileName(f);
            try
            {
                var affected = engine.Execute(sql);
                total += affected;
                logger.LogInformation("  {File}: {Affected} row(s) affected", name, affected);
            }
            catch (Exception ex)
            {
                logger.LogError("  {File}: FAILED - {Message}", name, ex.Message);
                throw new InvalidOperationException(
                    $"migration {name} failed; nothing was saved. Fix the migration (or the data it " +
                    $"expects) and run `fiesta migrate` again.", ex);
            }
        }

        if (total == 0)
        {
            logger.LogInformation("No rows changed; nothing to save.");
            return;
        }

        var saved = 0;
        foreach (var (name, entryPath) in manifest.Tables)
        {
            var schema = schemas[name];
            var extracted = engine.ExtractTable(schema);

            // Row environments are positional. A migration that inserts, deletes or reorders rows makes the
            // old list meaningless - writing it back would hand one row's visibility to another. It cannot
            // be re-derived from SQL output, so when the count changes the annotations are dropped and every
            // row becomes visible to every environment, which is what an unannotated table means anyway.
            var envs = rowEnvs.GetValueOrDefault(name);
            if (envs != null && envs.Count != extracted.Rows.Count) envs = null;

            await projectService.WriteTableFileAsync(projectPath, entryPath, new TableFile
            {
                Header = headers[name],
                Columns = schema.Columns,
                Data = extracted.Rows,
                RowEnvironments = envs
            });
            saved++;
        }
        logger.LogInformation("Migrations applied: {Total} row(s) affected, {Saved} tables saved", total, saved);
    }
}
