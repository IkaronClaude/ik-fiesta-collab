using Microsoft.Data.SqlClient;
using Mimir.Api.Db;
using Mimir.Api.Models;

namespace Mimir.Api.Services;

public class ShopService
{
    private readonly DbConnectionFactory _db;

    public ShopService(DbConnectionFactory db) => _db = db;

    public async Task<List<ShopItemResponse>> GetAllItemsAsync()
    {
        await using var conn = await _db.OpenAccountAsync();
        await using var cmd = new SqlCommand(
            "SELECT goodsNo, name, price, unit FROM tItem WHERE isSell = 1 ORDER BY goodsNo",
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
            "SELECT goodsNo, name, price, unit FROM tItem WHERE goodsNo = @goodsNo AND isSell = 1",
            conn);
        cmd.Parameters.AddWithValue("@goodsNo", goodsNo);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return MapItem(reader);
    }

    private static ShopItemResponse MapItem(SqlDataReader r) => new(
        GoodsNo: r.GetInt32(r.GetOrdinal("goodsNo")),
        Name:    r.GetString(r.GetOrdinal("name")),
        Price:   r.GetInt32(r.GetOrdinal("price")),
        Unit:    r.GetInt32(r.GetOrdinal("unit"))
    );
}
