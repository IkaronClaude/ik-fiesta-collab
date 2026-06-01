using Fiesta.Collab.Core.Models;

namespace Fiesta.Collab.Core.Project;

/// <summary>
/// Manages a fiesta project directory: loading/saving the manifest,
/// reading/writing table JSON files.
/// </summary>
public interface IProjectService
{
    Task<FiestaProject> LoadProjectAsync(string projectDir, CancellationToken ct = default);
    Task SaveProjectAsync(string projectDir, FiestaProject project, CancellationToken ct = default);
    Task WriteTableFileAsync(string projectDir, string relativePath, TableFile tableFile, CancellationToken ct = default);
    Task<TableFile> ReadTableFileAsync(string projectDir, string relativePath, CancellationToken ct = default);
}
