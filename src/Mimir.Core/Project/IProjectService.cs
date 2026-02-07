using Mimir.Core.Models;

namespace Mimir.Core.Project;

/// <summary>
/// Manages a Mimir project directory: loading/saving the manifest,
/// reading/writing JSONL data files.
/// </summary>
public interface IProjectService
{
    /// <summary>
    /// Load the project manifest from a directory containing mimir.json.
    /// </summary>
    Task<MimirProject> LoadProjectAsync(string projectDir, CancellationToken ct = default);

    /// <summary>
    /// Save the project manifest to mimir.json.
    /// </summary>
    Task SaveProjectAsync(string projectDir, MimirProject project, CancellationToken ct = default);

    /// <summary>
    /// Write a table's data to its JSONL file within the project directory.
    /// </summary>
    Task WriteTableDataAsync(string projectDir, TableEntry entry, TableData data, CancellationToken ct = default);

    /// <summary>
    /// Read a table's data from its JSONL file.
    /// </summary>
    Task<TableData> ReadTableDataAsync(string projectDir, TableEntry entry, CancellationToken ct = default);
}
