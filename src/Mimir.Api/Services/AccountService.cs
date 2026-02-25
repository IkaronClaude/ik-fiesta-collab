using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Mimir.Api.Db;
using Mimir.Api.Models;

namespace Mimir.Api.Services;

public class AccountService
{
    private readonly DbConnectionFactory _db;

    public AccountService(DbConnectionFactory db) => _db = db;

    private static string Md5Hex(string s) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLower();

    public async Task<AccountResponse> CreateAccountAsync(CreateAccountRequest req)
    {
        var passwordMd5 = Md5Hex(req.IngamePassword);
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(req.WebPassword);

        await using var conn = await _db.OpenAccountAsync();
        using var tx = conn.BeginTransaction();

        // Create game account via stored procedure.
        int userNo;
        await using (var insertCmd = new SqlCommand("usp_User_insert", conn, tx)
        {
            CommandType = CommandType.StoredProcedure
        })
        {
            insertCmd.Parameters.AddWithValue("@userID", req.Username);
            insertCmd.Parameters.AddWithValue("@userPW", passwordMd5);
            insertCmd.Parameters.AddWithValue("@userName", req.Username);
            insertCmd.Parameters.AddWithValue("@userIP", "127.0.0.1");
            insertCmd.Parameters.AddWithValue("@eMail", req.Email ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@isMail", false);
            var outParam = insertCmd.Parameters.Add("@userNo", SqlDbType.Int);
            outParam.Direction = ParameterDirection.Output;
            await insertCmd.ExecuteNonQueryAsync();
            userNo = (int)outParam.Value;
        }

        var created = DateTime.UtcNow;

        // Insert web credential for API login.
        await using (var credCmd = new SqlCommand(
            "INSERT INTO tWebCredential (nUserNo, sPasswordHash) VALUES (@userNo, @hash)",
            conn, tx))
        {
            credCmd.Parameters.AddWithValue("@userNo", userNo);
            credCmd.Parameters.AddWithValue("@hash", passwordHash);
            await credCmd.ExecuteNonQueryAsync();
        }

        tx.Commit();
        return new AccountResponse(userNo, req.Username, req.Email, created);
    }

    public async Task<AccountResponse?> GetAccountAsync(int userNo)
    {
        await using var conn = await _db.OpenAccountAsync();
        await using var cmd = new SqlCommand(
            "SELECT nUserNo, sUserID, dDate FROM tUser WHERE nUserNo = @userNo",
            conn);
        cmd.Parameters.AddWithValue("@userNo", userNo);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new AccountResponse(
            reader.GetInt32(reader.GetOrdinal("nUserNo")),
            reader.GetString(reader.GetOrdinal("sUserID")),
            null,
            reader.GetDateTime(reader.GetOrdinal("dDate"))
        );
    }

    /// <summary>
    /// Returns (UserNo, IsAdmin) if credentials are valid, or null if invalid.
    /// </summary>
    public async Task<(int UserNo, bool IsAdmin)?> ValidateWebCredentialAsync(string username, string password)
    {
        await using var conn = await _db.OpenAccountAsync();
        await using var cmd = new SqlCommand("""
            SELECT u.nUserNo, u.nAuthID, w.sPasswordHash
            FROM tUser u
            JOIN tWebCredential w ON w.nUserNo = u.nUserNo
            WHERE u.sUserID = @username
            """, conn);
        cmd.Parameters.AddWithValue("@username", username);

        int userNo;
        bool isAdmin;
        string hash;

        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) return null;
            userNo  = reader.GetInt32(reader.GetOrdinal("nUserNo"));
            isAdmin = reader.GetInt32(reader.GetOrdinal("nAuthID")) == 9;
            hash    = reader.GetString(reader.GetOrdinal("sPasswordHash"));
        }

        if (!BCrypt.Net.BCrypt.Verify(password, hash)) return null;

        // Stamp last login time.
        await using var upd = new SqlCommand(
            "UPDATE tWebCredential SET dLastLogin = GETDATE() WHERE nUserNo = @userNo", conn);
        upd.Parameters.AddWithValue("@userNo", userNo);
        await upd.ExecuteNonQueryAsync();

        return (userNo, isAdmin);
    }

    public async Task SetIngamePasswordAsync(int userNo, string newPassword)
    {
        var passwordMd5 = Md5Hex(newPassword);
        await using var conn = await _db.OpenAccountAsync();
        await using var cmd = new SqlCommand(
            "UPDATE tUser SET sUserPW = @pw WHERE nUserNo = @userNo", conn);
        cmd.Parameters.AddWithValue("@pw", passwordMd5);
        cmd.Parameters.AddWithValue("@userNo", userNo);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<CashResponse?> GetCashAsync(int userNo)
    {
        await using var conn = await _db.OpenAccountAsync();
        await using var cmd = new SqlCommand(
            "SELECT ISNULL(SUM(cash), 0) FROM tCash WHERE userNo = @userNo", conn);
        cmd.Parameters.AddWithValue("@userNo", userNo);

        var result = await cmd.ExecuteScalarAsync();
        if (result is null || result == DBNull.Value) return null;
        return new CashResponse(userNo, Convert.ToInt32(result));
    }

    public async Task<CashResponse?> AddCashAsync(int userNo, int amount)
    {
        await using var conn = await _db.OpenAccountAsync();
        await using var cmd = new SqlCommand("""
            INSERT INTO tCash (userNo, cash, cashtype, status) VALUES (@userNo, @amount, 0, 0);
            SELECT ISNULL(SUM(cash), 0) FROM tCash WHERE userNo = @userNo;
            """, conn);
        cmd.Parameters.AddWithValue("@userNo", userNo);
        cmd.Parameters.AddWithValue("@amount", amount);

        var result = await cmd.ExecuteScalarAsync();
        if (result is null || result == DBNull.Value) return null;
        return new CashResponse(userNo, Convert.ToInt32(result));
    }

    /// <summary>
    /// Grants a premium/cash item to the account's item storage.
    /// NOTE: Table name 'tAccountItem' may need adjustment to match the actual server schema
    ///       (common names: tCashItem, tPremiumItem, tAccountInventory).
    /// </summary>
    public async Task GiveItemAsync(int userNo, int itemNo, int amount)
    {
        await using var conn = await _db.OpenAccountAsync();
        await using var cmd = new SqlCommand("""
            INSERT INTO tAccountItem (nUserNo, nItemNo, nCount)
            VALUES (@userNo, @itemNo, @amount)
            """, conn);
        cmd.Parameters.AddWithValue("@userNo", userNo);
        cmd.Parameters.AddWithValue("@itemNo", itemNo);
        cmd.Parameters.AddWithValue("@amount", amount);
        await cmd.ExecuteNonQueryAsync();
    }
}
