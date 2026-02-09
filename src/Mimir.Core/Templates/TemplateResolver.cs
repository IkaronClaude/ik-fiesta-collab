using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mimir.Core.Templates;

public sealed class TemplateResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public const string TemplateFileName = "mimir.template.json";

    public static async Task<ProjectTemplate> LoadAsync(string projectDir, CancellationToken ct = default)
    {
        var path = FindTemplateFile(projectDir);
        if (path is null)
            return new ProjectTemplate();

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ProjectTemplate>(stream, JsonOptions, ct)
               ?? new ProjectTemplate();
    }

    public static async Task SaveAsync(string projectDir, ProjectTemplate template, CancellationToken ct = default)
    {
        var path = Path.Combine(projectDir, TemplateFileName);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, template, JsonOptions, ct);
    }

    private static string? FindTemplateFile(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, TemplateFileName);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
