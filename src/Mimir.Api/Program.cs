using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.RateLimiting;
using LettuceEncrypt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Mimir.Api.Db;
using Mimir.Api.Endpoints;
using Mimir.Api.Services;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// --- Services ---
builder.Services.AddSingleton<DbConnectionFactory>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<CharacterService>();
builder.Services.AddScoped<ShopService>();

// --- Captcha (singleton — provider/site-key are env-var constants) ---
builder.Services.AddHttpClient();
builder.Services.AddSingleton<CaptchaService>();

// --- JWT Authentication ---
var jwtSecret = cfg["JWT_SECRET"]
    ?? throw new InvalidOperationException("JWT_SECRET environment variable is not set.");

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.MapInboundClaims = false;
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = signingKey,
            ValidateIssuer           = false,
            ValidateAudience         = false,
            RoleClaimType            = "role",
        };
    });

builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("admin", p => p.RequireRole("admin"));
});

// --- Rate Limiting ---
builder.Services.AddRateLimiter(opts =>
{
    opts.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(GetClientIp(ctx),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window      = TimeSpan.FromMinutes(1)
            }));
    opts.AddFixedWindowLimiter("login", o =>
    {
        o.PermitLimit = 10;
        o.Window      = TimeSpan.FromMinutes(15);
    });
    opts.AddFixedWindowLimiter("register", o =>
    {
        o.PermitLimit = 3;
        o.Window      = TimeSpan.FromHours(1);
    });
    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// --- CORS (only if CORS_ORIGINS is set) ---
var corsOrigins = cfg["CORS_ORIGINS"]?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
if (corsOrigins.Length > 0)
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

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

// --- OpenAPI / Swagger (opt-in via ENABLE_SWAGGER=true) ---
var enableSwagger = string.Equals(cfg["ENABLE_SWAGGER"], "true", StringComparison.OrdinalIgnoreCase);
if (enableSwagger)
    builder.Services.AddOpenApi();

// --- Build ---
var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
    KnownIPNetworks = { },
    KnownProxies = { }
});

var pathBase = Environment.GetEnvironmentVariable("ASPNETCORE_PATHBASE");
if (!string.IsNullOrEmpty(pathBase))
    app.UsePathBase(pathBase);

// --- DB init: create tWebCredential if it doesn't exist ---
await DbInit.EnsureCreatedAsync(app.Services);

// --- Swagger UI ---
if (enableSwagger)
{
    app.MapOpenApi();
    var swaggerPrefix = string.IsNullOrEmpty(pathBase) ? "" : pathBase;
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint($"{swaggerPrefix}/openapi/v1.json", "Fiesta API");
        o.RoutePrefix = "swagger";
    });
}

// --- Middleware order ---
if (!string.IsNullOrEmpty(leDomain) || !string.IsNullOrEmpty(certPath))
    app.UseHttpsRedirection();
if (corsOrigins.Length > 0) app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// --- Public config endpoint (exposes captcha provider + site key for SPA) ---
app.MapGet("/api/config", (CaptchaService captcha) => Results.Ok(new
{
    captchaProvider = captcha.Provider,
    captchaSiteKey  = captcha.SiteKey
}))
.WithTags("Config")
.WithSummary("Get public config")
.WithDescription("Returns captcha provider and site key for the SPA frontend.")
.AllowAnonymous();

// --- Routes ---
app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapCharacterEndpoints();
app.MapShopEndpoints();

app.Run();

// --- Helpers ---
static string GetClientIp(HttpContext ctx) =>
    ctx.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
    ?? ctx.Connection.RemoteIpAddress?.ToString()
    ?? "unknown";
