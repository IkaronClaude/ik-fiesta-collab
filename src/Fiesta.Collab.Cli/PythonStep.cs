using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fiesta.Collab.Sql;
using Microsoft.Extensions.Logging;

namespace Fiesta.Collab.Cli;

/// <summary>
/// A migration step written in Python (migrations/NNNN-name.py), run against the LIVE engine of the migrate run.
///
/// Rule (Fiesta2026on2016 CLAUDE.md, silver rule): a step is SQL unless SQL truly cannot express it, and even then
/// extending collab is weighed first. When a step is Python, it still only reads and writes TABLES - no files.
///
/// Protocol (the step is a child process; nothing is exported or reloaded):
///   step  -> collab   one line on the step's stdout:  "@@fiesta " + JSON request
///   collab -> step    one line on the step's stdin:   JSON reply
/// Requests: {"op":"query","sql":..,"params":{..}}      -> {"ok":true,"columns":[..],"rows":[[..],..]}
///           {"op":"exec","sql":..,"params":{..}}       -> {"ok":true,"affected":n}
///           {"op":"many","sql":..,"rows":[{..},..]}    -> {"ok":true,"affected":n}   (one transaction)
///   any failure                                        -> {"ok":false,"error":".."}
/// Every other stdout / stderr line is the step's own output and goes to the log. The helper module
/// python/fiesta_step.py (shipped beside fiesta.dll, on PYTHONPATH) wraps this: query() / execute() / executemany().
/// The interpreter is $FIESTA_PYTHON, else "python".
/// </summary>
public static class PythonStep
{
    private const string Prefix = "@@fiesta ";

    public static int Run(ISqlEngine engine, string path, ILogger logger)
    {
        var helperDir = Path.Combine(AppContext.BaseDirectory, "python");
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("FIESTA_PYTHON") ?? "python")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!,
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(Path.GetFullPath(path));
        var pp = Environment.GetEnvironmentVariable("PYTHONPATH");
        psi.Environment["PYTHONPATH"] = string.IsNullOrEmpty(pp) ? helperDir : helperDir + Path.PathSeparator + pp;
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        var name = Path.GetFileName(path);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"{name}: could not start {psi.FileName}");
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr) stderr.AppendLine(e.Data);
            logger.LogInformation("    {Step}: {Line}", name, e.Data);
        };
        proc.BeginErrorReadLine();

        var affected = 0;
        string? line;
        while ((line = proc.StandardOutput.ReadLine()) != null)
        {
            if (!line.StartsWith(Prefix, StringComparison.Ordinal))
            {
                logger.LogInformation("    {Step}: {Line}", name, line);
                continue;
            }
            string reply;
            try
            {
                using var doc = JsonDocument.Parse(line[Prefix.Length..]);
                var req = doc.RootElement;
                var op = req.GetProperty("op").GetString();
                var sql = req.GetProperty("sql").GetString() ?? "";
                switch (op)
                {
                    case "query":
                    {
                        var (cols, rows) = engine.QueryRows(sql, Params(req, "params"));
                        reply = JsonSerializer.Serialize(new { ok = true, columns = cols, rows });
                        break;
                    }
                    case "exec":
                    {
                        var n = engine.Execute(sql, Params(req, "params"));
                        affected += Math.Max(n, 0);
                        reply = JsonSerializer.Serialize(new { ok = true, affected = n });
                        break;
                    }
                    case "many":
                    {
                        var sets = req.TryGetProperty("rows", out var rs) && rs.ValueKind == JsonValueKind.Array
                            ? rs.EnumerateArray().Select(ToDict).ToList()
                            : new List<IReadOnlyDictionary<string, object?>>();
                        var n = engine.ExecuteMany(sql, sets);
                        affected += Math.Max(n, 0);
                        reply = JsonSerializer.Serialize(new { ok = true, affected = n });
                        break;
                    }
                    default:
                        reply = JsonSerializer.Serialize(new { ok = false, error = $"unknown op '{op}'" });
                        break;
                }
            }
            catch (Exception ex)
            {
                reply = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
            }
            proc.StandardInput.WriteLine(reply);
            proc.StandardInput.Flush();
        }
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            string tail;
            lock (stderr) tail = stderr.ToString();
            if (tail.Length > 2000) tail = tail[^2000..];
            throw new InvalidOperationException($"{name} exited with {proc.ExitCode}: {tail.Trim()}");
        }
        return affected;
    }

    private static IReadOnlyDictionary<string, object?> Params(JsonElement req, string prop)
        => req.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Object
            ? ToDict(p)
            : new Dictionary<string, object?>();

    private static IReadOnlyDictionary<string, object?> ToDict(JsonElement obj)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in obj.EnumerateObject())
            d[p.Name] = Value(p.Value);
        return d;
    }

    private static object? Value(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => v.GetString(),
        JsonValueKind.True => 1L,
        JsonValueKind.False => 0L,
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
        _ => v.GetRawText(),
    };
}
