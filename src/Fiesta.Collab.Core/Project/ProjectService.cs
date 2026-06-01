using System.Text.Json;
using System.Text.Json.Serialization;
using Fiesta.Collab.Core.Models;

namespace Fiesta.Collab.Core.Project;

public sealed class ProjectService : IProjectService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public const string ManifestFileName = "fiesta.json";

    public async Task<FiestaProject> LoadProjectAsync(string projectDir, CancellationToken ct = default)
    {
        var path = Path.Combine(projectDir, ManifestFileName);
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<FiestaProject>(stream, JsonOptions, ct)
               ?? throw new InvalidDataException($"Failed to deserialize {path}");
    }

    public async Task SaveProjectAsync(string projectDir, FiestaProject project, CancellationToken ct = default)
    {
        Directory.CreateDirectory(projectDir);
        var path = Path.Combine(projectDir, ManifestFileName);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, project, JsonOptions, ct);
    }

    public async Task WriteTableFileAsync(string projectDir, string relativePath, TableFile tableFile, CancellationToken ct = default)
    {
        var fullPath = Path.Combine(projectDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var stream = File.Create(fullPath);
        await JsonSerializer.SerializeAsync(stream, tableFile, JsonOptions, ct);
    }

    public async Task<TableFile> ReadTableFileAsync(string projectDir, string relativePath, CancellationToken ct = default)
    {
        var fullPath = Path.Combine(projectDir, relativePath);
        await using var stream = File.OpenRead(fullPath);
        return await JsonSerializer.DeserializeAsync<TableFile>(stream, JsonOptions, ct)
               ?? throw new InvalidDataException($"Failed to deserialize {fullPath}");
    }
}
