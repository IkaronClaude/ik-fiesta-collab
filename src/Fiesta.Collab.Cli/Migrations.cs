using System.Text.Json;
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

    /// <summary>Where a migration's -- @report tables go: build/reports under the project.</summary>
    public static string ReportDir(string projectPath) => Path.Combine(projectPath, "build", "reports");

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
            engine.LoadTable(new TableEntry { Schema = schema, Rows = tableFile.Data, RowEnvironments = tableFile.RowEnvironments });
        }

        var affected = engine.Execute(sql);
        if (affected == 0) return 0;

        foreach (var (name, entryPath) in manifest.Tables)
        {
            var schema = schemas[name];
            var extracted = engine.ExtractTable(schema);

            // the row environments ride in the engine's _envs column, so they follow their rows
            var envs = extracted.RowEnvironments;

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


    /// <summary>Whether two versions of a table hold the same rows, in the same order, with the same values.</summary>
    private static bool SameData(IReadOnlyList<Dictionary<string, object?>> a,
                                 IReadOnlyList<Dictionary<string, object?>> b,
                                 TableSchema schema)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            foreach (var c in schema.Columns)
                if (!SameValue(a[i].GetValueOrDefault(c.Name), b[i].GetValueOrDefault(c.Name)))
                    return false;
        return true;
    }

    private static bool SameEnvs(IReadOnlyList<List<string>?>? a, IReadOnlyList<List<string>?>? b)
    {
        static string Key(IReadOnlyList<List<string>?>? l)
            => l is null || l.All(e => e is null) ? "" : string.Join("|", l.Select(e => e is null ? "" : string.Join(",", e)));
        return Key(a) == Key(b);
    }

    private static string FirstDifference(IReadOnlyList<Dictionary<string, object?>> a,
                                          IReadOnlyList<Dictionary<string, object?>> b, TableSchema schema)
    {
        if (a.Count != b.Count) return $"rows {a.Count} -> {b.Count}";
        for (var i = 0; i < a.Count; i++)
            foreach (var c in schema.Columns)
            {
                object? x = a[i].GetValueOrDefault(c.Name), y = b[i].GetValueOrDefault(c.Name);
                if (!SameValue(x, y))
                    return $"row {i} {c.Name} ({c.Type}): {x} [{x?.GetType().Name}] -> {y} [{y?.GetType().Name}]";
            }
        return "none";
    }

    /// <summary>
    /// Value equality across the type changes a SQLite round trip makes.
    ///
    /// A column read from JSON as an int comes back from the engine as a long, and JSON numbers arrive as
    /// JsonElement, so object.Equals says "different" for values that are identical. That made SameData
    /// false for essentially every table, which dropped the row-environment annotations of all 1,417 of
    /// them on every migrate - AbState lost the annotations that keep 318 overlay-only rows out of the
    /// server build, and came out at 1,095 rows against the server's 777.
    /// </summary>
    private static bool SameValue(object? x, object? y)
    {
        if (x is null || y is null) return x is null && y is null;
        if (x is JsonElement jx) x = Unbox(jx);
        if (y is JsonElement jy) y = Unbox(jy);
        if (x is null || y is null) return x is null && y is null;

        if (IsIntegral(x) && IsIntegral(y))
            return Convert.ToInt64(x) == Convert.ToInt64(y);
        // A Float column comes out of the engine as a Single while JSON holds the double it was written as: 0.6 vs
        // 0.6f (0.60000002384) are the same stored value, so compare at float precision. Widening to double made
        // every table with an inexact float "changed" (ItemViewInfo, MapViewInfo) and cost it its row environments.
        if ((x is float || y is float) && IsNumeric(x) && IsNumeric(y))
            return (float)Convert.ToDouble(x) == (float)Convert.ToDouble(y);
        if (IsNumeric(x) && IsNumeric(y))
            return Convert.ToDouble(x).Equals(Convert.ToDouble(y));
        return string.Equals(x.ToString(), y.ToString(), StringComparison.Ordinal);
    }

    private static object? Unbox(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.Number when je.TryGetInt64(out var l) => l,
        JsonValueKind.Number => je.GetDouble(),
        JsonValueKind.String => je.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => je.ToString()
    };

    private static bool IsIntegral(object v)
        => v is byte or sbyte or short or ushort or int or uint or long or ulong;

    private static bool IsNumeric(object v) => IsIntegral(v) || v is float or double or decimal;

    /// <summary>The loaded project: every manifest table in one engine, with what write-back needs.</summary>
    private sealed class Loaded(FiestaProject manifest, ISqlEngine engine) : IDisposable
    {
        public FiestaProject Manifest { get; } = manifest;
        public ISqlEngine Engine { get; } = engine;
        public Dictionary<string, TableHeader> Headers { get; } = new();
        public Dictionary<string, TableSchema> Schemas { get; } = new();
        public Dictionary<string, IReadOnlyList<List<string>?>?> RowEnvs { get; } = new();
        public Dictionary<string, IReadOnlyList<Dictionary<string, object?>>> Before { get; } = new();
        public void Dispose() => Engine.Dispose();

        /// <summary>The table as it now stands in the engine, with the row environments its `_envs` column holds. Null
        /// when neither its data nor its environments changed, unless <paramref name="evenIfSame"/>.</summary>
        public TableFile? Changed(string name, bool evenIfSame = false)
        {
            var schema = Schemas[name];
            var extracted = Engine.ExtractTable(schema);
            var same = SameData(Before[name], extracted.Rows, schema) && SameEnvs(RowEnvs.GetValueOrDefault(name), extracted.RowEnvironments);
            if (!same && Environment.GetEnvironmentVariable("COLLAB_DEBUG_SAMEDATA") == "1")
                Console.Error.WriteLine($"SameData {name}: {FirstDifference(Before[name], extracted.Rows, schema)}");
            if (same && !evenIfSame) return null;
            return new TableFile
            {
                Header = Headers[name],
                Columns = schema.Columns,
                Data = extracted.Rows,
                RowEnvironments = extracted.RowEnvironments
            };
        }
    }

    private static async Task<Loaded> LoadAsync(string projectPath, IServiceProvider services)
    {
        var projectService = services.GetRequiredService<IProjectService>();
        var manifest = await projectService.LoadProjectAsync(projectPath);
        var p = new Loaded(manifest, services.GetRequiredService<ISqlEngine>());
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
            p.Headers[name] = tableFile.Header;
            p.Schemas[name] = schema;
            p.RowEnvs[name] = tableFile.RowEnvironments;
            p.Before[name] = tableFile.Data;
            p.Engine.LoadTable(new TableEntry { Schema = schema, Rows = tableFile.Data, RowEnvironments = tableFile.RowEnvironments });
        }
        return p;
    }

    private static int Apply(ISqlEngine engine, IEnumerable<string> files, string reportDir, ILogger logger, string again)
    {
        var total = 0;
        foreach (var f in files)
        {
            var sql = File.ReadAllText(f);
            if (string.IsNullOrWhiteSpace(sql)) continue;
            var name = Path.GetFileName(f);
            try
            {
                var affected = MigrationScript.Run(engine, sql, name, reportDir);
                total += affected;
                logger.LogInformation("  {File}: {Affected} row(s) affected", name, affected);
            }
            catch (Exception ex)
            {
                logger.LogError("  {File}: FAILED - {Message}", name, ex.Message);
                throw new InvalidOperationException(
                    $"migration {name} failed; nothing was saved. Fix the migration (or the data it " +
                    $"expects) and run {again} again.", ex);
            }
        }
        return total;
    }

    /// <summary>The migration files of one variant layer, in order. A layer may hold only .sql steps: a generated
    /// step (NNNN-name.py and the like) has to become SQL before collab can build the variant.</summary>
    public static List<string> LayerFiles(string projectPath, string layer)
    {
        var dir = Path.Combine(projectPath, layer);
        if (!Directory.Exists(dir)) throw new InvalidOperationException($"variant layer '{layer}' not found at {dir}");
        var generated = Directory.GetFiles(dir).Select(f => Path.GetFileName(f))
            .Where(n => n.Length > 0 && char.IsDigit(n[0]) && !n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (generated.Count > 0)
            throw new InvalidOperationException(
                $"variant layer '{layer}' holds generated steps collab cannot run: {string.Join(", ", generated)}. " +
                "Convert them to SQL (functions, @param / @assert / @report) or build this variant with the old tooling.");
        return Directory.GetFiles(dir, "*.sql").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The files a variant's layers ship for one environment: &lt;layer&gt;/overrides/&lt;env&gt;/** (flag files a zone plugin
    /// reads, whole files no table describes), copied onto build/&lt;variant&gt;/&lt;env&gt; after the environment's own
    /// overrides. Later layers win a path both ship. Relative is the path under the environment's build folder.
    /// </summary>
    public static List<(string Source, string Relative)> LayerOverrides(string projectPath, IEnumerable<string> layers, string env)
    {
        var byRel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers)
        {
            var root = Path.Combine(projectPath, layer, "overrides", env);
            if (!Directory.Exists(root)) continue;
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                byRel[Path.GetRelativePath(root, f)] = f;
        }
        return byRel.Select(kv => (kv.Value, kv.Key)).ToList();
    }

    /// <summary>Where a variant's builds and reports go: build/&lt;variant&gt;.</summary>
    public static string VariantDir(string projectPath, string variant) => Path.Combine(projectPath, "build", variant);

    /// <summary>
    /// A build variant: data/ (the base migrations are already in it) plus the variant's layers, in order, in ONE
    /// session - applied in memory, data/ untouched. Returns the tables whose data the layers changed; every other
    /// table is data/ as it stands. Reports go to build/&lt;variant&gt;/reports.
    /// </summary>
    public static async Task<Dictionary<string, TableFile>> ApplyVariantAsync(string projectPath, string variant,
        IServiceProvider services, ILogger logger)
    {
        var manifest = await services.GetRequiredService<IProjectService>().LoadProjectAsync(projectPath);
        if (manifest.Variants is null || !manifest.Variants.TryGetValue(variant, out var layers))
            throw new InvalidOperationException(
                $"no variant '{variant}' in fiesta.json (known: {string.Join(", ", manifest.Variants?.Keys.AsEnumerable() ?? [])})");
        var files = layers.SelectMany(l => LayerFiles(projectPath, l)).ToList();

        using var p = await LoadAsync(projectPath, services);
        logger.LogInformation("Variant {Variant}: {Count} migration(s) from {Layers}", variant, files.Count, string.Join(" + ", layers));
        var total = Apply(p.Engine, files, Path.Combine(VariantDir(projectPath, variant), "reports"), logger,
            $"`fiesta build --variant {variant}`");
        var changed = new Dictionary<string, TableFile>();
        if (total == 0) return changed;
        foreach (var name in p.Manifest.Tables.Keys)
            if (p.Changed(name) is { } t) changed[name] = t;
        logger.LogInformation("Variant {Variant}: {Total} row(s) affected, {Tables} table(s) changed", variant, total, changed.Count);
        return changed;
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
        using var p = await LoadAsync(projectPath, services);
        var manifest = p.Manifest;

        logger.LogInformation("Applying {Count} migration(s) to {Tables} tables", files.Count, manifest.Tables.Count);
        var total = Apply(p.Engine, files, ReportDir(projectPath), logger, "`fiesta migrate`");

        if (total == 0)
        {
            logger.LogInformation("No rows changed; nothing to save.");
            return;
        }

        var saved = 0;
        foreach (var (name, entryPath) in manifest.Tables)
        {
            // Row environments ride in the engine's `_envs` column, so a row keeps its own through inserts, deletes
            // and reorders; a row a migration inserts is shared unless it names _envs. (They used to be a positional
            // list, dropped whenever a table's data changed - SetEffect then built 1,415 rows where the server has
            // 1,043, and every table with an inexact float lost its annotations on every migrate.)
            await projectService.WriteTableFileAsync(projectPath, entryPath, p.Changed(name, evenIfSame: true)!);
            saved++;
        }
        logger.LogInformation("Migrations applied: {Total} row(s) affected, {Saved} tables saved", total, saved);
    }
}
