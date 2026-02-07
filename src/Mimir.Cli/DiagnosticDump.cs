using System.Text;
using Mimir.Shn.Crypto;

namespace Mimir.Cli;

public static class DiagnosticDump
{
    public static void DumpShnFile(string filePath)
    {
        var crypto = new ShnCrypto();

        byte[] cryptHeader;
        byte[] decrypted;

        using (var file = File.OpenRead(filePath))
        using (var reader = new BinaryReader(file))
        {
            cryptHeader = reader.ReadBytes(32);
            int dataLength = reader.ReadInt32() - 36;
            decrypted = reader.ReadBytes(dataLength);
            crypto.Crypt(decrypted, 0, dataLength);
        }

        using var stream = new MemoryStream(decrypted);
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        uint header = r.ReadUInt32();
        uint recordCount = r.ReadUInt32();
        uint defaultRecordLength = r.ReadUInt32();
        uint columnCount = r.ReadUInt32();

        Console.WriteLine($"=== {Path.GetFileName(filePath)} ===");
        Console.WriteLine($"Header: 0x{header:X8}  Records: {recordCount}  DefRecLen: {defaultRecordLength}  Columns: {columnCount}");
        Console.WriteLine();

        Console.WriteLine("--- Column Definitions ---");
        var columns = new List<(string name, uint typeCode, int length)>();
        for (int i = 0; i < columnCount; i++)
        {
            var nameBytes = r.ReadBytes(48);
            int end = 0;
            while (end < 48 && nameBytes[end] != 0) end++;
            string name = end > 0 ? Encoding.UTF8.GetString(nameBytes, 0, end) : "(empty)";

            uint typeCode = r.ReadUInt32();
            int length = r.ReadInt32();

            columns.Add((name, typeCode, length));
            Console.WriteLine($"  [{i}] Name=\"{name}\"  TypeCode={typeCode} (0x{typeCode:X2})  Length={length}");
        }

        Console.WriteLine();
        Console.WriteLine("--- First 5 rows (raw hex per column) ---");

        int rowsToDump = (int)Math.Min(recordCount, 5);
        for (int row = 0; row < rowsToDump; row++)
        {
            long rowStart = stream.Position;
            ushort rowLength = r.ReadUInt16();
            Console.WriteLine($"  Row {row} (length={rowLength}):");

            foreach (var (name, typeCode, length) in columns)
            {
                int readLen = length;

                // For variable-length string (type 26), compute actual length
                if (typeCode == 26)
                    readLen = (int)(rowLength - defaultRecordLength + 1);

                // For unknown types, just read based on declared length
                var bytes = r.ReadBytes(readLen);
                var hex = Convert.ToHexString(bytes);

                // Also try to interpret as common types for reference
                string interpretation = "";
                if (readLen == 1) interpretation = $" byte={bytes[0]}";
                else if (readLen == 2) interpretation = $" u16={BitConverter.ToUInt16(bytes)} i16={BitConverter.ToInt16(bytes)}";
                else if (readLen == 4) interpretation = $" u32={BitConverter.ToUInt32(bytes)} i32={BitConverter.ToInt32(bytes)} f={BitConverter.ToSingle(bytes)}";
                else if (readLen > 4)
                {
                    int strEnd = 0;
                    while (strEnd < readLen && bytes[strEnd] != 0) strEnd++;
                    if (strEnd > 0) interpretation = $" str=\"{Encoding.UTF8.GetString(bytes, 0, strEnd)}\"";
                }

                Console.WriteLine($"    {name} (type={typeCode}/0x{typeCode:X2}, len={readLen}): {hex}{interpretation}");
            }
        }

        Console.WriteLine();
    }
}
