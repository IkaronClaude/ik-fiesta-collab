using Mimir.Core.Models;

namespace Mimir.Core.Providers;

/// <summary>
/// A data provider knows how to read and write a specific file format
/// (e.g. SHN, raw text tables). It converts between native file formats
/// and the common TableData representation used by the JSONL project.
/// </summary>
public interface IDataProvider
{
    /// <summary>
    /// Identifier for this provider (e.g. "shn", "rawtable").
    /// Matches the sourceFormat field in TableSchema.
    /// </summary>
    string FormatId { get; }

    /// <summary>
    /// File extensions this provider handles (e.g. [".shn"]).
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Read a native file into the common TableData format.
    /// </summary>
    Task<TableData> ReadAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Write TableData back to the native file format.
    /// Schema.Metadata and ColumnDefinition.SourceTypeCode are used
    /// to produce a byte-identical (or near-identical) output.
    /// </summary>
    Task WriteAsync(string filePath, TableData data, CancellationToken ct = default);
}
