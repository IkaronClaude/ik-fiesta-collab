using Microsoft.Extensions.Logging;
using Mimir.Core.Models;
using Mimir.Core.Providers;

namespace Mimir.ShineTable;

public sealed class ShineTableDataProvider : IDataProvider
{
    private readonly ILogger<ShineTableDataProvider> _logger;

    public ShineTableDataProvider(ILogger<ShineTableDataProvider> logger)
    {
        _logger = logger;
    }

    public string FormatId => "shinetable";
    public IReadOnlyList<string> SupportedExtensions => [".txt"];

    public Task<IReadOnlyList<TableEntry>> ReadAsync(string filePath, CancellationToken ct = default)
    {
        _logger.LogDebug("Reading shine table file {FilePath}", filePath);

        var lines = File.ReadAllLines(filePath);
        var format = DetectFormat(lines);

        List<TableEntry> tables = format switch
        {
            TextFormat.Table => ShineTableFormatParser.Parse(filePath, lines),
            TextFormat.Define => ConfigTableFormatParser.Parse(filePath, lines),
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
            ? fmt?.ToString() : null;

        _logger.LogDebug("Writing shine table file {FilePath} (format: {Format})", filePath, format);

        var lines = format switch
        {
            "define" => ConfigTableFormatParser.Write(tables),
            _ => ShineTableFormatParser.Write(tables)
        };

        File.WriteAllLines(filePath, lines);
        return Task.CompletedTask;
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
