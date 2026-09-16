using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Fiesta.Collab.Core.Models;
using Fiesta.Collab.Core.Providers;
using Fiesta.Collab.Shn.Crypto;

namespace Fiesta.Collab.Shn;

public sealed class ShnDataProvider : IDataProvider
{
    // SHN files use EUC-KR (code page 949) for Korean strings
    private static readonly Encoding ShnEncoding;

    static ShnDataProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ShnEncoding = Encoding.GetEncoding(949);
    }

    private readonly IShnCrypto _crypto;
    private readonly ILogger<ShnDataProvider> _logger;

    public ShnDataProvider(IShnCrypto crypto, ILogger<ShnDataProvider> logger)
    {
        _crypto = crypto;
        _logger = logger;
    }

    public string FormatId => "shn";
    public IReadOnlyList<string> SupportedExtensions => [".shn"];

    public bool CanHandle(string filePath)
    {
        if (!Path.GetExtension(filePath).Equals(".shn", StringComparison.OrdinalIgnoreCase))
            return false;

        var fi = new FileInfo(filePath);
        if (fi.Length < 36)
            return false;

        using var fs = File.OpenRead(filePath);
        fs.Seek(32, SeekOrigin.Begin);
        Span<byte> buf = stackalloc byte[4];
        fs.ReadExactly(buf);
        var dataLength = BitConverter.ToUInt32(buf);

        return dataLength == (ulong)fi.Length;
    }

    public Task<IReadOnlyList<TableEntry>> ReadAsync(string filePath, CancellationToken ct = default)
    {
        var tableName = Path.GetFileNameWithoutExtension(filePath);
        _logger.LogDebug("Reading SHN file {FilePath}", filePath);

        byte[] cryptHeader;
        byte[] decrypted;

        using (var file = File.OpenRead(filePath))
        using (var reader = new BinaryReader(file))
        {
            cryptHeader = reader.ReadBytes(32);
            int dataLength = reader.ReadInt32() - 36;
            decrypted = reader.ReadBytes(dataLength);
            _crypto.Crypt(decrypted, 0, dataLength);
        }

        using var stream = new MemoryStream(decrypted);
        using var reader2 = new BinaryReader(stream, ShnEncoding, leaveOpen: true);

        uint header = reader2.ReadUInt32();
        uint recordCount = reader2.ReadUInt32();
        uint defaultRecordLength = reader2.ReadUInt32();
        uint columnCount = reader2.ReadUInt32();

        var columns = ReadColumns(reader2, columnCount);
        ValidateRecordLength(columns, defaultRecordLength);
        var rows = ReadRows(reader2, columns, recordCount, defaultRecordLength);

        var schema = new TableSchema
        {
            TableName = tableName,
            SourceFormat = FormatId,
            Columns = columns.Select(c => c.Definition).ToList(),
            Metadata = new Dictionary<string, object>
            {
                ["cryptHeader"] = Convert.ToBase64String(cryptHeader),
                ["header"] = header,
                ["defaultRecordLength"] = defaultRecordLength
            }
        };

        IReadOnlyList<TableEntry> result = [new TableEntry { Schema = schema, Rows = rows }];
        return Task.FromResult(result);
    }

    public Task WriteAsync(string filePath, IReadOnlyList<TableEntry> tables, CancellationToken ct = default)
    {
        var data = tables[0];
        _logger.LogDebug("Writing SHN file {FilePath}", filePath);

        var metadata = data.Schema.Metadata
                       ?? throw new InvalidOperationException("Cannot write SHN without metadata (missing cryptHeader/header)");

        var cryptHeader = Convert.FromBase64String(GetMetadataString(metadata, "cryptHeader"));
        var header = GetMetadataUInt32(metadata, "header");

        using var content = new MemoryStream();
        using (var writer = new BinaryWriter(content, ShnEncoding, leaveOpen: true))
        {
            var columns = data.Schema.Columns;
            uint defaultRecordLength = CalculateDefaultRecordLength(columns);

            writer.Write(header);
            writer.Write((uint)data.Rows.Count);
            writer.Write(defaultRecordLength);
            writer.Write((uint)columns.Count);

            WriteColumns(writer, columns);
            WriteRows(writer, columns, data.Rows, defaultRecordLength);
        }

        var encrypted = content.ToArray();
        _crypto.Crypt(encrypted, 0, encrypted.Length);

        using var output = File.Create(filePath);
        using var finalWriter = new BinaryWriter(output);
        finalWriter.Write(cryptHeader);
        finalWriter.Write(encrypted.Length + 36);
        finalWriter.Write(encrypted);

        return Task.CompletedTask;
    }

    private static List<ShnColumnInfo> ReadColumns(BinaryReader reader, uint count)
    {
        var columns = new List<ShnColumnInfo>((int)count);
        int unkCount = 0;

        for (int i = 0; i < count; i++)
        {
            string name = ReadPaddedString(reader, 48);
            uint typeCode = reader.ReadUInt32();
            int length = reader.ReadInt32();

            string columnName;
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2)
            {
                columnName = $"Undefined{unkCount}";
                unkCount++;
            }
            else
            {
                columnName = name;
            }

            columns.Add(new ShnColumnInfo
            {
                Definition = new ColumnDefinition
                {
                    Name = columnName,
                    Type = MapShnType(typeCode, length),
                    Length = length,
                    SourceTypeCode = (int)typeCode,
                    SourceName = columnName == name ? null : name
                },
                TypeCode = typeCode
            });
        }

        return columns;
    }

    private static void ValidateRecordLength(List<ShnColumnInfo> columns, uint defaultRecordLength)
    {
        uint computed = 2; // row length prefix
        foreach (var col in columns)
            computed += (uint)col.Definition.Length;

        if (computed != defaultRecordLength)
            throw new InvalidDataException(
                $"Computed record length {computed} does not match default {defaultRecordLength}");
    }

    private static List<Dictionary<string, object?>> ReadRows(
        BinaryReader reader, List<ShnColumnInfo> columns, uint recordCount, uint defaultRecordLength)
    {
        var rows = new List<Dictionary<string, object?>>((int)recordCount);

        for (uint i = 0; i < recordCount; i++)
        {
            ushort rowLength = reader.ReadUInt16();
            var row = new Dictionary<string, object?>(columns.Count);

            foreach (var col in columns)
            {
                // Strings and floats are the only type-driven reads. Everything else is an integer of the
                // width the HEADER declares, because the width is not a property of the type code: 29 is
                // 8 bytes in the 2016 tables and 4 in the 2026 ones. An unrecognised code is read the same
                // way rather than throwing - a numeric column of a known width can always be read, and
                // refusing one means a newer client cannot be opened at all.
                object value = col.TypeCode switch
                {
                    9 or 10 or 24 => ReadPaddedString(reader, col.Definition.Length),
                    26 => ReadNullTerminatedString(reader),
                    5 => reader.ReadSingle(),
                    _ => ReadInteger(reader, col.Definition.Length, IsSigned(col.TypeCode), col.TypeCode)
                };
                row[col.Definition.Name] = value;
            }

            rows.Add(row);
        }

        return rows;
    }


    /// <summary>Type codes whose values are signed; every other numeric code is unsigned.</summary>
    private static bool IsSigned(uint typeCode) => typeCode is 13 or 20 or 21 or 22;

    /// <summary>
    /// A little-endian integer of exactly <paramref name="length"/> bytes, returned in the narrowest type
    /// that holds it so values compare and round-trip as themselves.
    /// </summary>
    private static object ReadInteger(BinaryReader reader, int length, bool signed, uint typeCode)
    {
        if (length is <= 0 or > 8)
            throw new InvalidDataException($"SHN column type {typeCode} has an unusable width of {length}");

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException($"SHN column type {typeCode}: wanted {length} bytes, got {bytes.Length}");

        ulong raw = 0;
        for (var i = length - 1; i >= 0; i--) raw = (raw << 8) | bytes[i];

        if (!signed)
            return length switch { 1 => (byte)raw, 2 => (ushort)raw, <= 4 => (uint)raw, _ => raw };

        // Sign-extend from the declared width.
        var shift = 64 - length * 8;
        var v = (long)(raw << shift) >> shift;
        return length switch { 1 => (sbyte)v, 2 => (short)v, <= 4 => (int)v, _ => v };
    }

    private static void WriteColumns(BinaryWriter writer, IReadOnlyList<ColumnDefinition> columns)
    {
        foreach (var col in columns)
        {
            string nameToWrite = col.SourceName ?? (col.Name.StartsWith("Undefined") ? " " : col.Name);
            WritePaddedString(writer, nameToWrite, 48);
            writer.Write((uint)(col.SourceTypeCode ?? throw new InvalidOperationException(
                $"Column {col.Name} missing SourceTypeCode, cannot write SHN")));
            writer.Write(col.Length);
        }
    }

    private static void WriteRows(
        BinaryWriter writer,
        IReadOnlyList<ColumnDefinition> columns,
        IReadOnlyList<Dictionary<string, object?>> rows,
        uint defaultRecordLength)
    {
        foreach (var row in rows)
        {
            long rowStart = writer.BaseStream.Position;
            short varLength = 0;
            writer.Write((short)0); // placeholder for row length

            foreach (var col in columns)
            {
                var value = row[col.Name];
                int typeCode = col.SourceTypeCode!.Value;

                switch (typeCode)
                {
                    case 1 or 12 or 16:
                        writer.Write(ConvertToByte(value));
                        break;
                    case 2:
                        writer.Write(ConvertToUInt16(value));
                        break;
                    case 3 or 11 or 18 or 27:
                        writer.Write(ConvertToUInt32(value));
                        break;
                    case 5:
                        writer.Write(ConvertToSingle(value));
                        break;
                    case 9 or 10 or 24:
                        WritePaddedString(writer, value?.ToString() ?? "", col.Length);
                        break;
                    case 13 or 21:
                        writer.Write(ConvertToInt16(value));
                        break;
                    case 20:
                        writer.Write(ConvertToSByte(value));
                        break;
                    case 22:
                        writer.Write(ConvertToInt32(value));
                        break;
                    case 26:
                        WriteNullTerminatedString(writer, value?.ToString() ?? "", ref varLength);
                        break;
                    case 29:
                        writer.Write(ConvertToUInt64(value));
                        break;
                }
            }

            long rowEnd = writer.BaseStream.Position;
            writer.BaseStream.Seek(rowStart, SeekOrigin.Begin);
            writer.Write((short)(defaultRecordLength + varLength));
            writer.BaseStream.Seek(rowEnd, SeekOrigin.Begin);
        }
    }

    private static string ReadPaddedString(BinaryReader reader, int length)
    {
        var buffer = reader.ReadBytes(length);
        int end = 0;
        while (end < length && buffer[end] != 0x00) end++;
        return end > 0 ? LosslessEucKr.GetString(buffer, 0, end) : string.Empty;
    }

    private static string ReadNullTerminatedString(BinaryReader reader)
    {
        long start = reader.BaseStream.Position;
        while (reader.ReadByte() != 0x00) { }
        int len = (int)(reader.BaseStream.Position - start - 1);
        if (len <= 0) return string.Empty;
        reader.BaseStream.Position = start;
        var result = LosslessEucKr.GetString(reader.ReadBytes(len));
        reader.ReadByte(); // consume the null terminator
        return result;
    }

    private static void WritePaddedString(BinaryWriter writer, string value, int length)
    {
        var bytes = LosslessEucKr.GetBytes(value);
        if (bytes.Length > length)
            throw new ArgumentOutOfRangeException(nameof(value),
                $"String '{value}' ({bytes.Length} bytes) exceeds padded length {length}");

        writer.Write(bytes);
        for (int i = bytes.Length; i < length; i++)
            writer.Write((byte)0);
    }

    /// <summary>
    /// The project column type for a SHN column. Strings and floats come from the type code; every other
    /// column is an integer whose type follows the header's DECLARED WIDTH, because the width is not a
    /// property of the code - 29 is 8 bytes in the 2016 tables and 4 in the 2026 ones.
    ///
    /// An unrecognised code maps by width too rather than throwing. Refusing one means a whole table is
    /// lost (MobLoca died on "Unknown SHN type code 28"), and a numeric column of a known width is always
    /// readable and writable.
    /// </summary>
    private static ColumnType MapShnType(uint typeCode, int length) => typeCode switch
    {
        9 or 10 or 24 or 26 => ColumnType.String,
        5 => ColumnType.Float,
        _ when IsSigned(typeCode) => length switch
        {
            1 => ColumnType.SByte,
            2 => ColumnType.Int16,
            _ => ColumnType.Int32,
        },
        _ => length switch
        {
            1 => ColumnType.Byte,
            2 => ColumnType.UInt16,
            <= 4 => ColumnType.UInt32,
            _ => ColumnType.UInt64,
        }
    };

    private static uint CalculateDefaultRecordLength(IReadOnlyList<ColumnDefinition> columns)
    {
        uint length = 2; // row length prefix
        foreach (var col in columns)
            length += (uint)col.Length;
        return length;
    }

    private static void WriteNullTerminatedString(BinaryWriter writer, string value, ref short varLength)
    {
        var bytes = LosslessEucKr.GetBytes(value);
        writer.Write(bytes);
        writer.Write((byte)0x00);
        varLength += (short)bytes.Length; // extra bytes beyond the 1-byte column length
    }

    // A value may be held signed even where the column is unsigned: these tables are written with
    // negatives against a WORD column (an NPC facing, a level difference), and the model keeps the sign
    // so a .txt round trip can write it back as the file stated it. A binary slot has no sign to keep,
    // so it wraps here, at the last moment - Convert.ToUInt16 would throw and GetUInt32 would too.
    private static long AsLong(JsonElement je) => je.ValueKind == JsonValueKind.String
        ? long.Parse(je.GetString()!) : je.TryGetInt64(out long l) ? l : (long)je.GetUInt64();

    private static byte ConvertToByte(object? v) => v is JsonElement je ? unchecked((byte)AsLong(je)) : unchecked((byte)Convert.ToInt64(v));
    private static sbyte ConvertToSByte(object? v) => v is JsonElement je ? je.ValueKind == JsonValueKind.String ? sbyte.Parse(je.GetString()!) : (sbyte)je.GetInt32() : Convert.ToSByte(v);
    private static ushort ConvertToUInt16(object? v) => v is JsonElement je ? unchecked((ushort)AsLong(je)) : unchecked((ushort)Convert.ToInt64(v));
    private static short ConvertToInt16(object? v) => v is JsonElement je ? je.ValueKind == JsonValueKind.String ? short.Parse(je.GetString()!) : (short)je.GetInt32() : Convert.ToInt16(v);
    private static uint ConvertToUInt32(object? v) => v is JsonElement je ? unchecked((uint)AsLong(je)) : unchecked((uint)Convert.ToInt64(v));
    private static int ConvertToInt32(object? v) => v is JsonElement je ? je.ValueKind == JsonValueKind.String ? int.Parse(je.GetString()!) : je.GetInt32() : Convert.ToInt32(v);
    private static float ConvertToSingle(object? v) => v is JsonElement je ? je.ValueKind == JsonValueKind.String ? float.Parse(je.GetString()!) : je.GetSingle() : Convert.ToSingle(v);
    private static ulong ConvertToUInt64(object? v) => v is JsonElement je ? je.ValueKind == JsonValueKind.String ? ulong.Parse(je.GetString()!) : je.GetUInt64() : Convert.ToUInt64(v);

    private static string GetMetadataString(Dictionary<string, object> metadata, string key)
    {
        if (metadata.TryGetValue(key, out var val))
        {
            if (val is JsonElement je) return je.GetString()!;
            return val.ToString()!;
        }
        throw new InvalidOperationException($"Missing SHN metadata key: {key}");
    }

    private static uint GetMetadataUInt32(Dictionary<string, object> metadata, string key)
    {
        if (metadata.TryGetValue(key, out var val))
        {
            if (val is JsonElement je) return je.GetUInt32();
            return Convert.ToUInt32(val);
        }
        throw new InvalidOperationException($"Missing SHN metadata key: {key}");
    }

    private sealed class ShnColumnInfo
    {
        public required ColumnDefinition Definition { get; init; }
        public required uint TypeCode { get; init; }
    }
}
