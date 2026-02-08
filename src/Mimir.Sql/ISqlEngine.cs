using Mimir.Core.Constraints;
using Mimir.Core.Models;

namespace Mimir.Sql;

/// <summary>
/// SQL query engine backed by SQLite. Loads TableData into in-memory
/// tables with proper schemas and supports arbitrary SQL queries.
/// </summary>
public interface ISqlEngine : IDisposable
{
    /// <summary>
    /// Register resolved constraints and table key info. Disables FK enforcement during bulk load.
    /// Call <see cref="EnableForeignKeys"/> after loading all tables.
    /// </summary>
    void SetConstraints(IReadOnlyList<ResolvedConstraint> constraints, Dictionary<string, TableKeyInfo>? tableKeys = null);

    /// <summary>
    /// Returns table names in dependency order (referenced tables first)
    /// based on registered constraints.
    /// </summary>
    IReadOnlyList<string> GetLoadOrder(IEnumerable<string> tableNames);

    /// <summary>
    /// Re-enable FOREIGN KEY enforcement after bulk loading.
    /// </summary>
    void EnableForeignKeys();

    /// <summary>
    /// Load a table into the SQLite database. Creates the table with
    /// the correct column types (and FK clauses if constraints are set)
    /// and inserts all rows.
    /// </summary>
    void LoadTable(TableEntry data);

    /// <summary>
    /// Execute a non-query SQL statement (INSERT, UPDATE, DELETE, CREATE, etc.).
    /// Returns the number of rows affected.
    /// </summary>
    int Execute(string sql);

    /// <summary>
    /// Execute a SQL query and return results as a list of row dictionaries.
    /// </summary>
    List<Dictionary<string, object?>> Query(string sql);

    /// <summary>
    /// Extract all rows from a loaded table back into a TableData,
    /// using the original schema for round-tripping.
    /// </summary>
    TableEntry ExtractTable(TableSchema schema);

    /// <summary>
    /// List all table names currently loaded.
    /// </summary>
    IReadOnlyList<string> ListTables();
}
