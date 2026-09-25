using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Fiesta.Collab.Core.Constraints;
using Fiesta.Collab.Core.Models;

namespace Fiesta.Collab.Sql;

public sealed class SqlEngine : ISqlEngine
{
    /// <summary>The row-environment column every loaded table carries: a comma list of environments, NULL = every
    /// environment (docs/DESIGN-variants-and-native-steps.md, section 3). Not a schema column: ExtractTable returns it as
    /// TableEntry.RowEnvironments.</summary>
    public const string EnvsColumn = "_envs";

    private readonly SqliteConnection _connection;
    private readonly ILogger<SqlEngine> _logger;
    private List<ResolvedConstraint> _constraints = [];
    private Dictionary<string, TableKeyInfo> _tableKeys = [];

    public SqlEngine(ILogger<SqlEngine> logger)
    {
        _logger = logger;
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        SqlFunctions.Register(_connection);

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
        var envs = data.RowEnvironments;
        var insertSql = $"INSERT INTO [{schema.TableName}] ({string.Join(", ", columns.Select(c => $"[{c.Name}]"))}" +
                        (envs != null ? $", [{EnvsColumn}]" : "") +
                        $") VALUES ({string.Join(", ", paramNames)}{(envs != null ? ", @envs" : "")})";

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

        var envParam = cmd.CreateParameter();
        envParam.ParameterName = "@envs";
        if (envs != null) cmd.Parameters.Add(envParam);

        cmd.Prepare();

        var rowIndex = 0;
        foreach (var row in data.Rows)
        {
            if (envs != null)
                envParam.Value = rowIndex < envs.Count && envs[rowIndex] is { } e ? string.Join(",", e) : DBNull.Value;
            rowIndex++;
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
        // one pass over the reader by ordinal (a generic Query() dictionary per row, then a second typed copy, made
        // this the slowest part of a session edit: ~1 s for ItemInfo). Same values: a schema column missing from the
        // result or NULL is null, the rest go through ConvertFromSqlite.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT * FROM [{schema.TableName}]";
        using var reader = cmd.ExecuteReader();
        var ordinals = new Dictionary<string, int>(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
            ordinals[reader.GetName(i)] = i;
        var cols = schema.Columns.Select(c => (c.Name, c.Type, Ordinal: ordinals.TryGetValue(c.Name, out var o) ? o : -1)).ToArray();
        var envOrdinal = ordinals.TryGetValue(EnvsColumn, out var eo) ? eo : -1;

        var typedRows = new List<Dictionary<string, object?>>();
        var envs = new List<List<string>?>();
        var anyEnv = false;
        while (reader.Read())
        {
            var typedRow = new Dictionary<string, object?>(cols.Length);
            foreach (var (name, type, ordinal) in cols)
                typedRow[name] = ordinal < 0 || reader.IsDBNull(ordinal) ? null : ConvertFromSqlite(reader.GetValue(ordinal), type);
            typedRows.Add(typedRow);
            var env = envOrdinal < 0 || reader.IsDBNull(envOrdinal) ? null : reader.GetString(envOrdinal);
            if (string.IsNullOrWhiteSpace(env)) envs.Add(null);
            else
            {
                envs.Add(env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
                anyEnv = true;
            }
        }

        return new TableEntry { Schema = schema, Rows = typedRows, RowEnvironments = anyEnv ? envs : null };
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
        parts.Add($"[{EnvsColumn}] TEXT");

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
        // SQLite may return strings for numeric columns (e.g. shine table data that couldn't be parsed on load)
        if (value is string s)
        {
            return type switch
            {
                ColumnType.String => s,
                ColumnType.Byte when byte.TryParse(s, out var b) => b,
                ColumnType.Byte when sbyte.TryParse(s, out var sb2) => sb2,
                ColumnType.SByte when sbyte.TryParse(s, out var sb) => sb,
                ColumnType.Int16 when short.TryParse(s, out var i16) => i16,
                ColumnType.UInt16 when ushort.TryParse(s, out var u16) => u16,
                // same rule as the long branch: a negative against an unsigned column is kept, not wrapped
                ColumnType.UInt16 when short.TryParse(s, out var s16) => s16,
                ColumnType.Int32 when int.TryParse(s, out var i32) => i32,
                ColumnType.UInt32 when uint.TryParse(s, out var u32) => u32,
                ColumnType.UInt32 when int.TryParse(s, out var s32) => s32,
                ColumnType.UInt64 when ulong.TryParse(s, out var u64) => u64,
                ColumnType.Float when float.TryParse(s, out var f) => f,
                _ => s
            };
        }

        // SQLite returns Int64 for all integers - use unchecked casts to handle unsigned values.
        //
        // A value OUT OF RANGE for the column type is kept as it stands rather than wrapped. These
        // tables are written with values the declared type cannot hold - Field.txt states CanParty 1000
        // against a BYTE, a MobRegen group's RangeDegree is 4294967295 against a DWRD, which the parser
        // reads as Int32, ExpRecalculation's ByLevelDiff starts at -150 against a WORD, and every NPC.txt
        // facing that points that way is negative - and the 2016 loader wraps them itself. The parser
        // keeps what the file says on the way in; wrapping here on the way out threw it away again just
        // as permanently, so 1000 came back 232 and -150 came back 65386, and both were written back
        // that way: a party cap of 232, a level-difference penalty turned into a huge positive number,
        // and every negative-facing NPC turned around. A binary slot has no room for the excess, so the
        // SHN writer wraps at the point of writing instead.
        //
        // A NEGATIVE value keeps its sign even where the column type is unsigned. These tables are
        // written with negatives against a WORD column - ExpRecalculation's ByLevelDiff starts at -150,
        // and every NPC.txt facing is one - and the 2016 loader wraps them itself. The parser keeps the
        // sign on the way in; wrapping it here on the way out threw it away again just as permanently,
        // so -150 came back as 65386 and was written back as 65386, turning a level-difference penalty
        // into a huge positive number and moving every NPC that faced a negative direction.
        if (value is long l)
        {
            return type switch
            {
                ColumnType.Byte => l is >= byte.MinValue and <= byte.MaxValue ? (byte)l : l,
                ColumnType.SByte => l is >= sbyte.MinValue and <= sbyte.MaxValue ? (sbyte)l : l,
                ColumnType.Int16 => l is >= short.MinValue and <= short.MaxValue ? (short)l : l,
                ColumnType.UInt16 => l is >= ushort.MinValue and <= ushort.MaxValue ? (ushort)l : l,
                ColumnType.Int32 => l is >= int.MinValue and <= int.MaxValue ? (int)l : l,
                ColumnType.UInt32 => l is >= uint.MinValue and <= uint.MaxValue ? (uint)l : l,
                ColumnType.UInt64 => unchecked((ulong)l),
                ColumnType.Float => (float)l,
                ColumnType.String => l.ToString(),
                _ => l
            };
        }

        // SQLite returns Double for REAL columns
        if (value is double d)
        {
            return type switch
            {
                ColumnType.Float => (float)d,
                ColumnType.Byte => (byte)d,
                ColumnType.UInt16 => (ushort)d,
                ColumnType.UInt32 => (uint)d,
                ColumnType.Int32 => (int)d,
                ColumnType.String => d.ToString(),
                _ => d
            };
        }

        return type switch
        {
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
