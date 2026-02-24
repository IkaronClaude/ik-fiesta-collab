using Microsoft.Data.SqlClient;

namespace Mimir.Api.Db;

public class DbConnectionFactory
{
    private readonly string _accountConn;
    private readonly string _characterConn;

    public DbConnectionFactory(IConfiguration config)
    {
        _accountConn = config["ACCOUNT_CONN"]
            ?? throw new InvalidOperationException("ACCOUNT_CONN environment variable is not set.");
        _characterConn = config["CHARACTER_CONN"]
            ?? throw new InvalidOperationException("CHARACTER_CONN environment variable is not set.");
    }

    public async Task<SqlConnection> OpenAccountAsync()
    {
        var conn = new SqlConnection(_accountConn);
        await conn.OpenAsync();
        return conn;
    }

    public async Task<SqlConnection> OpenCharacterAsync()
    {
        var conn = new SqlConnection(_characterConn);
        await conn.OpenAsync();
        return conn;
    }
}
