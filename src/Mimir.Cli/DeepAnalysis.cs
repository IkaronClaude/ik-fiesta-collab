using System.Text;
using Mimir.Shn.Crypto;

namespace Mimir.Cli;

/// <summary>
/// Deep analysis across all SHN files:
/// - Signedness check for integer types (any negative values? any values exceeding signed max?)
/// - String empty patterns: which columns use '-' vs '' when "not set"
/// - Per-type summary with all column usages (up to 20 examples)
/// </summary>
public static class DeepAnalysis
{
    public static void Run(string shineDir, string? outputPath)
    {
        var crypto = new ShnCrypto();
        var columns = new List<ColumnStats>();

        foreach (var file in Directory.EnumerateFiles(shineDir, "*.shn", SearchOption.AllDirectories))
        {
            try
            {
                AnalyzeFile(file, crypto, columns);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"SKIP {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        using var writer = outputPath != null
            ? new StreamWriter(outputPath, false, Encoding.UTF8)
            : new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8);

        WriteSignednessReport(writer, columns);
        WriteStringReport(writer, columns);
        WriteTypeReport(writer, columns);

        if (outputPath != null)
            Console.WriteLine($"Deep analysis written to {outputPath}");
    }

    private static void WriteSignednessReport(StreamWriter w, List<ColumnStats> columns)
    {
        w.WriteLine("==========================================================");
        w.WriteLine("  SIGNEDNESS CHECK");
        w.WriteLine("  Types 20 (byte-sized), 21 (16-bit), 22 (32-bit)");
        w.WriteLine("  If these are signed: expect negative values.");
        w.WriteLine("  Also checking 1/2/3/11 for any high-bit values.");
        w.WriteLine("==========================================================");
        w.WriteLine();

        // Integer types to check
        uint[] intTypes = [1, 2, 3, 11, 12, 13, 16, 20, 21, 22, 27, 29];

        foreach (uint typeCode in intTypes)
        {
            var typeCols = columns.Where(c => c.TypeCode == typeCode).ToList();
            if (typeCols.Count == 0) continue;

            w.WriteLine($"--- Type {typeCode} (0x{typeCode:X2}) - {typeCols.Count} columns across all files ---");

            long globalMin = long.MaxValue;
            long globalMax = long.MinValue;
            bool anyNegative = false;

            foreach (var col in typeCols)
            {
                if (col.IntMin < globalMin) globalMin = col.IntMin;
                if (col.IntMax > globalMax) globalMax = col.IntMax;
                if (col.HasNegative) anyNegative = true;
            }

            w.WriteLine($"  Global range: [{globalMin}, {globalMax}]");
            w.WriteLine($"  Any negative values: {anyNegative}");

            if (anyNegative)
            {
                w.WriteLine($"  SIGNED - columns with negatives:");
                foreach (var col in typeCols.Where(c => c.HasNegative).Take(10))
                    w.WriteLine($"    {col.FileName}.{col.ColumnName}: min={col.IntMin} max={col.IntMax} (rows={col.RowCount})");
            }

            // Check for values that would overflow signed range
            bool exceedsSignedByte = typeCode is 1 or 12 or 16 or 20 && globalMax > 127;
            bool exceedsSignedU16 = typeCode is 2 or 13 or 21 && globalMax > 32767;
            bool exceedsSignedU32 = typeCode is 3 or 11 or 22 or 27 && globalMax > 2_147_483_647;

            if (exceedsSignedByte)
                w.WriteLine($"  ** Values > 127 found - UNSIGNED byte range used");
            if (exceedsSignedU16)
                w.WriteLine($"  ** Values > 32767 found - UNSIGNED 16-bit range used");
            if (exceedsSignedU32)
                w.WriteLine($"  ** Values > 2,147,483,647 found - UNSIGNED 32-bit range used");

            // Show columns with extreme values
            var extremes = typeCols
                .Where(c => c.IntMax > 0)
                .OrderByDescending(c => c.IntMax)
                .Take(5)
                .ToList();
            if (extremes.Count > 0)
            {
                w.WriteLine($"  Top 5 highest-value columns:");
                foreach (var col in extremes)
                    w.WriteLine($"    {col.FileName}.{col.ColumnName}: max={col.IntMax}");
            }

            w.WriteLine();
        }
    }

    private static void WriteStringReport(StreamWriter w, List<ColumnStats> columns)
    {
        w.WriteLine("==========================================================");
        w.WriteLine("  STRING EMPTY VALUE PATTERNS");
        w.WriteLine("  '-' = likely a key/index (dash means 'none')");
        w.WriteLine("  '' = likely free text (empty means 'none')");
        w.WriteLine("==========================================================");
        w.WriteLine();

        uint[] stringTypes = [9, 10, 24, 26];

        foreach (uint typeCode in stringTypes)
        {
            var typeCols = columns.Where(c => c.TypeCode == typeCode).ToList();
            if (typeCols.Count == 0) continue;

            var dashCols = typeCols.Where(c => c.DashCount > 0).ToList();
            var emptyCols = typeCols.Where(c => c.EmptyCount > 0 && c.DashCount == 0).ToList();
            var neitherCols = typeCols.Where(c => c.EmptyCount == 0 && c.DashCount == 0).ToList();

            w.WriteLine($"--- Type {typeCode} (0x{typeCode:X2}) - {typeCols.Count} columns ---");
            w.WriteLine($"  Uses '-' as empty: {dashCols.Count} columns (KEY/INDEX pattern)");
            w.WriteLine($"  Uses '' as empty:  {emptyCols.Count} columns (TEXT pattern)");
            w.WriteLine($"  Never empty:       {neitherCols.Count} columns");
            w.WriteLine();

            if (dashCols.Count > 0)
            {
                w.WriteLine($"  KEY/INDEX columns (using '-'):");
                foreach (var col in dashCols.Take(20))
                    w.WriteLine($"    {col.FileName}.{col.ColumnName} (len={col.Length}, dash={col.DashCount}/{col.RowCount}, distinct={col.DistinctStringCount})");
                w.WriteLine();
            }

            if (emptyCols.Count > 0)
            {
                w.WriteLine($"  TEXT columns (using ''):");
                foreach (var col in emptyCols.Take(20))
                    w.WriteLine($"    {col.FileName}.{col.ColumnName} (len={col.Length}, empty={col.EmptyCount}/{col.RowCount}, distinct={col.DistinctStringCount})");
                w.WriteLine();
            }
        }
    }

    private static void WriteTypeReport(StreamWriter w, List<ColumnStats> columns)
    {
        w.WriteLine("==========================================================");
        w.WriteLine("  FULL TYPE CODE REPORT (20 examples per type)");
        w.WriteLine("==========================================================");
        w.WriteLine();

        var byType = columns.GroupBy(c => c.TypeCode).OrderBy(g => g.Key);

        foreach (var group in byType)
        {
            var usages = group.ToList();
            var lengths = usages.Select(u => u.Length).Distinct().OrderBy(l => l).ToList();

            w.WriteLine($"Type {group.Key} (0x{group.Key:X2}) - {usages.Count} columns, lengths: [{string.Join(", ", lengths)}]");

            foreach (var col in usages.Take(20))
            {
                w.Write($"  {col.FileName}.{col.ColumnName} (len={col.Length}, rows={col.RowCount}");
                if (col.IsIntType)
                    w.Write($", range=[{col.IntMin},{col.IntMax}]");
                if (col.IsStringType)
                    w.Write($", distinct={col.DistinctStringCount}, dash={col.DashCount}, empty={col.EmptyCount}");
                w.WriteLine(")");

                foreach (var sample in col.Samples.Take(3))
                    w.WriteLine($"    hex={sample.Hex}  {sample.Interpretation}");
            }
            w.WriteLine();
        }
    }

    private static void AnalyzeFile(string filePath, ShnCrypto crypto, List<ColumnStats> allColumns)
    {
        byte[] decrypted;
        using (var file = File.OpenRead(filePath))
        using (var reader = new BinaryReader(file))
        {
            reader.ReadBytes(32);
            int dataLength = reader.ReadInt32() - 36;
            if (dataLength <= 0) return;
            decrypted = reader.ReadBytes(dataLength);
            crypto.Crypt(decrypted, 0, dataLength);
        }

        using var stream = new MemoryStream(decrypted);
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        r.ReadUInt32(); // header
        uint recordCount = r.ReadUInt32();
        uint defaultRecordLength = r.ReadUInt32();
        uint columnCount = r.ReadUInt32();

        var colDefs = new List<(string name, uint typeCode, int length)>();
        for (int i = 0; i < columnCount; i++)
        {
            var nameBytes = r.ReadBytes(48);
            int end = 0;
            while (end < 48 && nameBytes[end] != 0) end++;
            string name = end > 0 ? Encoding.UTF8.GetString(nameBytes, 0, end).Trim() : $"Unk{i}";
            uint typeCode = r.ReadUInt32();
            int length = r.ReadInt32();
            colDefs.Add((name, typeCode, length));
        }

        string fileName = Path.GetFileNameWithoutExtension(filePath);
        var stats = colDefs.Select(c => new ColumnStats
        {
            FileName = fileName,
            ColumnName = c.name,
            TypeCode = c.typeCode,
            Length = c.length
        }).ToList();

        // Read ALL rows
        for (uint row = 0; row < recordCount; row++)
        {
            if (stream.Position >= stream.Length) break;
            ushort rowLength = r.ReadUInt16();

            for (int col = 0; col < colDefs.Count; col++)
            {
                var (_, typeCode, length) = colDefs[col];
                var s = stats[col];

                switch (typeCode)
                {
                    case 1 or 12 or 16:
                    {
                        byte v = r.ReadByte();
                        s.TrackInt(v);
                        if (row < 3) s.AddSample(new[] { v });
                        break;
                    }
                    case 2:
                    {
                        var bytes = r.ReadBytes(2);
                        ushort v = BitConverter.ToUInt16(bytes);
                        s.TrackInt(v);
                        if (row < 3) s.AddSample(bytes);
                        break;
                    }
                    case 3 or 11 or 18 or 27:
                    {
                        var bytes = r.ReadBytes(4);
                        uint v = BitConverter.ToUInt32(bytes);
                        s.TrackInt(v);
                        if (row < 3) s.AddSample(bytes);
                        break;
                    }
                    case 5:
                    {
                        var bytes = r.ReadBytes(4);
                        if (row < 3) s.AddSample(bytes);
                        break;
                    }
                    case 9 or 10 or 24:
                    {
                        var bytes = r.ReadBytes(length);
                        int strEnd = 0;
                        while (strEnd < length && bytes[strEnd] != 0) strEnd++;
                        string str = strEnd > 0 ? Encoding.UTF8.GetString(bytes, 0, strEnd) : string.Empty;
                        s.TrackString(str);
                        if (row < 3) s.AddSample(bytes);
                        break;
                    }
                    case 13 or 21:
                    {
                        var bytes = r.ReadBytes(2);
                        short v = BitConverter.ToInt16(bytes);
                        s.TrackInt(v);
                        if (row < 3) s.AddSample(bytes);
                        break;
                    }
                    case 20:
                    {
                        byte raw = r.ReadByte();
                        sbyte v = (sbyte)raw;
                        s.TrackInt(v);
                        if (row < 3) s.AddSample(new[] { raw });
                        break;
                    }
                    case 22:
                    {
                        var bytes = r.ReadBytes(4);
                        int v = BitConverter.ToInt32(bytes);
                        s.TrackInt(v);
                        if (row < 3) s.AddSample(bytes);
                        break;
                    }
                    case 26:
                    {
                        // Null-terminated variable-length string
                        long start = stream.Position;
                        while (r.ReadByte() != 0) { }
                        int len = (int)(stream.Position - start - 1);
                        stream.Position = start;
                        var bytes = r.ReadBytes(len);
                        r.ReadByte(); // null terminator
                        string str = len > 0 ? Encoding.UTF8.GetString(bytes) : string.Empty;
                        s.TrackString(str);
                        if (row < 3) s.AddSample(bytes);
                        break;
                    }
                    case 29:
                    {
                        var bytes = r.ReadBytes(8);
                        ulong v = BitConverter.ToUInt64(bytes);
                        // Track as long (may lose top bit, but fine for analysis)
                        s.TrackInt((long)v);
                        if (row < 3) s.AddSample(bytes);
                        break;
                    }
                    default:
                    {
                        // Unknown type - skip based on length
                        var bytes = r.ReadBytes(length);
                        if (row < 3) s.AddSample(bytes);
                        break;
                    }
                }
            }
        }

        allColumns.AddRange(stats);
    }

    private class ColumnStats
    {
        public required string FileName { get; init; }
        public required string ColumnName { get; init; }
        public required uint TypeCode { get; init; }
        public required int Length { get; init; }

        public int RowCount { get; private set; }

        // Integer tracking
        public long IntMin { get; private set; } = long.MaxValue;
        public long IntMax { get; private set; } = long.MinValue;
        public bool HasNegative { get; private set; }

        // String tracking
        public int EmptyCount { get; private set; }
        public int DashCount { get; private set; }
        public int DistinctStringCount => _distinctStrings?.Count ?? 0;
        private HashSet<string>? _distinctStrings;

        // Samples (first 3 rows)
        public List<SampleData> Samples { get; } = [];

        public bool IsIntType => TypeCode is 1 or 2 or 3 or 11 or 12 or 13 or 16 or 18 or 20 or 21 or 22 or 27 or 29;
        public bool IsStringType => TypeCode is 9 or 10 or 24 or 26;

        public void TrackInt(long value)
        {
            RowCount++;
            if (value < IntMin) IntMin = value;
            if (value > IntMax) IntMax = value;
            if (value < 0) HasNegative = true;
        }

        public void TrackString(string value)
        {
            RowCount++;
            _distinctStrings ??= [];
            _distinctStrings.Add(value);
            if (value == string.Empty) EmptyCount++;
            if (value == "-") DashCount++;
        }

        public void AddSample(byte[] bytes)
        {
            Samples.Add(new SampleData
            {
                Hex = Convert.ToHexString(bytes),
                Interpretation = Interpret(bytes, TypeCode)
            });
        }

        private static string Interpret(byte[] bytes, uint typeCode)
        {
            return typeCode switch
            {
                1 or 12 or 16 => $"byte={bytes[0]}",
                2 => $"u16={BitConverter.ToUInt16(bytes)} i16={BitConverter.ToInt16(bytes)}",
                3 or 11 or 18 or 27 when bytes.Length >= 4 =>
                    $"u32={BitConverter.ToUInt32(bytes)} i32={BitConverter.ToInt32(bytes)}",
                5 when bytes.Length >= 4 => $"f32={BitConverter.ToSingle(bytes)}",
                13 or 21 when bytes.Length >= 2 => $"i16={BitConverter.ToInt16(bytes)} u16={BitConverter.ToUInt16(bytes)}",
                20 => $"i8={(sbyte)bytes[0]} u8={bytes[0]}",
                22 when bytes.Length >= 4 => $"i32={BitConverter.ToInt32(bytes)} u32={BitConverter.ToUInt32(bytes)}",
                29 when bytes.Length >= 8 => $"u64={BitConverter.ToUInt64(bytes)} (0x{BitConverter.ToUInt64(bytes):X16})",
                9 or 10 or 24 or 26 => FormatString(bytes),
                _ => Convert.ToHexString(bytes)
            };
        }

        private static string FormatString(byte[] bytes)
        {
            int end = 0;
            while (end < bytes.Length && bytes[end] != 0) end++;
            return end > 0
                ? $"\"{Encoding.UTF8.GetString(bytes, 0, Math.Min(end, 60))}\""
                : "\"\"";
        }
    }

    private class SampleData
    {
        public required string Hex { get; init; }
        public required string Interpretation { get; init; }
    }
}
