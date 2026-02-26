using Microsoft.Data.SqlClient;
using Mimir.Api.Models;
using Mimir.Api.Services;

namespace Mimir.Api.Endpoints;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        // POST /api/accounts — create account (no auth required, captcha verified if provider configured)
        app.MapPost("/api/accounts", async (CreateAccountRequest req, AccountService accounts, CaptchaService captcha) =>
        {
            if (!await captcha.VerifyAsync(req.CaptchaToken))
                return Results.Json(new { error = "Captcha verification failed." },
                    statusCode: StatusCodes.Status400BadRequest);

            try
            {
                var account = await accounts.CreateAccountAsync(req);
                return Results.Created($"/api/accounts/{account.UserNo}", account);
            }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
            {
                return Results.Conflict(new { error = "Username already exists." });
            }
        })
        .RequireRateLimiting("register");

        // GET /api/accounts/me — own account info (JWT required)
        app.MapGet("/api/accounts/me", async (HttpContext ctx, AccountService accounts) =>
        {
            int userNo = GetUserNo(ctx);
            var account = await accounts.GetAccountAsync(userNo);
            return account is null ? Results.NotFound() : Results.Ok(account);
        })
        .RequireAuthorization();

        // GET /api/accounts/me/characters — own character list (JWT required)
        app.MapGet("/api/accounts/me/characters",
            async (HttpContext ctx, CharacterService chars) =>
            {
                int userNo = GetUserNo(ctx);
                var characters = await chars.GetCharactersByUserAsync(userNo);
                return Results.Ok(characters);
            })
            .RequireAuthorization();

        // GET /api/accounts/me/cash — own premium balance (JWT required)
        app.MapGet("/api/accounts/me/cash", async (HttpContext ctx, AccountService accounts) =>
        {
            int userNo = GetUserNo(ctx);
            var cash = await accounts.GetCashAsync(userNo);
            return cash is null ? Results.NotFound() : Results.Ok(cash);
        })
        .RequireAuthorization();

        // POST /api/accounts/me/cash — add cash to own account (admin required)
        app.MapPost("/api/accounts/me/cash",
            async (AddCashRequest req, HttpContext ctx, AccountService accounts) =>
            {
                int userNo = GetUserNo(ctx);
                var cash = await accounts.AddCashAsync(userNo, req.Amount);
                return cash is null ? Results.NotFound() : Results.Ok(cash);
            })
            .RequireAuthorization("admin");

        // POST /api/accounts/{id}/cash — add cash to any account (admin required)
        app.MapPost("/api/accounts/{id:int}/cash",
            async (int id, AddCashRequest req, AccountService accounts) =>
            {
                var cash = await accounts.AddCashAsync(id, req.Amount);
                return cash is null ? Results.NotFound() : Results.Ok(cash);
            })
            .RequireAuthorization("admin");

        // POST /api/accounts/me/inventory — give item to own account (admin required)
        app.MapPost("/api/accounts/me/inventory",
            async (GiveItemRequest req, HttpContext ctx, AccountService accounts) =>
            {
                int userNo = GetUserNo(ctx);
                await accounts.GiveItemAsync(userNo, req.GoodsNo, req.Amount);
                return Results.NoContent();
            })
            .RequireAuthorization("admin");

        // POST /api/accounts/{id}/inventory — give item to any account (admin required)
        app.MapPost("/api/accounts/{id:int}/inventory",
            async (int id, GiveItemRequest req, AccountService accounts) =>
            {
                await accounts.GiveItemAsync(id, req.GoodsNo, req.Amount);
                return Results.NoContent();
            })
            .RequireAuthorization("admin");
    }

    private static int GetUserNo(HttpContext ctx) =>
        int.Parse(ctx.User.FindFirst("userNo")!.Value);
}
