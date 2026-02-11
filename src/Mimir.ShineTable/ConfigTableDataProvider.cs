using Microsoft.Extensions.Logging;
using Mimir.Core.Models;
using Mimir.Core.Providers;

namespace Mimir.ShineTable;

/// <summary>
/// Data provider for the #DEFINE/#ENDDEFINE text format ("configtable").
/// Handles reading and writing config-style text tables (ServerInfo, DefaultCharacterData, etc.).
/// </summary>
public sealed class ConfigTableDataProvider : IDataProvider
{
    private readonly ILogger<ConfigTableDataProvider> _logger;

    public ConfigTableDataProvider(ILogger<ConfigTableDataProvider> logger)
    {
        _logger = logger;
    }

    public string FormatId => "configtable";
    public IReadOnlyList<string> SupportedExtensions => [".txt"];

    public bool CanHandle(string filePath) =>
        TextFormatDetector.Detect(filePath) == TextFormat.Define;

    public Task<IReadOnlyList<TableEntry>> ReadAsync(string filePath, CancellationToken ct = default)
    {
        _logger.LogDebug("Reading config table file {FilePath}", filePath);

        var lines = File.ReadAllLines(filePath);
        var tables = ConfigTableFormatParser.Parse(filePath, lines);

        if (tables.Count == 0)
            _logger.LogWarning("No tables found in {FilePath}", filePath);

        IReadOnlyList<TableEntry> result = tables;
        return Task.FromResult(result);
    }

    public Task WriteAsync(string filePath, IReadOnlyList<TableEntry> tables, CancellationToken ct = default)
    {
        if (tables.Count == 0) return Task.CompletedTask;

        _logger.LogDebug("Writing config table file {FilePath}", filePath);

        var lines = ConfigTableFormatParser.Write(tables);
        File.WriteAllLines(filePath, lines);
        return Task.CompletedTask;
    }
}
