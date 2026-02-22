using System.Text;
using Microsoft.Extensions.Logging;
using Mimir.Core.Models;
using Mimir.Core.Providers;

namespace Mimir.ShineTable;

/// <summary>
/// Data provider for the #table/#columntype/#columnname/#record text format ("shinetable").
/// </summary>
public sealed class ShineTableDataProvider : IDataProvider
{
    // Shine table .txt files use EUC-KR (code page 949) for Korean strings
    internal static readonly Encoding TextEncoding;

    static ShineTableDataProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        TextEncoding = Encoding.GetEncoding(949);
    }

    private readonly ILogger<ShineTableDataProvider> _logger;

    public ShineTableDataProvider(ILogger<ShineTableDataProvider> logger)
    {
        _logger = logger;
    }

    public string FormatId => "shinetable";
    public IReadOnlyList<string> SupportedExtensions => [".txt"];

    public bool CanHandle(string filePath) =>
        TextFormatDetector.Detect(filePath) == TextFormat.Table;

    public Task<IReadOnlyList<TableEntry>> ReadAsync(string filePath, CancellationToken ct = default)
    {
        _logger.LogDebug("Reading shine table file {FilePath}", filePath);

        var lines = File.ReadAllLines(filePath, TextEncoding);
        var tables = ShineTableFormatParser.Parse(filePath, lines);

        if (tables.Count == 0)
            _logger.LogWarning("No tables found in {FilePath}", filePath);

        IReadOnlyList<TableEntry> result = tables;
        return Task.FromResult(result);
    }

    public Task WriteAsync(string filePath, IReadOnlyList<TableEntry> tables, CancellationToken ct = default)
    {
        if (tables.Count == 0) return Task.CompletedTask;

        _logger.LogDebug("Writing shine table file {FilePath}", filePath);

        var lines = ShineTableFormatParser.Write(tables);
        File.WriteAllLines(filePath, lines, TextEncoding);
        return Task.CompletedTask;
    }
}
