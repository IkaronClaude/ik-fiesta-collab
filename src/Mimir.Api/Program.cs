using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

// --- HTTPS: manual PFX cert (optional) ---
if (!string.IsNullOrEmpty(cfg["HTTPS_CERT_PATH"]))
{
    builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h =>
        h.ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile(
            cfg["HTTPS_CERT_PATH"]!, cfg["HTTPS_CERT_PASSWORD"])));
}

// --- Build ---
var app = builder.Build();

// --- DB init: create tWebCredential if it doesn't exist ---
await DbInit.EnsureCreatedAsync(app.Services);

// --- Middleware order ---
app.UseRateLimiter();
if (corsOrigins.Length > 0) app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// --- Public config endpoint (exposes captcha provider + site key for SPA) ---
app.MapGet("/api/config", (CaptchaService captcha) => Results.Ok(new
{
    captchaProvider = captcha.Provider,
    captchaSiteKey  = captcha.SiteKey
})).AllowAnonymous();

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
