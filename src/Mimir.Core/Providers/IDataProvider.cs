using Mimir.Core.Models;

namespace Mimir.Core.Providers;

/// <summary>
/// A data provider knows how to read and write a specific file format
/// (e.g. SHN, raw text tables). It converts between native file formats
/// and the common TableEntry representation used by the JSON project.
/// Returns a list because some formats (e.g. raw tables) store multiple
/// tables per file.
/// </summary>
public interface IDataProvider
{
    /// <summary>
    /// Identifier for this provider (e.g. "shn", "shinetable").
    /// Matches the sourceFormat field in TableSchema.
    /// </summary>
    string FormatId { get; }

    /// <summary>
    /// File extensions this provider handles (e.g. [".shn"]).
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Read a native file into one or more tables.
    /// </summary>
    Task<IReadOnlyList<TableEntry>> ReadAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Write one or more tables back to the native file format.
    /// Schema.Metadata and ColumnDefinition.SourceTypeCode are used
    /// to produce a byte-identical (or near-identical) output.
    /// </summary>
    Task WriteAsync(string filePath, IReadOnlyList<TableEntry> tables, CancellationToken ct = default);
}
