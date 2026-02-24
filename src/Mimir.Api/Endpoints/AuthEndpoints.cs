using Mimir.Api.Models;
using Mimir.Api.Services;

namespace Mimir.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // POST /api/auth/login — verify BCrypt web password, return JWT
        app.MapPost("/api/auth/login", async (LoginRequest req, AccountService accounts, TokenService tokens) =>
        {
            var result = await accounts.ValidateWebCredentialAsync(req.Username, req.Password);
            if (result is null)
                return Results.Unauthorized();

            var token = tokens.CreateToken(result.Value.UserNo, result.Value.IsAdmin);
            return Results.Ok(new LoginResponse(token));
        });

        // POST /api/auth/set-ingame-password — update MD5 game password for own account
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
