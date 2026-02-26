using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// HTTPS: manual PFX cert
if (!string.IsNullOrEmpty(cfg["HTTPS_CERT_PATH"]))
{
    builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h =>
        h.ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile(
            cfg["HTTPS_CERT_PATH"]!, cfg["HTTPS_CERT_PASSWORD"])));
}

var app = builder.Build();

// Dynamic config endpoint — SPA reads this on load to find the API URL.
app.MapGet("/config.json", () => Results.Ok(new
{
    apiUrl = cfg["API_URL"] ?? "http://localhost:5000"
}));

app.UseDefaultFiles();      // / -> index.html
app.UseStaticFiles();
app.MapFallbackToFile("index.html");   // SPA client-side routing

app.Run();
