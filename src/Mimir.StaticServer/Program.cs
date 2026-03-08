using LettuceEncrypt;
using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// --- HTTPS ---
// Priority: Let's Encrypt (auto cert) > manual PFX cert > HTTP only (dev default).
// Set LETSENCRYPT_DOMAIN + LETSENCRYPT_EMAIL for auto certs via ACME HTTP-01.
// Set HTTPS_CERT_PATH for a manually provisioned PFX file.
// In both cases also expose ports 80 and 443 in docker-compose and set
//   ASPNETCORE_URLS=http://+:80;https://+:443
var leDomain = cfg["LETSENCRYPT_DOMAIN"];
var certPath = cfg["HTTPS_CERT_PATH"];

if (!string.IsNullOrEmpty(leDomain))
{
    var certDir = cfg["LETSENCRYPT_CERT_DIR"] ?? "C:/certs";
    builder.Services.AddLettuceEncrypt(o =>
    {
        o.AcceptTermsOfService = true;
        o.DomainNames         = [leDomain];
        o.EmailAddress        = cfg["LETSENCRYPT_EMAIL"] ?? "";
    }).PersistDataToDirectory(new DirectoryInfo(certDir), null);
    builder.WebHost.ConfigureKestrel(k =>
        k.ConfigureHttpsDefaults(h => h.UseLettuceEncrypt(k.ApplicationServices)));
}
else if (!string.IsNullOrEmpty(certPath))
{
    builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h =>
        h.ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile(
            certPath, cfg["HTTPS_CERT_PASSWORD"])));
}

var app = builder.Build();

// Dynamic config endpoint — SPA reads this on load to find the API URL.
app.MapGet("/config.json", () => Results.Ok(new
{
    apiUrl = cfg["API_URL"] ?? "http://localhost:5000"
}));

if (!string.IsNullOrEmpty(leDomain) || !string.IsNullOrEmpty(certPath))
    app.UseHttpsRedirection();

app.UseDefaultFiles();      // / -> index.html
app.UseStaticFiles();
app.MapFallbackToFile("index.html");   // SPA client-side routing

app.Run();
