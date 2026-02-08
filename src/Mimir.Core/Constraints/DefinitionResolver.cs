using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mimir.Core.Project;

namespace Mimir.Core.Constraints;

public sealed class DefinitionResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public const string DefinitionsFileName = "mimir.definitions.json";

    public static async Task<ProjectDefinitions> LoadAsync(string projectDir, CancellationToken ct = default)
    {
        var path = FindDefinitionsFile(projectDir);
        if (path is null)
            return new ProjectDefinitions();

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ProjectDefinitions>(stream, JsonOptions, ct)
               ?? new ProjectDefinitions();
    }

    private static string? FindDefinitionsFile(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, DefinitionsFileName);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public static async Task SaveAsync(string projectDir, ProjectDefinitions file, CancellationToken ct = default)
    {
        var path = Path.Combine(projectDir, DefinitionsFileName);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, file, JsonOptions, ct);
    }

    /// <summary>
    /// Resolves all constraint rules against the manifest, producing concrete
    /// (sourceTable, sourceColumn, targetTable, targetColumn, emptyValues) tuples.
    /// </summary>
    public static List<ResolvedConstraint> Resolve(ProjectDefinitions definitions, MimirProject manifest)
    {
        var resolved = new List<ResolvedConstraint>();
        var tables = definitions.Tables ?? [];

        foreach (var rule in definitions.Constraints)
        {
            if (rule.ForeignKey == null) continue;

            foreach (var (tableName, tablePath) in manifest.Tables)
            {
                if (rule.Match.Path != null && !GlobMatch(tablePath, rule.Match.Path))
                    continue;

                if (rule.Match.Table != null && !GlobMatch(tableName, rule.Match.Table))
                    continue;

                string targetTable = ResolveTemplate(rule.ForeignKey.Table, tableName);

                if (!manifest.Tables.ContainsKey(targetTable))
                    continue;

                string? targetColumn = ResolveTargetColumn(rule.ForeignKey.Column, targetTable, tables);
                if (targetColumn == null)
                    continue;

                resolved.Add(new ResolvedConstraint
                {
                    Rule = rule,
                    SourceTable = tableName,
                    ColumnPattern = rule.Match.Column,
                    TargetTable = targetTable,
                    TargetColumn = targetColumn,
                    EmptyValues = rule.EmptyValues ?? []
                });
            }
        }

        return resolved;
    }

    private static string? ResolveTargetColumn(
        string? column, string targetTable, Dictionary<string, TableKeyInfo> tables)
    {
        if (column != null && column != "@id")
            return column;

        if (!tables.TryGetValue(targetTable, out var keyInfo))
            return column;

        if (column == "@id")
            return keyInfo.IdColumn;

        return keyInfo.KeyColumn;
    }

    private static string ResolveTemplate(string template, string sourceTableName)
    {
        if (!template.Contains('{')) return template;

        string filePrefix = sourceTableName;
        int lastUnderscore = sourceTableName.LastIndexOf('_');
        if (lastUnderscore > 0)
            filePrefix = sourceTableName[..lastUnderscore];

        return template.Replace("{file}", filePrefix);
    }

    public static bool GlobMatch(string input, string pattern)
    {
        string regex = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]")
            + "$";

        return Regex.IsMatch(input, regex, RegexOptions.IgnoreCase);
    }
}

public sealed class ResolvedConstraint
{
    public required ConstraintRule Rule { get; init; }
    public required string SourceTable { get; init; }
    public required string ColumnPattern { get; init; }
    public required string TargetTable { get; init; }
    public required string TargetColumn { get; init; }
    public required List<string> EmptyValues { get; init; }
}
