using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mimir.Core.Project;

/// <summary>
/// Reads and writes per-environment config files from environments/&lt;name&gt;.json.
/// </summary>
public static class EnvironmentStore
{
    public const string EnvDir = "environments";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    public static string GetEnvDir(string projectDir) =>
        Path.Combine(projectDir, EnvDir);

    public static string GetEnvPath(string projectDir, string envName) =>
        Path.Combine(projectDir, EnvDir, $"{envName}.json");

    public static bool Exists(string projectDir, string envName) =>
        File.Exists(GetEnvPath(projectDir, envName));

    public static IReadOnlyList<string> ListNames(string projectDir)
    {
        var dir = GetEnvDir(projectDir);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != null)
            .Cast<string>()
            .OrderBy(n => n)
            .ToList();
    }

    public static EnvironmentConfig? Load(string projectDir, string envName)
    {
        var path = GetEnvPath(projectDir, envName);
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<EnvironmentConfig>(json, JsonOptions);
    }

    public static Dictionary<string, EnvironmentConfig> LoadAll(string projectDir)
    {
        var result = new Dictionary<string, EnvironmentConfig>();
        foreach (var name in ListNames(projectDir))
        {
            var config = Load(projectDir, name);
            if (config != null) result[name] = config;
        }
        return result;
    }

    public static void Save(string projectDir, string envName, EnvironmentConfig config)
    {
        var dir = GetEnvDir(projectDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(GetEnvPath(projectDir, envName), JsonSerializer.Serialize(config, JsonOptions));
    }

    public static void Remove(string projectDir, string envName)
    {
        var path = GetEnvPath(projectDir, envName);
        if (File.Exists(path)) File.Delete(path);
    }

    public static void EnsureEnvDir(string projectDir) =>
        Directory.CreateDirectory(GetEnvDir(projectDir));
}
