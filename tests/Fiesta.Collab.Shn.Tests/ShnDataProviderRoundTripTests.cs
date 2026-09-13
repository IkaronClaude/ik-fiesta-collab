using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Fiesta.Collab.Shn.Crypto;
using Shouldly;
using Xunit;

namespace Fiesta.Collab.Shn.Tests;

/// <summary>
/// Byte-identical round-trips for two things the real client files contain and the provider used to lose:
/// column names shorter than two characters ("R", " ", "") and text bytes that are not valid EUC-KR
/// (the 2026 US client ships cp1252 names like "Pi\xF1ata").
/// </summary>
public class ShnDataProviderRoundTripTests : IDisposable
{
    private readonly ShnDataProvider _provider = new(new ShnCrypto(), NullLogger<ShnDataProvider>.Instance);
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"collab-shn-rt-{Guid.NewGuid():N}");

    public ShnDataProviderRoundTripTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    private static byte[] BuildShn((string name, uint type, int len)[] columns, byte[][] rows)
    {
        using var body = new MemoryStream();
        using (var w = new BinaryWriter(body, Encoding.Latin1, leaveOpen: true))
        {
            uint recLen = 2;
            foreach (var c in columns) recLen += (uint)c.len;
            w.Write(1818316900u);
            w.Write((uint)rows.Length);
            w.Write(recLen);
            w.Write((uint)columns.Length);
            foreach (var (name, type, len) in columns)
            {
                var nb = Encoding.Latin1.GetBytes(name);
                w.Write(nb);
                for (int i = nb.Length; i < 48; i++) w.Write((byte)0);
                w.Write(type);
                w.Write(len);
            }
            foreach (var r in rows)
            {
                w.Write((ushort)(r.Length + 2));
                w.Write(r);
            }
        }
        var enc = body.ToArray();
        new ShnCrypto().Crypt(enc, 0, enc.Length);
        using var file = new MemoryStream();
        file.Write(new byte[32]);
        file.Write(BitConverter.GetBytes(enc.Length + 36));
        file.Write(enc);
        return file.ToArray();
    }

    private async Task<byte[]> RoundTrip(byte[] original)
    {
        var src = Path.Combine(_tempDir, "in.shn");
        var dst = Path.Combine(_tempDir, "out.shn");
        await File.WriteAllBytesAsync(src, original);
        var tables = await _provider.ReadAsync(src);
        await _provider.WriteAsync(dst, tables);
        return await File.ReadAllBytesAsync(dst);
    }

    [Fact]
    public async Task ShortColumnNames_RoundTripByteIdentical()
    {
        // "R" and " " are real column names in AbStateMsg / ActiveSkillView / DiceDividind of the 2016 client.
        var original = BuildShn(
            [("ID", 2, 2), ("R", 9, 8), (" ", 2, 2), ("", 1, 1), ("Name", 9, 8)],
            [[1, 0, .. "abc\0\0\0\0\0"u8, 7, 0, 3, .. "xyz\0\0\0\0\0"u8]]);

        var written = await RoundTrip(original);

        written.ShouldBe(original);
    }

    [Fact]
    public async Task ShortColumnNames_StillGetDistinctLogicalNames()
    {
        var original = BuildShn([("ID", 2, 2), ("R", 9, 8), (" ", 2, 2), ("", 1, 1)], [[1, 0, .. "abc\0\0\0\0\0"u8, 7, 0, 3]]);
        var src = Path.Combine(_tempDir, "in.shn");
        await File.WriteAllBytesAsync(src, original);

        var table = (await _provider.ReadAsync(src))[0];

        table.Schema.Columns.Select(c => c.Name).ShouldBe(["ID", "Undefined0", "Undefined1", "Undefined2"]);
        table.Rows[0]["Undefined0"].ShouldBe("abc");
    }

    [Fact]
    public async Task NonEucKrBytes_RoundTripByteIdentical_AndSurviveJson()
    {
        // cp1252 "Pi\xF1ata" in a padded column and "K\xE4bbn" in a null-terminated column.
        var padded = new byte[10];
        new byte[] { (byte)'P', (byte)'i', 0xF1, (byte)'a', (byte)'t', (byte)'a' }.CopyTo(padded, 0);
        var varlen = new byte[] { (byte)'K', 0xE4, (byte)'b', (byte)'b', (byte)'n', 0 };
        var original = BuildShn([("ID", 2, 2), ("Name", 9, 10), ("Desc", 26, 1)], [[1, 0, .. padded, .. varlen]]);

        var written = await RoundTrip(original);
        written.ShouldBe(original);

        var src = Path.Combine(_tempDir, "in.shn");
        var name = (string)(await _provider.ReadAsync(src))[0].Rows[0]["Name"]!;
        var viaJson = JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(name));
        viaJson.ShouldBe(name);
    }
}

public class LosslessEucKrTests
{
    [Theory]
    [InlineData(new byte[] { 0x4B, 0xE4, 0x62, 0x62, 0x6E })]
    [InlineData(new byte[] { 0x50, 0x69, 0xF1, 0x61, 0x74, 0x61 })]
    [InlineData(new byte[] { 0x61, 0x94, 0x2E, 0x20 })]
    [InlineData(new byte[] { 0xF1 })]
    [InlineData(new byte[] { 0xC7, 0xD1, 0xB1, 0xDB })]
    public void GetBytes_InvertsGetString(byte[] bytes)
    {
        var s = LosslessEucKr.GetString(bytes);
        LosslessEucKr.GetBytes(s).ShouldBe(bytes, $"decoded as {string.Join(" ", s.Select(c => ((int)c).ToString("X4")))}");
    }
}
