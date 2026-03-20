using Mimir.Api.Models;
using Mimir.Api.Services;

namespace Mimir.Api.Endpoints;

public static class ShopEndpoints
{
    public static void MapShopEndpoints(this WebApplication app)
    {
        // GET /api/shop — public shop listing (no auth required)
        app.MapGet("/api/shop", async (ShopService shop) =>
        {
            var items = await shop.GetAllItemsAsync();
            return Results.Ok(items);
        })
            .WithTags("Shop")
            .WithSummary("List all shop items")
            .Produces<List<ShopItemResponse>>();

        app.MapGet("/api/shop/{goodsNo:int}", async (int goodsNo, ShopService shop) =>
        {
            var item = await shop.GetItemByGoodsNoAsync(goodsNo);
            return item is null ? Results.NotFound() : Results.Ok(item);
        })
            .WithTags("Shop")
            .WithSummary("Get shop item by ID")
            .Produces<ShopItemResponse>()
            .Produces(StatusCodes.Status404NotFound);
    }
}
