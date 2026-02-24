using Microsoft.Data.SqlClient;
using Mimir.Api.Db;
using Mimir.Api.Models;

namespace Mimir.Api.Services;

/// <summary>
/// Reads premium shop items from the Account database.
/// NOTE: Table name 'tMallGoods' may need adjustment to match the actual server schema
///       (common names: tCashShop, tMallItem, tGoodsInfo).
/// </summary>
public class ShopService
{
    private readonly DbConnectionFactory _db;

    public ShopService(DbConnectionFactory db) => _db = db;

    public async Task<List<ShopItemResponse>> GetAllItemsAsync()
    {
        await using var conn = await _db.OpenAccountAsync();
        await using var cmd = new SqlCommand(
            "SELECT nGoodsNo, sName, nPrice, nItemNo FROM tMallGoods ORDER BY nGoodsNo",
            conn);

        var results = new List<ShopItemResponse>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(MapItem(reader));

        return results;
    }

    public async Task<ShopItemResponse?> GetItemByGoodsNoAsync(int goodsNo)
    {
        await using var conn = await _db.OpenAccountAsync();
        await using var cmd = new SqlCommand(
            "SELECT nGoodsNo, sName, nPrice, nItemNo FROM tMallGoods WHERE nGoodsNo = @goodsNo",
            conn);
        cmd.Parameters.AddWithValue("@goodsNo", goodsNo);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return MapItem(reader);
    }

    private static ShopItemResponse MapItem(SqlDataReader r) => new(
        GoodsNo: r.GetInt32(r.GetOrdinal("nGoodsNo")),
        Name:    r.GetString(r.GetOrdinal("sName")),
        Price:   r.GetInt32(r.GetOrdinal("nPrice")),
        ItemNo:  r.GetInt32(r.GetOrdinal("nItemNo"))
    );
}
