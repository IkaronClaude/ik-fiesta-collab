using System.Text;
using Mimir.Shn.Crypto;

namespace Mimir.Cli;

public static class TypeAnalysis
{
    public static void AnalyzeAllTypes(string shineDir)
    {
        var crypto = new ShnCrypto();
        // typeCode -> list of (fileName, columnName, length, sampleValues)
        var typeUsages = new Dictionary<uint, List<TypeUsage>>();

        foreach (var file in Directory.EnumerateFiles(shineDir, "*.shn", SearchOption.AllDirectories))
        {
            try
            {
                AnalyzeFile(file, crypto, typeUsages);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SKIP {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Console.WriteLine("=== SHN Type Code Analysis ===");
        Console.WriteLine();

        foreach (var (typeCode, usages) in typeUsages.OrderBy(kv => kv.Key))
        {
            var lengths = usages.Select(u => u.Length).Distinct().OrderBy(l => l).ToList();
            Console.WriteLine($"Type {typeCode} (0x{typeCode:X2}) - used {usages.Count} times, lengths: [{string.Join(", ", lengths)}]");

            // Show up to 5 example columns
            foreach (var usage in usages.Take(5))
            {
                Console.WriteLine($"  {usage.FileName}.{usage.ColumnName} (len={usage.Length})");
                foreach (var sample in usage.Samples.Take(3))
                {
                    Console.WriteLine($"    hex={sample.Hex}  {sample.Interpretations}");
                }
            }
            Console.WriteLine();
        }
    }

    private static void AnalyzeFile(string filePath, ShnCrypto crypto,
        Dictionary<uint, List<TypeUsage>> typeUsages)
    {
        byte[] decrypted;
        using (var file = File.OpenRead(filePath))
        using (var reader = new BinaryReader(file))
        {
            reader.ReadBytes(32); // crypt header
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

        var columns = new List<(string name, uint typeCode, int length)>();
        for (int i = 0; i < columnCount; i++)
        {
            var nameBytes = r.ReadBytes(48);
            int end = 0;
            while (end < 48 && nameBytes[end] != 0) end++;
            string name = end > 0 ? Encoding.UTF8.GetString(nameBytes, 0, end).Trim() : $"Unk{i}";
            uint typeCode = r.ReadUInt32();
            int length = r.ReadInt32();
            columns.Add((name, typeCode, length));
        }

        // Read up to 10 rows to collect samples
        int rowsToRead = (int)Math.Min(recordCount, 10);
        var columnSamples = columns.Select(_ => new List<SampleValue>()).ToList();

        for (int row = 0; row < rowsToRead; row++)
        {
            ushort rowLength = r.ReadUInt16();

            for (int col = 0; col < columns.Count; col++)
            {
                var (_, typeCode, length) = columns[col];
                int readLen = length;
                if (typeCode == 26)
                    readLen = (int)(rowLength - defaultRecordLength + 1);

                var bytes = r.ReadBytes(readLen);
                columnSamples[col].Add(new SampleValue
                {
                    Hex = Convert.ToHexString(bytes),
                    Interpretations = Interpret(bytes, readLen)
                });
            }
        }

        string fileName = Path.GetFileNameWithoutExtension(filePath);
        for (int col = 0; col < columns.Count; col++)
        {
            var (name, typeCode, length) = columns[col];
            if (!typeUsages.ContainsKey(typeCode))
                typeUsages[typeCode] = [];

            typeUsages[typeCode].Add(new TypeUsage
            {
                FileName = fileName,
                ColumnName = name,
                Length = length,
                Samples = columnSamples[col]
            });
        }
    }

    private static string Interpret(byte[] bytes, int length)
    {
        var parts = new List<string>();

        if (length == 1)
            parts.Add($"byte={bytes[0]}");
        else if (length == 2)
            parts.Add($"u16={BitConverter.ToUInt16(bytes)} i16={BitConverter.ToInt16(bytes)}");
        else if (length == 4)
        {
            uint u32 = BitConverter.ToUInt32(bytes);
            int i32 = BitConverter.ToInt32(bytes);
            float f = BitConverter.ToSingle(bytes);
            parts.Add($"u32={u32} i32={i32} f={f}");

            // Check if it looks like a string
            int strEnd = 0;
            while (strEnd < length && bytes[strEnd] >= 0x20 && bytes[strEnd] < 0x7F) strEnd++;
            if (strEnd >= 2)
                parts.Add($"str=\"{Encoding.ASCII.GetString(bytes, 0, strEnd)}\"");
        }
        else if (length == 8)
        {
            ulong u64 = BitConverter.ToUInt64(bytes);
            parts.Add($"u64={u64}");
            uint lo = BitConverter.ToUInt32(bytes, 0);
            uint hi = BitConverter.ToUInt32(bytes, 4);
            parts.Add($"pair=({lo},{hi})");
        }
        else if (length > 4)
        {
            int strEnd = 0;
            while (strEnd < length && bytes[strEnd] != 0) strEnd++;
            if (strEnd > 0)
                parts.Add($"str=\"{Encoding.UTF8.GetString(bytes, 0, Math.Min(strEnd, 40))}\"");
        }

        return string.Join("  ", parts);
    }

    private class TypeUsage
    {
        public required string FileName { get; init; }
        public required string ColumnName { get; init; }
        public required int Length { get; init; }
        public required List<SampleValue> Samples { get; init; }
    }

    private class SampleValue
    {
        public required string Hex { get; init; }
        public required string Interpretations { get; init; }
    }
}
