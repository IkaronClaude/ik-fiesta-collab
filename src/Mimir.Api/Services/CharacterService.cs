using Microsoft.Data.SqlClient;
using Mimir.Api.Db;
using Mimir.Api.Models;

namespace Mimir.Api.Services;

public class CharacterService
{
    private readonly DbConnectionFactory _db;

    public CharacterService(DbConnectionFactory db) => _db = db;

    public async Task<List<CharacterResponse>> GetCharactersByUserAsync(int userNo)
    {
        await using var conn = await _db.OpenCharacterAsync();
        await using var cmd = new SqlCommand("""
            SELECT c.nCharNo, c.sID, c.nLevel, c.nExp, c.nMoney, c.sLoginZone, c.dLastLoginDate,
                   s.nClass, s.nRace, s.nGender
            FROM   tCharacter c
            LEFT JOIN tCharacterShape s ON s.nCharNo = c.nCharNo
            WHERE  c.nUserNo = @userNo AND c.bDeleted = 0
            ORDER BY c.nSlotNo
            """, conn);
        cmd.Parameters.AddWithValue("@userNo", userNo);

        var results = new List<CharacterResponse>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(MapCharacter(reader));

        return results;
    }

    public async Task<List<CharacterResponse>> GetLeaderboardAsync()
    {
        await using var conn = await _db.OpenCharacterAsync();
        await using var cmd = new SqlCommand("""
            SELECT TOP 100
                c.nCharNo, c.sID, c.nLevel, c.nExp, c.nMoney, c.sLoginZone, c.dLastLoginDate,
                s.nClass, s.nRace, s.nGender
            FROM   tCharacter c
            LEFT JOIN tCharacterShape s ON s.nCharNo = c.nCharNo
            WHERE  c.bDeleted = 0
            ORDER BY c.nExp DESC
            """, conn);

        var results = new List<CharacterResponse>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(MapCharacter(reader));

        return results;
    }

    public async Task<CharacterResponse?> GetCharacterByCharNoAsync(int charNo)
    {
        await using var conn = await _db.OpenCharacterAsync();
        await using var cmd = new SqlCommand("""
            SELECT c.nCharNo, c.sID, c.nLevel, c.nExp, c.nMoney, c.sLoginZone, c.dLastLoginDate,
                   s.nClass, s.nRace, s.nGender
            FROM   tCharacter c
            LEFT JOIN tCharacterShape s ON s.nCharNo = c.nCharNo
            WHERE  c.nCharNo = @charNo AND c.bDeleted = 0
            """, conn);
        cmd.Parameters.AddWithValue("@charNo", charNo);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return MapCharacter(reader);
    }

    private static CharacterResponse MapCharacter(SqlDataReader r)
    {
        int lastLoginOrd = r.GetOrdinal("dLastLoginDate");
        int nClassOrd    = r.GetOrdinal("nClass");
        int nRaceOrd     = r.GetOrdinal("nRace");
        int nGenderOrd   = r.GetOrdinal("nGender");

        return new CharacterResponse(
            CharNo:        r.GetInt32(r.GetOrdinal("nCharNo")),
            Name:          r.GetString(r.GetOrdinal("sID")),
            Level:         Convert.ToInt32(r.GetValue(r.GetOrdinal("nLevel"))),
            Exp:           Convert.ToInt64(r.GetValue(r.GetOrdinal("nExp"))),
            Money:         Convert.ToInt64(r.GetValue(r.GetOrdinal("nMoney"))),
            LoginZone:     r.GetString(r.GetOrdinal("sLoginZone")),
            LastLoginDate: r.IsDBNull(lastLoginOrd) ? null : r.GetDateTime(lastLoginOrd),
            Class:         r.IsDBNull(nClassOrd)  ? 0 : Convert.ToInt32(r.GetValue(nClassOrd)),
            Race:          r.IsDBNull(nRaceOrd)   ? 0 : Convert.ToInt32(r.GetValue(nRaceOrd)),
            Gender:        r.IsDBNull(nGenderOrd) ? 0 : Convert.ToInt32(r.GetValue(nGenderOrd))
        );
    }
}
