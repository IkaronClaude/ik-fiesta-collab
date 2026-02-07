using System.Text.Json;
using System.Text.Json.Serialization;
using Mimir.Core.Models;

namespace Mimir.Core.Project;

public sealed class ProjectService : IProjectService
{
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions RowOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public const string ManifestFileName = "mimir.json";

    public async Task<MimirProject> LoadProjectAsync(string projectDir, CancellationToken ct = default)
    {
        var path = Path.Combine(projectDir, ManifestFileName);
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<MimirProject>(stream, ManifestOptions, ct)
               ?? throw new InvalidDataException($"Failed to deserialize {path}");
    }

    public async Task SaveProjectAsync(string projectDir, MimirProject project, CancellationToken ct = default)
    {
        Directory.CreateDirectory(projectDir);
        var path = Path.Combine(projectDir, ManifestFileName);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, project, ManifestOptions, ct);
    }

    public async Task WriteTableDataAsync(string projectDir, TableEntry entry, TableData data, CancellationToken ct = default)
    {
        var fullPath = Path.Combine(projectDir, entry.DataPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var writer = new StreamWriter(fullPath);
        foreach (var row in data.Rows)
        {
            var line = JsonSerializer.Serialize(row, RowOptions);
            await writer.WriteLineAsync(line.AsMemory(), ct);
        }
    }

    public async Task<TableData> ReadTableDataAsync(string projectDir, TableEntry entry, CancellationToken ct = default)
    {
        var fullPath = Path.Combine(projectDir, entry.DataPath);
        var rows = new List<Dictionary<string, object?>>();

        using var reader = new StreamReader(fullPath);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var row = JsonSerializer.Deserialize<Dictionary<string, object?>>(line, RowOptions)
                      ?? throw new InvalidDataException($"Failed to deserialize row in {fullPath}");
            rows.Add(row);
        }

        return new TableData
        {
            Schema = entry.Schema,
            Rows = rows
        };
    }
}
