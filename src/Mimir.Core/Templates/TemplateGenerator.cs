using Mimir.Core.Models;

namespace Mimir.Core.Templates;

/// <summary>
/// Auto-generates a ProjectTemplate from scanning environment tables.
/// Used by the init-template command.
/// </summary>
public static class TemplateGenerator
{
    private static readonly string[] PkCandidates = ["ID", "Index", "Idx"];
    private static readonly string[] UkCandidates = ["InxName", "IndexName"];

    public static ProjectTemplate Generate(
        Dictionary<(string table, string env), TableFile> envTables,
        IReadOnlyList<string> envOrder,
        IReadOnlyList<(string env, string path)>? passthroughFiles = null)
    {
        var actions = new List<TemplateAction>();

        // Group tables by name
        var tablesByName = envTables
            .GroupBy(kv => kv.Key.table)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.ToDictionary(kv => kv.Key.env, kv => kv.Value));

        foreach (var (tableName, envVersions) in tablesByName)
        {
            var envsPresent = envOrder.Where(e => envVersions.ContainsKey(e)).ToList();
            if (envsPresent.Count == 0) continue;

            var firstEnv = envsPresent[0];
            var firstTable = envVersions[firstEnv];

            // Copy from first environment
            actions.Add(new TemplateAction
            {
                Action = "copy",
                From = new TableRef { Table = tableName, Env = firstEnv },
                To = tableName
            });

            // Merge from additional environments
            foreach (var env in envsPresent.Skip(1))
            {
                var otherTable = envVersions[env];
                var joinCol = FindJoinColumn(firstTable, otherTable);

                if (joinCol != null)
                {
                    // Compatible schemas — standard merge
                    actions.Add(new TemplateAction
                    {
                        Action = "merge",
                        From = new TableRef { Table = tableName, Env = env },
                        Into = tableName,
                        On = new JoinClause { Source = joinCol, Target = joinCol },
                        ColumnStrategy = "auto",
                        ConflictStrategy = "split"
                    });
                }
                else
                {
                    // Incompatible schemas (zero shared columns): same filename, different tables
                    // in different environments. Import as a separate env-specific entry with a
                    // unique internal name but outputName set to the original filename so the build
                    // still produces {tableName}.shn at the correct path.
                    var internalName = $"{tableName}__{env}";
                    actions.Add(new TemplateAction
                    {
                        Action = "copy",
                        From = new TableRef { Table = tableName, Env = env },
                        To = internalName,
                        OutputName = tableName
                    });

                    // Schema declarations for this env's separate table
                    var pkColOther = FindPrimaryKey(otherTable);
                    if (pkColOther != null)
                        actions.Add(new TemplateAction { Action = "setPrimaryKey", Table = internalName, Column = pkColOther });

                    var ukColOther = FindUniqueKey(otherTable);
                    if (ukColOther != null)
                        actions.Add(new TemplateAction { Action = "setUniqueKey", Table = internalName, Column = ukColOther });
                }
            }

            // Schema declarations based on first env's columns
            var pkCol = FindPrimaryKey(firstTable);
            if (pkCol != null)
            {
                actions.Add(new TemplateAction
                {
                    Action = "setPrimaryKey",
                    Table = tableName,
                    Column = pkCol
                });
            }

            var ukCol = FindUniqueKey(firstTable);
            if (ukCol != null)
            {
                actions.Add(new TemplateAction
                {
                    Action = "setUniqueKey",
                    Table = tableName,
                    Column = ukCol
                });
            }
        }

        // Add copyFile actions for passthrough files (not handled by any table provider)
        if (passthroughFiles != null)
        {
            foreach (var (env, path) in passthroughFiles)
            {
                actions.Add(new TemplateAction
                {
                    Action = "copyFile",
                    Env = env,
                    Path = path
                });
            }
        }

        return new ProjectTemplate { Actions = actions };
    }

    private static string? FindJoinColumn(TableFile a, TableFile b)
    {
        var aColNames = a.Columns.Select(c => c.Name).ToHashSet();
        var bColNames = b.Columns.Select(c => c.Name).ToHashSet();
        var shared = aColNames.Intersect(bColNames).ToHashSet();

        // Prefer string unique-key columns first (InxName, IndexName) — these are stable
        // across environments. Numeric IDs like "ID" or "Index" are often row-position-based
        // and can differ between server and client.
        foreach (var candidate in UkCandidates)
        {
            if (shared.Contains(candidate))
                return candidate;
        }

        // Fall back to known numeric PK column names
        foreach (var candidate in PkCandidates)
        {
            if (shared.Contains(candidate))
                return candidate;
        }

        // Fall back to first shared UInt16/UInt32 column
        foreach (var col in a.Columns)
        {
            if (shared.Contains(col.Name) && col.Type is ColumnType.UInt16 or ColumnType.UInt32)
                return col.Name;
        }

        // Fall back to first shared column
        return shared.FirstOrDefault();
    }

    private static string? FindPrimaryKey(TableFile table)
    {
        foreach (var candidate in PkCandidates)
        {
            var col = table.Columns.FirstOrDefault(c => c.Name == candidate);
            if (col != null && col.Type is ColumnType.UInt16 or ColumnType.UInt32 or ColumnType.Int32)
                return col.Name;
        }
        return null;
    }

    private static string? FindUniqueKey(TableFile table)
    {
        foreach (var candidate in UkCandidates)
        {
            var col = table.Columns.FirstOrDefault(c => c.Name == candidate);
            if (col != null && col.Type == ColumnType.String)
                return col.Name;
        }
        return null;
    }
}
