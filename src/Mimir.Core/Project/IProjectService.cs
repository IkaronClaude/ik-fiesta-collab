using Mimir.Core.Models;

namespace Mimir.Core.Project;

/// <summary>
/// Manages a Mimir project directory: loading/saving the manifest,
/// reading/writing table JSON files.
/// </summary>
public interface IProjectService
{
    Task<MimirProject> LoadProjectAsync(string projectDir, CancellationToken ct = default);
    Task SaveProjectAsync(string projectDir, MimirProject project, CancellationToken ct = default);
    Task WriteTableFileAsync(string projectDir, string relativePath, TableFile tableFile, CancellationToken ct = default);
    Task<TableFile> ReadTableFileAsync(string projectDir, string relativePath, CancellationToken ct = default);
}
