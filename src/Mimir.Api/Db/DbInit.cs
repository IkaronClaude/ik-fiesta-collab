using Microsoft.Data.SqlClient;

namespace Mimir.Api.Db;

public static class DbInit
{
    public static async Task EnsureCreatedAsync(IServiceProvider services)
    {
        var factory = services.GetRequiredService<DbConnectionFactory>();
        await using var conn = await factory.OpenAccountAsync();

        await using var cmd = new SqlCommand("""
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'tWebCredential')
            CREATE TABLE tWebCredential (
                nUserNo       int          NOT NULL PRIMARY KEY,
                sPasswordHash nvarchar(80) NOT NULL,
                dCreated      datetime     NOT NULL DEFAULT GETDATE(),
                dLastLogin    datetime     NULL,
                CONSTRAINT FK_WebCred_User FOREIGN KEY (nUserNo) REFERENCES tUser(nUserNo)
            )
            """, conn);

        await cmd.ExecuteNonQueryAsync();
    }
}
