using System.Text.Json;

namespace Fiesta.Collab.Core.Models;

/// <summary>
/// The template action buildAs ({"action": "buildAs", "table": T, "env": E, "as": A, "path": P}): when env E is built,
/// table T's env-A view - A's columns, rows and file-format header - is written into E's output as well, in directory P
/// (relative to E's output), replacing the file of the same name E builds itself. For a file E must hold exactly as A
/// has it. Recorded at import in T's metadata as {E: {as: A, path: P}} so the build needs no template.
/// </summary>
public static class BuildAs
{
    public const string MetadataKey = "buildAs";

    public sealed record Target(string As, string Path);

    /// <summary>the {env: target} map stored in metadata (a dictionary in memory, a JSON object once saved)</summary>
    public static Dictionary<string, Target> Read(object? raw) => raw switch
    {
        Dictionary<string, Target> d => d,
        Dictionary<string, Dictionary<string, string>> j => j.ToDictionary(
            kv => kv.Key, kv => new Target(kv.Value.GetValueOrDefault("as") ?? "", kv.Value.GetValueOrDefault("path") ?? "")),
        JsonElement je when je.ValueKind == JsonValueKind.Object => je.EnumerateObject().ToDictionary(
            p => p.Name,
            p => new Target(
                p.Value.TryGetProperty("as", out var a) ? a.GetString() ?? "" : "",
                p.Value.TryGetProperty("path", out var q) ? q.GetString() ?? "" : "")),
        _ => new Dictionary<string, Target>(),
    };

    /// <summary>where this table's other-env view goes when `env` is built, or null</summary>
    public static Target? For(IDictionary<string, object>? metadata, string env)
    {
        if (metadata is null || !metadata.TryGetValue(MetadataKey, out var raw) || raw is null)
            return null;
        return Read(raw).TryGetValue(env, out var t) && !string.IsNullOrEmpty(t.As) ? t : null;
    }

    /// <summary>the JSON form saved in metadata</summary>
    public static Dictionary<string, Dictionary<string, string>> ToJson(Dictionary<string, Target> map) =>
        map.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string> { ["as"] = kv.Value.As, ["path"] = kv.Value.Path });
}
