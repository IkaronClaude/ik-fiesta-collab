using Mimir.Api.Models;
using Mimir.Api.Services;

namespace Mimir.Api.Endpoints;

public static class CharacterEndpoints
{
    public static void MapCharacterEndpoints(this WebApplication app)
    {
        // GET /api/leaderboard — top 100 characters by exp (public)
        app.MapGet("/api/leaderboard", async (CharacterService chars) =>
            Results.Ok(await chars.GetLeaderboardAsync()))
            .WithTags("Characters")
            .WithSummary("Get leaderboard")
            .WithDescription("Returns top 100 characters ranked by experience points.")
            .Produces<List<CharacterResponse>>();

        app.MapGet("/api/characters/{charNo:int}", async (int charNo, CharacterService chars) =>
        {
            var character = await chars.GetCharacterByCharNoAsync(charNo);
            return character is null ? Results.NotFound() : Results.Ok(character);
        })
            .WithTags("Characters")
            .WithSummary("Get character by ID")
            .WithDescription("Returns public character info. Does not expose the owning account.")
            .Produces<CharacterResponse>()
            .Produces(StatusCodes.Status404NotFound);
    }
}
