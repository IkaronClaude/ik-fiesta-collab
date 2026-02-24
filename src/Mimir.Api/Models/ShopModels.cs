namespace Mimir.Api.Models;

public record ShopItemResponse(int GoodsNo, string Name, int Price, int ItemNo);

public record GiveItemRequest(int ItemNo, int Amount);
