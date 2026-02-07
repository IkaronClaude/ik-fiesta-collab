using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Mimir.Core.Models;

namespace Mimir.Sql;

public sealed class SqlEngine : ISqlEngine
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<SqlEngine> _logger;

    public SqlEngine(ILogger<SqlEngine> logger)
    {
        _logger = logger;
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        Execute("PRAGMA foreign_keys = ON;");
        Execute("PRAGMA journal_mode = WAL;");
    }

    public void LoadTable(TableData data)
    {
        var schema = data.Schema;
        _logger.LogDebug("Loading table {TableName} ({RowCount} rows)", schema.TableName, data.Rows.Count);

        var createSql = BuildCreateTable(schema);
        Execute(createSql);

        if (data.Rows.Count == 0) return;

        var columns = schema.Columns;
        var paramNames = columns.Select((_, i) => $"@p{i}").ToList();
        var insertSql = $"INSERT INTO [{schema.TableName}] ({string.Join(", ", columns.Select(c => $"[{c.Name}]"))}) " +
                        $"VALUES ({string.Join(", ", paramNames)})";

        using var transaction = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = insertSql;
        cmd.Transaction = transaction;

        var parameters = new SqliteParameter[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            parameters[i] = cmd.CreateParameter();
            parameters[i].ParameterName = paramNames[i];
            cmd.Parameters.Add(parameters[i]);
        }

        cmd.Prepare();

        foreach (var row in data.Rows)
        {
            for (int i = 0; i < columns.Count; i++)
            {
                var colName = columns[i].Name;
                parameters[i].Value = row.TryGetValue(colName, out var val) && val is not null
                    ? ConvertForSqlite(val, columns[i].Type)
                    : DBNull.Value;
            }
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
        _logger.LogInformation("Loaded {TableName}: {RowCount} rows, {ColCount} columns",
            schema.TableName, data.Rows.Count, columns.Count);
    }

    public int Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    public List<Dictionary<string, object?>> Query(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        var results = new List<Dictionary<string, object?>>();

        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                row[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            results.Add(row);
        }

        return results;
    }

    public TableData ExtractTable(TableSchema schema)
    {
        var rows = Query($"SELECT * FROM [{schema.TableName}]");

        // Convert SQLite types back to the expected .NET types based on schema
        var typedRows = new List<Dictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var typedRow = new Dictionary<string, object?>(row.Count);
            foreach (var col in schema.Columns)
            {
                if (row.TryGetValue(col.Name, out var val) && val is not null)
                    typedRow[col.Name] = ConvertFromSqlite(val, col.Type);
                else
                    typedRow[col.Name] = null;
            }
            typedRows.Add(typedRow);
        }

        return new TableData { Schema = schema, Rows = typedRows };
    }

    public IReadOnlyList<string> ListTables()
    {
        var results = Query("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name");
        return results.Select(r => r["name"]?.ToString() ?? "").ToList();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private static string BuildCreateTable(TableSchema schema)
    {
        var columnDefs = schema.Columns.Select(c =>
            $"[{c.Name}] {GetSqliteType(c.Type)}");
        return $"CREATE TABLE [{schema.TableName}] ({string.Join(", ", columnDefs)})";
    }

    private static string GetSqliteType(ColumnType type) => type switch
    {
        ColumnType.Byte or ColumnType.SByte or ColumnType.Int16 or
            ColumnType.UInt16 or ColumnType.Int32 or ColumnType.UInt32 => "INTEGER",
        ColumnType.Float => "REAL",
        ColumnType.String => "TEXT",
        _ => "TEXT"
    };

    private static object ConvertForSqlite(object value, ColumnType type)
    {
        // SQLite only has INTEGER, REAL, TEXT, BLOB
        // We store all integer types as long, floats as double
        return type switch
        {
            ColumnType.Byte => Convert.ToInt64(value),
            ColumnType.SByte => Convert.ToInt64(value),
            ColumnType.Int16 => Convert.ToInt64(value),
            ColumnType.UInt16 => Convert.ToInt64(value),
            ColumnType.Int32 => Convert.ToInt64(value),
            ColumnType.UInt32 => Convert.ToInt64(value),
            ColumnType.Float => Convert.ToDouble(value),
            ColumnType.String => value.ToString()!,
            _ => value
        };
    }

    private static object ConvertFromSqlite(object value, ColumnType type) => type switch
    {
        ColumnType.Byte => Convert.ToByte(value),
        ColumnType.SByte => Convert.ToSByte(value),
        ColumnType.Int16 => Convert.ToInt16(value),
        ColumnType.UInt16 => Convert.ToUInt16(value),
        ColumnType.Int32 => Convert.ToInt32(value),
        ColumnType.UInt32 => Convert.ToUInt32(value),
        ColumnType.Float => Convert.ToSingle(value),
        ColumnType.String => value.ToString()!,
        _ => value
    };
}
