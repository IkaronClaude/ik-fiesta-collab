using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace Mimir.Webhook;

public sealed record BuildStatus(
    bool Building,
    bool? LastSuccess,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string LastStep = "");

public sealed class BuildRunner : BackgroundService
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<BuildRunner> _logger;

    // Capacity 1: extra triggers while building are silently dropped
    private readonly Channel<bool> _trigger = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private volatile BuildStatus _status = new(false, null, null, null);
    private readonly StringBuilder _log = new();
    private readonly object _logLock = new();

    public BuildRunner(IConfiguration cfg, ILogger<BuildRunner> logger)
    {
        _cfg    = cfg;
        _logger = logger;
    }

    /// <summary>Returns false if a build is already queued/running.</summary>
    public bool TryTrigger() => _trigger.Writer.TryWrite(true);

    public BuildStatus GetStatus() => _status;

    public string GetLog() { lock (_logLock) return _log.ToString(); }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var _ in _trigger.Reader.ReadAllAsync(ct))
            await RunPipelineAsync(ct);
    }

    // -----------------------------------------------------------------

    private async Task RunPipelineAsync(CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        _status = new BuildStatus(true, null, startedAt, null);
        lock (_logLock) _log.Clear();
        Log($"=== Build started {startedAt:u} ===");

        var projDir  = _cfg["PROJ_DIR"] ?? "C:/project";
        var cliDll   = "C:/mimir/Mimir.Cli.dll";
        var pack     = bool.TryParse(_cfg["PACK_ENABLED"], out var pe) && pe;
        var project  = _cfg["COMPOSE_PROJECT"] ?? "";
        var services = (_cfg["RESTART_SERVICES"] ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        bool   success;
        string lastStep;
        try
        {
            (success, lastStep) = await RunStepsAsync(projDir, cliDll, pack, project, services, ct);
        }
        catch (Exception ex)
        {
            Log($"UNHANDLED EXCEPTION: {ex}");
            success  = false;
            lastStep = "exception";
        }

        var finishedAt = DateTime.UtcNow;
        _status = new BuildStatus(false, success, startedAt, finishedAt, lastStep);
        Log($"=== Build {(success ? "SUCCEEDED" : "FAILED")} — {lastStep} — {finishedAt:u} ===");
    }

    private async Task<(bool, string)> RunStepsAsync(
        string projDir, string cliDll, bool pack,
        string composeProject, string[] restartServices,
        CancellationToken ct)
    {
        // 1. Pull latest code
        if (await RunAsync("git", $"-C \"{projDir}\" pull", projDir, ct) != 0)
            return (false, "git pull");

        // 2. Build all environments
        if (await RunAsync("dotnet", $"\"{cliDll}\" build --all -p \"{projDir}\"", projDir, ct) != 0)
            return (false, "mimir build");

        // 3. Pack client patches (optional)
        if (pack)
        {
            if (await RunAsync("dotnet", $"\"{cliDll}\" pack --env client -p \"{projDir}\"", projDir, ct) != 0)
                return (false, "mimir pack");
        }

        // 4. Snapshot build/server -> deployed/server
        var buildSrv    = Path.Combine(projDir, "build",    "server").Replace('/', '\\');
        var deployedSrv = Path.Combine(projDir, "deployed", "server").Replace('/', '\\');
        var rc = await RunAsync("robocopy", $"\"{buildSrv}\" \"{deployedSrv}\" /MIR /NP /R:2 /W:1", projDir, ct);
        if (rc > 7) return (false, $"robocopy (exit {rc})");   // robocopy 0-7 are success codes

        // 5. Restart game containers (not SQL)
        if (restartServices.Length > 0 && !string.IsNullOrEmpty(composeProject))
        {
            var containers = string.Join(" ", restartServices.Select(s => $"{composeProject}-{s}-1"));
            Log($"Restarting: {containers}");
            if (await RunAsync("docker", $"restart {containers}", projDir, ct) != 0)
                return (false, "docker restart");
        }

        return (true, "done");
    }

    private async Task<int> RunAsync(string exe, string args, string workDir, CancellationToken ct)
    {
        Log($"$ {exe} {args}");
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory       = workDir,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Drain stdout/stderr on background threads so the process doesn't block on full pipe buffers
        var outTask = Task.Run(() => { while (!proc.StandardOutput.EndOfStream) Log(proc.StandardOutput.ReadLine()!); });
        var errTask = Task.Run(() => { while (!proc.StandardError.EndOfStream)  Log(proc.StandardError.ReadLine()!); });

        await proc.WaitForExitAsync(ct);
        await Task.WhenAll(outTask, errTask);
        return proc.ExitCode;
    }

    private void Log(string line)
    {
        _logger.LogInformation("{Line}", line);
        lock (_logLock) _log.AppendLine(line);
    }
}
