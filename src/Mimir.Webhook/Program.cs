using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mimir.Webhook;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<BuildRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BuildRunner>());

var app = builder.Build();
var cfg = app.Configuration;

var webhookSecret = cfg["WEBHOOK_SECRET"] ?? "";
var targetBranch  = cfg["WEBHOOK_BRANCH"]  ?? "master";

// POST /webhook — GitHub push event
app.MapPost("/webhook", async (HttpRequest req, BuildRunner runner) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var body = ms.ToArray();

    // Validate HMAC-SHA256 (skip if no secret configured — dev/testing)
    if (!string.IsNullOrEmpty(webhookSecret))
    {
        var sig = req.Headers["X-Hub-Signature-256"].FirstOrDefault() ?? "";
        if (!sig.StartsWith("sha256=")) return Results.Unauthorized();

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(webhookSecret));
        var expected  = "sha256=" + Convert.ToHexString(hmac.ComputeHash(body)).ToLower();
        var received  = sig.ToLower();
        if (expected.Length != received.Length ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(received)))
            return Results.Unauthorized();
    }

    // Only handle push events
    if (req.Headers["X-GitHub-Event"].FirstOrDefault() != "push")
        return Results.Ok(new { skipped = true, reason = "not a push event" });

    // Branch filter
    try
    {
        using var doc  = JsonDocument.Parse(body);
        var pushRef    = doc.RootElement.TryGetProperty("ref", out var r) ? r.GetString() ?? "" : "";
        if (pushRef != $"refs/heads/{targetBranch}")
            return Results.Ok(new { skipped = true, reason = $"ignoring {pushRef}" });
    }
    catch { return Results.BadRequest(new { error = "invalid payload" }); }

    if (!runner.TryTrigger())
        return Results.Conflict(new { error = "Build already in progress" });

    return Results.Accepted("/status", new { queued = true });
});

// GET /status — current build state
app.MapGet("/status", (BuildRunner runner) => Results.Ok(runner.GetStatus()));

// GET /log — full output of last/current build
app.MapGet("/log", (BuildRunner runner) => Results.Text(runner.GetLog(), "text/plain"));

app.Run();
