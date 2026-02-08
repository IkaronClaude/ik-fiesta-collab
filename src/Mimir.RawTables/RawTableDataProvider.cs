using Microsoft.Extensions.Logging;
using Mimir.Core.Models;
using Mimir.Core.Providers;

namespace Mimir.RawTables;

public sealed class RawTableDataProvider : IDataProvider
{
    private readonly ILogger<RawTableDataProvider> _logger;

    public RawTableDataProvider(ILogger<RawTableDataProvider> logger)
    {
        _logger = logger;
    }

    public string FormatId => "rawtable";
    public IReadOnlyList<string> SupportedExtensions => [".txt"];

    public Task<IReadOnlyList<TableEntry>> ReadAsync(string filePath, CancellationToken ct = default)
    {
        _logger.LogDebug("Reading raw table file {FilePath}", filePath);

        var lines = File.ReadAllLines(filePath);
        var format = DetectFormat(lines);

        List<TableEntry> tables = format switch
        {
            TextFormat.Table => TableFormatParser.Parse(filePath, lines),
            TextFormat.Define => DefineFormatParser.Parse(filePath, lines),
            _ => throw new InvalidDataException($"Could not detect text format for {filePath}")
        };

        if (tables.Count == 0)
            _logger.LogWarning("No tables found in {FilePath}", filePath);

        IReadOnlyList<TableEntry> result = tables;
        return Task.FromResult(result);
    }

    public Task WriteAsync(string filePath, IReadOnlyList<TableEntry> tables, CancellationToken ct = default)
    {
        if (tables.Count == 0) return Task.CompletedTask;

        var format = tables[0].Schema.Metadata?.TryGetValue("format", out var fmt) == true
            ? fmt.ToString() : null;

        // TODO: Implement writers when needed
        throw new NotImplementedException($"Raw table writing not yet implemented (format: {format})");
    }

    private static TextFormat DetectFormat(string[] lines)
    {
        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';')) continue;

            if (trimmed.StartsWith("#DEFINE", StringComparison.OrdinalIgnoreCase))
                return TextFormat.Define;

            if (trimmed.StartsWith("#table", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("#ignore", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("#exchange", StringComparison.OrdinalIgnoreCase))
                return TextFormat.Table;
        }

        return TextFormat.Unknown;
    }

    private enum TextFormat { Unknown, Table, Define }
}
