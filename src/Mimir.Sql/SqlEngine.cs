using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Mimir.Core.Constraints;
using Mimir.Core.Models;

namespace Mimir.Sql;

public sealed class SqlEngine : ISqlEngine
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<SqlEngine> _logger;
    private List<ResolvedConstraint> _constraints = [];
    private Dictionary<string, TableKeyInfo> _tableKeys = [];

    public SqlEngine(ILogger<SqlEngine> logger)
    {
        _logger = logger;
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        Execute("PRAGMA foreign_keys = ON;");
        Execute("PRAGMA journal_mode = WAL;");
    }

    public void SetConstraints(IReadOnlyList<ResolvedConstraint> constraints, Dictionary<string, TableKeyInfo>? tableKeys = null)
    {
        _constraints = constraints.ToList();
        _tableKeys = tableKeys ?? [];
        // Disable FK enforcement during bulk load - re-enable via EnableForeignKeys()
        Execute("PRAGMA foreign_keys = OFF;");
        _logger.LogDebug("Registered {Count} constraints, FK enforcement deferred", constraints.Count);
    }

    public IReadOnlyList<string> GetLoadOrder(IEnumerable<string> tableNames)
    {
        var names = tableNames.ToHashSet();

        // Build dependency graph: sourceTable depends on targetTable
        var deps = new Dictionary<string, HashSet<string>>();
        foreach (var name in names)
            deps[name] = [];

        foreach (var c in _constraints)
        {
            if (names.Contains(c.SourceTable) && names.Contains(c.TargetTable) && c.SourceTable != c.TargetTable)
                deps[c.SourceTable].Add(c.TargetTable);
        }

        // DFS topological sort - dependencies added first
        var result = new List<string>();
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();

        void Visit(string node)
        {
            if (visited.Contains(node)) return;
            if (visiting.Contains(node))
            {
                _logger.LogWarning("Circular FK dependency involving {Table}, skipping cycle", node);
                return;
            }
            visiting.Add(node);
            if (deps.TryGetValue(node, out var dependencies))
                foreach (var dep in dependencies)
                    Visit(dep);
            visiting.Remove(node);
            visited.Add(node);
            result.Add(node);
        }

        foreach (var name in names.OrderBy(n => n))
            Visit(name);

        return result;
    }

    public void EnableForeignKeys()
    {
        // Create indexes for designated key/id columns before enabling FK checks
        foreach (var (tableName, keyInfo) in _tableKeys)
        {
            if (keyInfo.IdColumn is not null)
                TryCreateIndex(tableName, keyInfo.IdColumn, unique: true, "PK");

            if (keyInfo.KeyColumn is not null)
                TryCreateIndex(tableName, keyInfo.KeyColumn, unique: true, "Key");
        }

        Execute("PRAGMA foreign_keys = ON;");
        _logger.LogDebug("FK enforcement enabled");
    }

    private void TryCreateIndex(string table, string column, bool unique, string label)
    {
        var uniqueStr = unique ? "UNIQUE " : "";
        var indexName = $"idx_{table}_{column}";
        try
        {
            Execute($"CREATE {uniqueStr}INDEX [{indexName}] ON [{table}]([{column}])");
        }
        catch (SqliteException)
        {
            if (!unique) return;
            // Duplicates exist - fall back to non-unique index, FK enforcement will be best-effort
            _logger.LogWarning("{Table}.{Column} has duplicates, {Label} index is non-unique - FK checks may not enforce",
                table, column, label);
            try { Execute($"CREATE INDEX [{indexName}] ON [{table}]([{column}])"); }
            catch { /* table or index might not exist */ }
        }
    }

    public void LoadTable(TableEntry data)
    {
        var schema = data.Schema;
        _logger.LogDebug("Loading table {TableName} ({RowCount} rows)", schema.TableName, data.Rows.Count);

        // Resolve which columns have FK constraints + build CREATE TABLE
        var fkColumns = ResolveFkColumns(schema);
        var createSql = BuildCreateTable(schema, fkColumns);
        Execute(createSql);

        if (data.Rows.Count == 0) return;

        var columns = schema.Columns;

        // Build empty-value lookup: column index → set of values to map to NULL
        var emptyLookup = new Dictionary<int, HashSet<string>>();
        for (int i = 0; i < columns.Count; i++)
        {
            foreach (var fk in fkColumns.Where(f => f.SourceColumn == columns[i].Name && f.EmptyValues.Count > 0))
            {
                if (!emptyLookup.TryGetValue(i, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    emptyLookup[i] = set;
                }
                foreach (var ev in fk.EmptyValues)
                    set.Add(ev);
            }
        }

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
                var val = row.TryGetValue(colName, out var v) ? v : null;

                // Map empty values to NULL so FK checks skip them
                if (val is not null && emptyLookup.TryGetValue(i, out var emptySet)
                    && emptySet.Contains(val.ToString()!))
                {
                    val = null;
                }

                parameters[i].Value = val is not null
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

    public TableEntry ExtractTable(TableSchema schema)
    {
        var rows = Query($"SELECT * FROM [{schema.TableName}]");

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

        return new TableEntry { Schema = schema, Rows = typedRows };
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

    /// <summary>
    /// Matches resolved constraints against a table's actual columns,
    /// returning concrete (sourceColumn, targetTable, targetColumn, emptyValues) tuples.
    /// </summary>
    private List<FkColumnBinding> ResolveFkColumns(TableSchema schema)
    {
        var bindings = new List<FkColumnBinding>();

        foreach (var constraint in _constraints.Where(c => c.SourceTable == schema.TableName))
        {
            foreach (var col in schema.Columns)
            {
                if (DefinitionResolver.GlobMatch(col.Name, constraint.ColumnPattern))
                {
                    bindings.Add(new FkColumnBinding
                    {
                        SourceColumn = col.Name,
                        TargetTable = constraint.TargetTable,
                        TargetColumn = constraint.TargetColumn,
                        EmptyValues = constraint.EmptyValues
                    });
                }
            }
        }

        return bindings;
    }

    private static string BuildCreateTable(TableSchema schema, List<FkColumnBinding> fkColumns)
    {
        var parts = new List<string>();

        foreach (var col in schema.Columns)
            parts.Add($"[{col.Name}] {GetSqliteType(col.Type)}");

        foreach (var fk in fkColumns)
            parts.Add($"FOREIGN KEY ([{fk.SourceColumn}]) REFERENCES [{fk.TargetTable}]([{fk.TargetColumn}])");

        return $"CREATE TABLE [{schema.TableName}] ({string.Join(", ", parts)})";
    }

    private static string GetSqliteType(ColumnType type) => type switch
    {
        ColumnType.Byte or ColumnType.SByte or ColumnType.Int16 or
            ColumnType.UInt16 or ColumnType.Int32 or ColumnType.UInt32 or ColumnType.UInt64 => "INTEGER",
        ColumnType.Float => "REAL",
        ColumnType.String => "TEXT",
        _ => "TEXT"
    };

    private static object ConvertForSqlite(object value, ColumnType type)
    {
        // JSON deserialization produces JsonElement, not native types
        if (value is JsonElement je)
            return ConvertJsonElement(je, type);

        return type switch
        {
            ColumnType.Byte => Convert.ToInt64(value),
            ColumnType.SByte => Convert.ToInt64(value),
            ColumnType.Int16 => Convert.ToInt64(value),
            ColumnType.UInt16 => Convert.ToInt64(value),
            ColumnType.Int32 => Convert.ToInt64(value),
            ColumnType.UInt32 => Convert.ToInt64(value),
            ColumnType.UInt64 => (long)Convert.ToUInt64(value),
            ColumnType.Float => Convert.ToDouble(value),
            ColumnType.String => value.ToString()!,
            _ => value
        };
    }

    private static object ConvertJsonElement(JsonElement je, ColumnType type)
    {
        // Handle type mismatches: JSON strings for numeric columns, etc.
        if (je.ValueKind == JsonValueKind.String)
        {
            var s = je.GetString() ?? "";
            return type switch
            {
                ColumnType.String => s,
                ColumnType.Float when double.TryParse(s, out var d) => d,
                ColumnType.UInt64 when ulong.TryParse(s, out var u) => (long)u,
                _ when long.TryParse(s, out var l) => l,
                _ => s // fallback: store as text
            };
        }

        return type switch
        {
            ColumnType.Byte or ColumnType.SByte or ColumnType.Int16 or
                ColumnType.UInt16 or ColumnType.Int32 or ColumnType.UInt32 => je.GetInt64(),
            ColumnType.UInt64 => (long)je.GetUInt64(),
            ColumnType.Float => je.GetDouble(),
            ColumnType.String => je.ToString(),
            _ => je.ToString()
        };
    }

    private static object ConvertFromSqlite(object value, ColumnType type)
    {
        // SQLite may return strings for numeric columns (e.g. raw table data that couldn't be parsed on load)
        if (value is string s)
        {
            return type switch
            {
                ColumnType.String => s,
                ColumnType.Byte when byte.TryParse(s, out var b) => b,
                ColumnType.SByte when sbyte.TryParse(s, out var sb) => sb,
                ColumnType.Int16 when short.TryParse(s, out var i16) => i16,
                ColumnType.UInt16 when ushort.TryParse(s, out var u16) => u16,
                ColumnType.Int32 when int.TryParse(s, out var i32) => i32,
                ColumnType.UInt32 when uint.TryParse(s, out var u32) => u32,
                ColumnType.UInt64 when ulong.TryParse(s, out var u64) => u64,
                ColumnType.Float when float.TryParse(s, out var f) => f,
                _ => s
            };
        }

        return type switch
        {
            ColumnType.Byte => Convert.ToByte(value),
            ColumnType.SByte => Convert.ToSByte(value),
            ColumnType.Int16 => Convert.ToInt16(value),
            ColumnType.UInt16 => Convert.ToUInt16(value),
            ColumnType.Int32 => Convert.ToInt32(value),
            ColumnType.UInt32 => Convert.ToUInt32(value),
            ColumnType.UInt64 => Convert.ToUInt64(value),
            ColumnType.Float => Convert.ToSingle(value),
            ColumnType.String => value.ToString()!,
            _ => value
        };
    }

    private sealed class FkColumnBinding
    {
        public required string SourceColumn { get; init; }
        public required string TargetTable { get; init; }
        public required string TargetColumn { get; init; }
        public required List<string> EmptyValues { get; init; }
    }
}
