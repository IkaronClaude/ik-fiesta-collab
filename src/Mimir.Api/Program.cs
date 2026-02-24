using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Mimir.Api.Db;
using Mimir.Api.Endpoints;
using Mimir.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// --- Services ---
builder.Services.AddSingleton<DbConnectionFactory>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<CharacterService>();
builder.Services.AddScoped<ShopService>();

// --- JWT Authentication ---
var jwtSecret = builder.Configuration["JWT_SECRET"]
    ?? throw new InvalidOperationException("JWT_SECRET environment variable is not set.");

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        // Preserve claim types as-is (don't remap "role" -> long URN form).
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

// --- Build ---
var app = builder.Build();

// --- DB init: create tWebCredential if it doesn't exist ---
await DbInit.EnsureCreatedAsync(app.Services);

app.UseAuthentication();
app.UseAuthorization();

// --- Routes ---
app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapCharacterEndpoints();
app.MapShopEndpoints();

app.Run();
