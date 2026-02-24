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
        });

        // GET /api/shop/{goodsNo} — single shop item (no auth required)
        app.MapGet("/api/shop/{goodsNo:int}", async (int goodsNo, ShopService shop) =>
        {
            var item = await shop.GetItemByGoodsNoAsync(goodsNo);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });
    }
}
