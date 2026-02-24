using System.Text;
using Mimir.Shn.Crypto;

namespace Mimir.Cli;

/// <summary>
/// Standalone SHN file inspector — reads and displays SHN files without a Mimir project.
/// Useful for diagnosing row order, schema, and fidelity issues.
/// </summary>
public static class ShnInspectCommand
{
    private static readonly Encoding ShnEncoding;

    static ShnInspectCommand()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ShnEncoding = Encoding.GetEncoding(949);
    }

    // ── Public entry points ──────────────────────────────────────────────────

    public static void PrintSchema(string filePath)
    {
        var f = ReadColumnsOnly(filePath);
        Console.WriteLine($"File:    {filePath}");
        Console.WriteLine($"Records: {f.RecordCount}   Columns: {f.Columns.Count}   DefaultRecLen: {f.DefaultRecordLength}");
        Console.WriteLine();
        Console.WriteLine($"  {"#",-5} {"Name",-34} {"ShnType",-10} {"CType",-8} {"Len",5}");
        Console.WriteLine($"  {new string('-', 5)} {new string('-', 34)} {new string('-', 10)} {new string('-', 8)} {new string('-', 5)}");
        for (int i = 0; i < f.Columns.Count; i++)
        {
            var col = f.Columns[i];
            Console.WriteLine($"  {i,-5} {col.Name,-34} {col.TypeCode,-3} (0x{col.TypeCode:X2})   {MapType(col.TypeCode),-8} {col.Length,5}");
        }
    }

    public static void PrintRowCount(string filePath)
    {
        Console.WriteLine(ReadRecordCount(filePath));
    }

    /// <summary>Prints rows from the file. <paramref name="head"/> and <paramref name="tail"/> take
    /// priority over <paramref name="skip"/>/<paramref name="take"/>.</summary>
    public static void PrintRows(string filePath, int skip, int? take, int? head, int? tail)
    {
        var f = ReadFile(filePath);
        int total = f.Rows.Count;

        List<(int idx, object?[] row)> slice;
        if (tail.HasValue)
        {
            int start = Math.Max(0, total - tail.Value);
            slice = f.Rows.Skip(start).Select((r, i) => (start + i, r)).ToList();
        }
        else
        {
            int count = head ?? take ?? total;
            slice = f.Rows.Skip(skip).Take(count).Select((r, i) => (skip + i, r)).ToList();
        }

        Console.WriteLine($"File: {filePath}  ({total} rows)");

        if (slice.Count == 0)
        {
            Console.WriteLine("(no rows in range)");
            return;
        }

        // Narrow table: print horizontally; wide table: print as record cards
        if (f.Columns.Count <= 8)
            PrintHorizontal(f.Columns, slice);
        else
            PrintCards(f.Columns, slice);
    }

    public static void Diff(string fileA, string fileB, int maxDiffs = 20)
    {
        var a = ReadFile(fileA);
        var b = ReadFile(fileB);

        int pad = Math.Max(fileA.Length, fileB.Length) + 4;

        Console.WriteLine($"Left:  {fileA}");
        Console.WriteLine($"       {a.Columns.Count} columns, {a.RecordCount} rows, recLen={a.DefaultRecordLength}");
        Console.WriteLine($"Right: {fileB}");
        Console.WriteLine($"       {b.Columns.Count} columns, {b.RecordCount} rows, recLen={b.DefaultRecordLength}");
        Console.WriteLine();

        DiffSchema(a, b);
        DiffRows(a, b, maxDiffs);
    }

    public static void Decrypt(string filePath, string outputPath)
    {
        var crypto = new ShnCrypto();
        byte[] decrypted;
        using (var file = File.OpenRead(filePath))
        using (var reader = new BinaryReader(file))
        {
            reader.ReadBytes(32); // cryptHeader
            int dataLength = reader.ReadInt32() - 36;
            decrypted = reader.ReadBytes(dataLength);
            crypto.Crypt(decrypted, 0, dataLength);
        }

        File.WriteAllBytes(outputPath, decrypted);
        Console.WriteLine($"Decrypted {decrypted.Length:N0} bytes → {outputPath}");
    }

    // ── Schema diff ──────────────────────────────────────────────────────────

    private static void DiffSchema(ShnFile a, ShnFile b)
    {
        var aCols = a.Columns.Select(c => c.Name).ToHashSet();
        var bCols = b.Columns.Select(c => c.Name).ToHashSet();
        var onlyInA = a.Columns.Where(c => !bCols.Contains(c.Name)).ToList();
        var onlyInB = b.Columns.Where(c => !aCols.Contains(c.Name)).ToList();

        // Order of shared columns in each file
        var sharedInA = a.Columns.Where(c => bCols.Contains(c.Name)).Select(c => c.Name).ToList();
        var sharedInB = b.Columns.Where(c => aCols.Contains(c.Name)).Select(c => c.Name).ToList();
        bool orderMatch = sharedInA.SequenceEqual(sharedInB);

        if (onlyInA.Count == 0 && onlyInB.Count == 0 && orderMatch)
        {
            Console.WriteLine($"Schema: identical ({a.Columns.Count} columns)");
        }
        else
        {
            Console.WriteLine("Schema: DIFFERS");
            if (onlyInA.Count > 0)
                Console.WriteLine($"  Only in left  ({onlyInA.Count}): {string.Join(", ", onlyInA.Select(c => c.Name))}");
            if (onlyInB.Count > 0)
                Console.WriteLine($"  Only in right ({onlyInB.Count}): {string.Join(", ", onlyInB.Select(c => c.Name))}");
            if (!orderMatch)
            {
                Console.WriteLine($"  Shared column order: DIFFERS");
                for (int i = 0; i < Math.Min(sharedInA.Count, sharedInB.Count); i++)
                {
                    if (sharedInA[i] != sharedInB[i])
                    {
                        Console.WriteLine($"  First divergence at shared-col index {i}: left={sharedInA[i]}, right={sharedInB[i]}");
                        break;
                    }
                }
            }
        }
        Console.WriteLine();
    }

    // ── Row diff ─────────────────────────────────────────────────────────────

    private static void DiffRows(ShnFile a, ShnFile b, int maxDiffs)
    {
        // Build aligned column index pairs for shared columns
        var bColIndex = b.Columns
            .Select((col, idx) => (col.Name, idx))
            .ToDictionary(x => x.Name, x => x.idx);
        var shared = a.Columns
            .Select((col, aIdx) => bColIndex.TryGetValue(col.Name, out int bIdx)
                ? (col.Name, aIdx, bIdx) : ((string?, int, int))(null, 0, 0))
            .Where(x => x.Item1 != null)
            .Select(x => (name: x.Item1!, aIdx: x.Item2, bIdx: x.Item3))
            .ToList();

        int rowsToCompare = Math.Min(a.Rows.Count, b.Rows.Count);
        int diffCount = 0;
        int identicalCount = 0;

        for (int i = 0; i < rowsToCompare; i++)
        {
            var aRow = a.Rows[i];
            var bRow = b.Rows[i];

            var diffs = shared
                .Where(c => aRow[c.aIdx]?.ToString() != bRow[c.bIdx]?.ToString())
                .Select(c => (c.name, aVal: aRow[c.aIdx]?.ToString() ?? "", bVal: bRow[c.bIdx]?.ToString() ?? ""))
                .ToList();

            if (diffs.Count == 0) { identicalCount++; continue; }

            diffCount++;
            if (diffCount > maxDiffs) continue;

            // Heuristic: if most shared columns differ, it's likely a row reorder
            bool looksLikeReorder = diffs.Count >= shared.Count / 2;

            Console.WriteLine(looksLikeReorder
                ? $"  Row {i}: {diffs.Count}/{shared.Count} columns differ  [likely row reorder / different item]"
                : $"  Row {i}: {diffs.Count} column(s) differ");

            // Show up to 5 column diffs; for reorders show first col (usually the key)
            int toShow = looksLikeReorder ? 2 : Math.Min(diffs.Count, 5);
            foreach (var (name, aVal, bVal) in diffs.Take(toShow))
            {
                var av = Truncate(aVal, 35);
                var bv = Truncate(bVal, 35);
                Console.WriteLine($"    {name,-32} {av} → {bv}");
            }
            if (diffs.Count > toShow)
                Console.WriteLine($"    ... and {diffs.Count - toShow} more column(s)");
        }

        if (a.Rows.Count != b.Rows.Count)
        {
            int extra = Math.Abs(a.Rows.Count - b.Rows.Count);
            string side = a.Rows.Count > b.Rows.Count ? "left" : "right";
            Console.WriteLine($"  Row count: {a.Rows.Count} (left) vs {b.Rows.Count} (right) — {extra} extra row(s) in {side}");
        }

        Console.WriteLine();
        Console.WriteLine($"Summary: {identicalCount} identical, {diffCount} differing" +
            (a.Rows.Count != b.Rows.Count ? $", {Math.Abs(a.Rows.Count - b.Rows.Count)} extra rows" : "") +
            $"  (of {rowsToCompare} compared)");

        if (diffCount > maxDiffs)
            Console.WriteLine($"(showed first {maxDiffs}; use --max-diffs to show more)");
    }

    // ── Row display ──────────────────────────────────────────────────────────

    private static void PrintHorizontal(List<ShnColDef> cols, List<(int idx, object?[] row)> rows)
    {
        // Compute column widths
        var widths = cols.Select(c => c.Name.Length).ToArray();
        foreach (var (_, row) in rows)
            for (int c = 0; c < cols.Count; c++)
                widths[c] = Math.Max(widths[c], Math.Min(row[c]?.ToString()?.Length ?? 0, 24));

        // Header
        var header = string.Join(" │ ", cols.Select((c, i) => c.Name.PadRight(widths[i])));
        var sep = string.Join("─┼─", widths.Select(w => new string('─', w)));
        Console.WriteLine($"  {"Row",-5} │ {header}");
        Console.WriteLine($"  {"─────"} ┼─{sep}");

        foreach (var (idx, row) in rows)
        {
            var vals = cols.Select((c, i) => Truncate(row[i]?.ToString() ?? "", widths[i]).PadRight(widths[i]));
            Console.WriteLine($"  {idx,-5} │ {string.Join(" │ ", vals)}");
        }
    }

    private static void PrintCards(List<ShnColDef> cols, List<(int idx, object?[] row)> rows)
    {
        int nameWidth = cols.Max(c => c.Name.Length);
        foreach (var (idx, row) in rows)
        {
            Console.WriteLine($"  ─── Row {idx} ───────────────────────────────");
            for (int c = 0; c < cols.Count; c++)
            {
                var val = Truncate(row[c]?.ToString() ?? "", 60);
                Console.WriteLine($"    {cols[c].Name.PadRight(nameWidth)} = {val}");
            }
        }
    }

    // ── File reading ─────────────────────────────────────────────────────────

    private sealed class ShnFile
    {
        public uint Header { get; init; }
        public uint RecordCount { get; init; }
        public uint DefaultRecordLength { get; init; }
        public List<ShnColDef> Columns { get; init; } = [];
        public List<object?[]> Rows { get; init; } = [];
    }

    private sealed class ShnColumnsOnly
    {
        public uint RecordCount { get; init; }
        public uint DefaultRecordLength { get; init; }
        public List<ShnColDef> Columns { get; init; } = [];
    }

    private sealed record ShnColDef(string Name, uint TypeCode, int Length);

    private static uint ReadRecordCount(string filePath)
    {
        var crypto = new ShnCrypto();
        using var file = File.OpenRead(filePath);
        using var reader = new BinaryReader(file);
        reader.ReadBytes(32);
        int dataLength = reader.ReadInt32() - 36;
        // Cipher runs backwards from position dataLength-1 to 0, initial key=(byte)dataLength.
        // Key at each position depends only on position and previous key (not on data),
        // so we can fast-forward the key schedule to position 7 without reading the bulk data.
        byte key = (byte)dataLength;
        for (int i = dataLength - 1; i >= 8; i--)
        {
            byte nk = (byte)(i & 0x0F);
            nk = (byte)(nk + 0x55);
            nk ^= (byte)((byte)(i) * 11);
            nk ^= key;
            nk ^= 0xAA;
            key = nk;
        }
        var buf = reader.ReadBytes(8);
        for (int i = 7; i >= 0; i--)
        {
            buf[i] ^= key;
            byte nk = (byte)(i & 0x0F);
            nk = (byte)(nk + 0x55);
            nk ^= (byte)((byte)(i) * 11);
            nk ^= key;
            nk ^= 0xAA;
            key = nk;
        }
        using var ms = new MemoryStream(buf);
        using var r = new BinaryReader(ms);
        r.ReadUInt32(); // header
        return r.ReadUInt32(); // recordCount
    }

    private static ShnColumnsOnly ReadColumnsOnly(string filePath)
    {
        var crypto = new ShnCrypto();
        byte[] decrypted;
        using (var file = File.OpenRead(filePath))
        using (var reader = new BinaryReader(file))
        {
            reader.ReadBytes(32);
            int dataLength = reader.ReadInt32() - 36;
            decrypted = reader.ReadBytes(dataLength);
            crypto.Crypt(decrypted, 0, dataLength);
        }

        using var stream = new MemoryStream(decrypted);
        using var r = new BinaryReader(stream, ShnEncoding, leaveOpen: true);

        r.ReadUInt32(); // header
        uint recordCount = r.ReadUInt32();
        uint defaultRecordLength = r.ReadUInt32();
        uint columnCount = r.ReadUInt32();

        var cols = ReadColumnDefs(r, columnCount);

        return new ShnColumnsOnly
        {
            RecordCount = recordCount,
            DefaultRecordLength = defaultRecordLength,
            Columns = cols
        };
    }

    private static ShnFile ReadFile(string filePath)
    {
        var crypto = new ShnCrypto();
        byte[] decrypted;
        using (var file = File.OpenRead(filePath))
        using (var reader = new BinaryReader(file))
        {
            reader.ReadBytes(32);
            int dataLength = reader.ReadInt32() - 36;
            decrypted = reader.ReadBytes(dataLength);
            crypto.Crypt(decrypted, 0, dataLength);
        }

        using var stream = new MemoryStream(decrypted);
        using var r = new BinaryReader(stream, ShnEncoding, leaveOpen: true);

        uint header = r.ReadUInt32();
        uint recordCount = r.ReadUInt32();
        uint defaultRecordLength = r.ReadUInt32();
        uint columnCount = r.ReadUInt32();

        var cols = ReadColumnDefs(r, columnCount);
        var rows = ReadRowData(r, cols, recordCount);

        return new ShnFile
        {
            Header = header,
            RecordCount = recordCount,
            DefaultRecordLength = defaultRecordLength,
            Columns = cols,
            Rows = rows
        };
    }

    private static List<ShnColDef> ReadColumnDefs(BinaryReader r, uint count)
    {
        var cols = new List<ShnColDef>((int)count);
        int unkCount = 0;
        for (int i = 0; i < count; i++)
        {
            var nameBytes = r.ReadBytes(48);
            int end = 0;
            while (end < 48 && nameBytes[end] != 0) end++;
            string rawName = end > 0 ? ShnEncoding.GetString(nameBytes, 0, end) : "";
            string name = string.IsNullOrWhiteSpace(rawName) || rawName.Trim().Length < 2
                ? $"Undefined{unkCount++}"
                : rawName;
            uint typeCode = r.ReadUInt32();
            int length = r.ReadInt32();
            cols.Add(new ShnColDef(name, typeCode, length));
        }
        return cols;
    }

    private static List<object?[]> ReadRowData(BinaryReader r, List<ShnColDef> cols, uint count)
    {
        var rows = new List<object?[]>((int)count);
        for (uint i = 0; i < count; i++)
        {
            r.ReadUInt16(); // rowLength — type 26 is self-terminating, so not needed
            var row = new object?[cols.Count];
            for (int c = 0; c < cols.Count; c++)
            {
                var col = cols[c];
                row[c] = col.TypeCode switch
                {
                    1 or 12 or 16 => (object)r.ReadByte(),
                    2             => r.ReadUInt16(),
                    3 or 11 or 18 or 27 => r.ReadUInt32(),
                    5             => r.ReadSingle(),
                    9 or 10 or 24 => ReadPaddedString(r, col.Length),
                    13 or 21      => r.ReadInt16(),
                    20            => r.ReadSByte(),
                    22            => r.ReadInt32(),
                    26            => ReadNullTerminatedString(r),
                    29            => r.ReadUInt64(),
                    _ => throw new InvalidDataException(
                            $"Unknown SHN type {col.TypeCode} (0x{col.TypeCode:X2}) in column '{col.Name}'")
                };
            }
            rows.Add(row);
        }
        return rows;
    }

    private static string ReadPaddedString(BinaryReader r, int length)
    {
        var buf = r.ReadBytes(length);
        int end = 0;
        while (end < length && buf[end] != 0) end++;
        return end > 0 ? ShnEncoding.GetString(buf, 0, end) : "";
    }

    private static string ReadNullTerminatedString(BinaryReader r)
    {
        long start = r.BaseStream.Position;
        while (r.ReadByte() != 0) { }
        int len = (int)(r.BaseStream.Position - start - 1);
        if (len <= 0) return "";
        r.BaseStream.Position = start;
        var result = ShnEncoding.GetString(r.ReadBytes(len));
        r.ReadByte(); // consume null
        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string MapType(uint typeCode) => typeCode switch
    {
        1 or 12 or 16        => "Byte",
        2                    => "UInt16",
        3 or 11 or 18 or 27  => "UInt32",
        5                    => "Float",
        9 or 10 or 24 or 26  => "String",
        13 or 21             => "Int16",
        20                   => "SByte",
        22                   => "Int32",
        29                   => "UInt64",
        _                    => "Unknown"
    };

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "…" : s;
}
