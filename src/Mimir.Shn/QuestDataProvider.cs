using System.Text;
using Microsoft.Extensions.Logging;
using Mimir.Core.Models;
using Mimir.Core.Providers;

namespace Mimir.Shn;

public sealed class QuestDataProvider : IDataProvider
{
    private const int DefaultFixedDataSize = 678;
    private const int QuestIdOffset = 2;

    private static readonly Encoding EucKr;

    static QuestDataProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        EucKr = Encoding.GetEncoding(949);
    }

    private readonly ILogger<QuestDataProvider> _logger;

    public QuestDataProvider(ILogger<QuestDataProvider> logger)
    {
        _logger = logger;
    }

    public string FormatId => "questdata";
    public IReadOnlyList<string> SupportedExtensions => [".shn"];

    public bool CanHandle(string filePath)
    {
        if (!Path.GetExtension(filePath).Equals(".shn", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Path.GetFileNameWithoutExtension(filePath)
                 .Equals("QuestData", StringComparison.OrdinalIgnoreCase))
            return false;

        var fi = new FileInfo(filePath);
        if (fi.Length < 8)
            return false;

        using var fs = File.OpenRead(filePath);
        using var reader = new BinaryReader(fs);

        ushort version = reader.ReadUInt16();
        if (version == 0 || version > 100)
            return false;

        ushort questCount = reader.ReadUInt16();
        if (questCount == 0)
            return false;

        // First record must have a reasonable length (fixed region + at least 3 null terminators)
        ushort firstRecordLength = reader.ReadUInt16();
        return firstRecordLength >= 100;
    }

    public Task<IReadOnlyList<TableEntry>> ReadAsync(string filePath, CancellationToken ct = default)
    {
        var tableName = Path.GetFileNameWithoutExtension(filePath);
        _logger.LogDebug("Reading quest file {FilePath}", filePath);

        using var fs = File.OpenRead(filePath);
        using var reader = new BinaryReader(fs);

        ushort version = reader.ReadUInt16();
        ushort questCount = reader.ReadUInt16();

        // First pass: read all records as raw byte arrays
        var recordData = new List<byte[]>(questCount);
        for (int i = 0; i < questCount; i++)
        {
            ushort recordLength = reader.ReadUInt16();
            byte[] data = reader.ReadBytes(recordLength - 2);
            recordData.Add(data);
        }

        // Auto-detect fixed data size by finding script boundaries.
        // Scripts are 3 null-terminated text strings at the end of each record.
        // Since script text has no embedded nulls, we find the 3rd null from the
        // end of each record — that's the end of script1, at position fixedDataSize + len(s1).
        // The minimum such position across all records gives fixedDataSize (when s1 is empty).
        int fixedDataSize = int.MaxValue;
        foreach (var d in recordData)
        {
            int nullCount = 0;
            for (int i = d.Length - 1; i >= 0; i--)
            {
                if (d[i] == 0)
                {
                    nullCount++;
                    if (nullCount == 3)
                    {
                        fixedDataSize = Math.Min(fixedDataSize, i);
                        break;
                    }
                }
            }
        }

        _logger.LogDebug("Detected fixedDataSize={Size} for {File}",
            fixedDataSize, tableName);

        var columns = new List<ColumnDefinition>
        {
            new() { Name = "QuestID", Type = ColumnType.UInt16, Length = 2 },
            new() { Name = "FixedData", Type = ColumnType.String, Length = fixedDataSize * 2 },
            new() { Name = "StartScript", Type = ColumnType.String, Length = 0 },
            new() { Name = "InProgressScript", Type = ColumnType.String, Length = 0 },
            new() { Name = "FinishScript", Type = ColumnType.String, Length = 0 }
        };

        var rows = new List<Dictionary<string, object?>>(questCount);

        foreach (var data in recordData)
        {
            byte[] fixedData = data[..fixedDataSize];
            ushort questId = BitConverter.ToUInt16(fixedData, QuestIdOffset);
            string hexData = Convert.ToHexString(fixedData);

            byte[] scriptBytes = data[fixedDataSize..];
            var scripts = SplitNullTerminatedStrings(scriptBytes, 3);

            rows.Add(new Dictionary<string, object?>
            {
                ["QuestID"] = questId,
                ["FixedData"] = hexData,
                ["StartScript"] = scripts[0],
                ["InProgressScript"] = scripts[1],
                ["FinishScript"] = scripts[2]
            });
        }

        var schema = new TableSchema
        {
            TableName = tableName,
            SourceFormat = FormatId,
            Columns = columns,
            Metadata = new Dictionary<string, object>
            {
                ["version"] = version,
                ["fixedDataSize"] = fixedDataSize
            }
        };

        IReadOnlyList<TableEntry> result = [new TableEntry { Schema = schema, Rows = rows }];
        return Task.FromResult(result);
    }

    public Task WriteAsync(string filePath, IReadOnlyList<TableEntry> tables, CancellationToken ct = default)
    {
        var data = tables[0];
        _logger.LogDebug("Writing quest file {FilePath}", filePath);

        var metadata = data.Schema.Metadata
                       ?? throw new InvalidOperationException("Cannot write quest file without metadata (missing version)");

        ushort version = GetMetadataUInt16(metadata, "version");

        using var fs = File.Create(filePath);
        using var writer = new BinaryWriter(fs);

        int fixedDataSize = metadata.TryGetValue("fixedDataSize", out var fds)
            ? GetMetadataInt(fds)
            : DefaultFixedDataSize;

        writer.Write(version);
        writer.Write((ushort)data.Rows.Count);

        foreach (var row in data.Rows)
        {
            var hexData = row["FixedData"]?.ToString()
                          ?? throw new InvalidOperationException("FixedData is null");
            byte[] fixedData = Convert.FromHexString(hexData);

            // Patch QuestID into fixed data if the column value differs
            ushort questId = ConvertToUInt16(row["QuestID"]);
            BitConverter.GetBytes(questId).CopyTo(fixedData, QuestIdOffset);

            var s1 = EucKr.GetBytes(row["StartScript"]?.ToString() ?? "");
            var s2 = EucKr.GetBytes(row["InProgressScript"]?.ToString() ?? "");
            var s3 = EucKr.GetBytes(row["FinishScript"]?.ToString() ?? "");

            ushort recordLength = (ushort)(2 + fixedDataSize + s1.Length + 1 + s2.Length + 1 + s3.Length + 1);

            writer.Write(recordLength);
            writer.Write(fixedData);
            writer.Write(s1);
            writer.Write((byte)0);
            writer.Write(s2);
            writer.Write((byte)0);
            writer.Write(s3);
            writer.Write((byte)0);
        }

        return Task.CompletedTask;
    }

    private static List<string> SplitNullTerminatedStrings(byte[] data, int count)
    {
        var result = new List<string>(count);
        int offset = 0;

        for (int i = 0; i < count; i++)
        {
            int nullPos = Array.IndexOf(data, (byte)0, offset);
            if (nullPos < 0)
                nullPos = data.Length;

            int len = nullPos - offset;
            result.Add(len > 0 ? EucKr.GetString(data, offset, len) : string.Empty);
            offset = nullPos + 1;
        }

        return result;
    }

    private static ushort GetMetadataUInt16(Dictionary<string, object> metadata, string key)
    {
        if (metadata.TryGetValue(key, out var val))
        {
            if (val is System.Text.Json.JsonElement je) return (ushort)je.GetUInt32();
            return Convert.ToUInt16(val);
        }
        throw new InvalidOperationException($"Missing quest metadata key: {key}");
    }

    private static int GetMetadataInt(object val)
    {
        if (val is System.Text.Json.JsonElement je) return je.GetInt32();
        return Convert.ToInt32(val);
    }

    private static ushort ConvertToUInt16(object? v)
    {
        if (v is System.Text.Json.JsonElement je)
            return je.ValueKind == System.Text.Json.JsonValueKind.String
                ? ushort.Parse(je.GetString()!)
                : (ushort)je.GetUInt32();
        return Convert.ToUInt16(v);
    }
}
