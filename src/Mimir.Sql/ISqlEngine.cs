using Mimir.Core.Models;

namespace Mimir.Sql;

/// <summary>
/// SQL query engine backed by SQLite. Loads TableData into in-memory
/// tables with proper schemas and supports arbitrary SQL queries.
/// </summary>
public interface ISqlEngine : IDisposable
{
    /// <summary>
    /// Load a table into the SQLite database. Creates the table with
    /// the correct column types and inserts all rows.
    /// </summary>
    void LoadTable(TableData data);

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
    TableData ExtractTable(TableSchema schema);

    /// <summary>
    /// List all table names currently loaded.
    /// </summary>
    IReadOnlyList<string> ListTables();
}
