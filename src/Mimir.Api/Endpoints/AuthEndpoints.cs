using Mimir.Api.Models;
using Mimir.Api.Services;

namespace Mimir.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // POST /api/auth/login — verify BCrypt web password, return JWT.
        // After the first failed attempt the response includes requiresCaptcha: true.
        // Subsequent attempts must include captchaToken.
        app.MapPost("/api/auth/login", async (LoginRequest req, AccountService accounts, TokenService tokens, CaptchaService captcha) =>
        {
            // Verify captcha when a token is supplied (client sends one after first failure).
            if (req.CaptchaToken is not null)
            {
                if (!await captcha.VerifyAsync(req.CaptchaToken))
                    return Results.Json(new LoginResponse(null, RequiresCaptcha: true),
                        statusCode: StatusCodes.Status401Unauthorized);
            }

            var result = await accounts.ValidateWebCredentialAsync(req.Username, req.Password);
            if (result is null)
                return Results.Json(new LoginResponse(null, RequiresCaptcha: true),
                    statusCode: StatusCodes.Status401Unauthorized);

            var token = tokens.CreateToken(result.Value.UserNo, result.Value.IsAdmin);
            return Results.Ok(new LoginResponse(token));
        })
        .RequireRateLimiting("login");

        // POST /api/auth/set-web-password — update BCrypt web password (JWT required)
        app.MapPost("/api/auth/set-web-password",
            async (SetWebPasswordRequest req, HttpContext ctx, AccountService accounts) =>
            {
                int userNo = GetUserNo(ctx);
                await accounts.SetWebPasswordAsync(userNo, req.NewPassword);
                return Results.NoContent();
            })
            .RequireAuthorization();

        // POST /api/auth/set-ingame-password — update MD5 game password (JWT required)
        app.MapPost("/api/auth/set-ingame-password",
            async (SetIngamePasswordRequest req, HttpContext ctx, AccountService accounts) =>
            {
                int userNo = GetUserNo(ctx);
                await accounts.SetIngamePasswordAsync(userNo, req.NewPassword);
                return Results.NoContent();
            })
            .RequireAuthorization();
    }

    private static int GetUserNo(HttpContext ctx) =>
        int.Parse(ctx.User.FindFirst("userNo")!.Value);
}
