namespace Mimir.Api.Models;

public record ShopItemResponse(int GoodsNo, string Name, int Price, int Unit);

public record GiveItemRequest(int GoodsNo, int Amount);
