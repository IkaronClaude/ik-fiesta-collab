using Mimir.Api.Services;

namespace Mimir.Api.Endpoints;

public static class CharacterEndpoints
{
    public static void MapCharacterEndpoints(this WebApplication app)
    {
        // GET /api/characters/{charNo} — public character info (no auth required)
        // Never includes nUserNo in the response.
        app.MapGet("/api/characters/{charNo:int}", async (int charNo, CharacterService chars) =>
        {
            var character = await chars.GetCharacterByCharNoAsync(charNo);
            return character is null ? Results.NotFound() : Results.Ok(character);
        });
    }
}
