using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Mimir.Shn.Crypto;
using Shouldly;
using Xunit;

namespace Mimir.Shn.Tests;

public class QuestDataProviderTests : IDisposable
{
    private static readonly Encoding EucKr;

    static QuestDataProviderTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        EucKr = Encoding.GetEncoding(949);
    }

    private readonly QuestDataProvider _provider;
    private readonly ShnDataProvider _shnProvider;
    private readonly string _tempDir;

    public QuestDataProviderTests()
    {
        _provider = new QuestDataProvider(NullLogger<QuestDataProvider>.Instance);
        _shnProvider = new ShnDataProvider(new ShnCrypto(), NullLogger<ShnDataProvider>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"mimir-quest-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void CanHandle_QuestFile_ReturnsTrue()
    {
        // Create a minimal valid quest binary: version=6, count=1, one record
        var path = WriteTempQuestFile("QuestData.shn", version: 6, quests: [
            MakeQuest(questId: 1, startScript: "", inProgressScript: "", finishScript: "")
        ]);

        _provider.CanHandle(path).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_StandardShn_ReturnsFalse()
    {
        // Create a mock standard SHN file (32 byte header + 4 byte length + minimal data)
        var path = Path.Combine(_tempDir, "ItemInfo.shn");
        var data = new byte[16]; // minimal decrypted: header(4) + recordCount(4) + recordLen(4) + colCount(4)
        var cryptHeader = new byte[32];
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(cryptHeader);
        bw.Write(data.Length + 36); // data length field = file size - 36 + 36
        bw.Write(data);
        File.WriteAllBytes(path, ms.ToArray());

        _provider.CanHandle(path).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_NonShnExtension_ReturnsFalse()
    {
        var path = Path.Combine(_tempDir, "data.txt");
        File.WriteAllBytes(path, new byte[100]);

        _provider.CanHandle(path).ShouldBeFalse();
    }

    [Fact]
    public async Task ReadAsync_ParsesHeader()
    {
        var path = WriteTempQuestFile("QuestData.shn", version: 6, quests: [
            MakeQuest(questId: 1),
            MakeQuest(questId: 2)
        ]);

        var tables = await _provider.ReadAsync(path);

        tables.Count.ShouldBe(1);
        var table = tables[0];
        table.Schema.TableName.ShouldBe("QuestData");
        table.Schema.SourceFormat.ShouldBe("questdata");
        table.Rows.Count.ShouldBe(2);
        table.Schema.Metadata.ShouldNotBeNull();
        table.Schema.Metadata!["version"].ShouldBe((ushort)6);
    }

    [Fact]
    public async Task ReadAsync_ExtractsQuestId()
    {
        var path = WriteTempQuestFile("QuestData.shn", version: 6, quests: [
            MakeQuest(questId: 42)
        ]);

        var tables = await _provider.ReadAsync(path);
        var row = tables[0].Rows[0];

        row["QuestID"].ShouldBe((ushort)42);
    }

    [Fact]
    public async Task ReadAsync_ExtractsScripts()
    {
        var path = WriteTempQuestFile("QuestData.shn", version: 6, quests: [
            MakeQuest(questId: 1,
                startScript: "SAY Hello\nEND",
                inProgressScript: "SAY Working\nEND",
                finishScript: "SAY Done\nEND"),
            MakeQuest(questId: 2) // empty scripts — needed for fixedDataSize auto-detection
        ]);

        var tables = await _provider.ReadAsync(path);
        var row = tables[0].Rows[0];

        row["StartScript"].ShouldBe("SAY Hello\nEND");
        row["InProgressScript"].ShouldBe("SAY Working\nEND");
        row["FinishScript"].ShouldBe("SAY Done\nEND");
    }

    [Fact]
    public async Task ReadAsync_FixedDataIsHexEncoded()
    {
        // Create quest with known bytes in fixed data region
        var fixedData = new byte[678];
        fixedData[0] = 0xAB;
        fixedData[1] = 0xCD;
        // QuestID at offset 2-3
        fixedData[2] = 0x05;
        fixedData[3] = 0x00;
        fixedData[677] = 0xFF;

        var path = WriteTempQuestFile("QuestData.shn", version: 6, quests: [
            MakeQuestRaw(fixedData, "", "", "")
        ]);

        var tables = await _provider.ReadAsync(path);
        var row = tables[0].Rows[0];

        var hexStr = row["FixedData"]!.ToString()!;
        hexStr.Length.ShouldBe(1356); // 678 * 2

        // Verify specific bytes
        hexStr[..4].ShouldBe("ABCD", StringCompareShould.IgnoreCase);
        hexStr[^2..].ShouldBe("FF", StringCompareShould.IgnoreCase);
    }

    [Fact]
    public async Task WriteAsync_RoundTrip_ByteIdentical()
    {
        var originalBytes = BuildQuestFile(version: 6, quests: [
            MakeQuest(questId: 1, startScript: "SAY Hello\nEND", inProgressScript: "", finishScript: "SAY Done\nEND"),
            MakeQuest(questId: 100, startScript: "", inProgressScript: "LINK 5\nEND", finishScript: ""),
            MakeQuest(questId: 9999, startScript: "IF Quest 1\nGOTO 2\nEND", inProgressScript: "SAY Wait\nEND", finishScript: "ACCEPT\nEND")
        ]);

        var inputPath = Path.Combine(_tempDir, "QuestData.shn");
        File.WriteAllBytes(inputPath, originalBytes);

        // Read
        var tables = await _provider.ReadAsync(inputPath);

        // Write
        var outputPath = Path.Combine(_tempDir, "QuestData_out.shn");
        await _provider.WriteAsync(outputPath, tables);

        // Compare
        var outputBytes = File.ReadAllBytes(outputPath);
        outputBytes.ShouldBe(originalBytes);
    }

    [Fact]
    public void ShnDataProvider_CanHandle_RejectsQuestFile()
    {
        var path = WriteTempQuestFile("QuestData.shn", version: 6, quests: [
            MakeQuest(questId: 1)
        ]);

        _shnProvider.CanHandle(path).ShouldBeFalse();
    }

    [Fact]
    public async Task ReadAsync_KoreanScripts_RoundTrip()
    {
        // Test EUC-KR script strings round-trip correctly
        var koreanScript = "SAY \uD55C\uAD6D\uC5B4\nEND"; // "SAY 한국어\nEND"

        var path = WriteTempQuestFile("QuestData.shn", version: 6, quests: [
            MakeQuest(questId: 1, startScript: koreanScript, inProgressScript: "", finishScript: ""),
            MakeQuest(questId: 2) // empty scripts — needed for fixedDataSize auto-detection
        ]);

        var tables = await _provider.ReadAsync(path);
        var row = tables[0].Rows[0];
        row["StartScript"].ShouldBe(koreanScript);

        // Write and re-read
        var outputPath = Path.Combine(_tempDir, "QuestData_kr.shn");
        await _provider.WriteAsync(outputPath, tables);

        var tables2 = await _provider.ReadAsync(outputPath);
        tables2[0].Rows[0]["StartScript"].ShouldBe(koreanScript);
    }

    // ── Helpers ──

    private record QuestRecord(byte[] FixedData, string StartScript, string InProgressScript, string FinishScript);

    private static QuestRecord MakeQuest(ushort questId = 0,
        string startScript = "", string inProgressScript = "", string finishScript = "")
    {
        var fixedData = new byte[678];
        BitConverter.GetBytes(questId).CopyTo(fixedData, 2); // QuestID at offset 2
        return new QuestRecord(fixedData, startScript, inProgressScript, finishScript);
    }

    private static QuestRecord MakeQuestRaw(byte[] fixedData, string startScript, string inProgressScript, string finishScript)
    {
        if (fixedData.Length != 678)
            throw new ArgumentException("Fixed data must be exactly 678 bytes");
        return new QuestRecord(fixedData, startScript, inProgressScript, finishScript);
    }

    private byte[] BuildQuestFile(ushort version, QuestRecord[] quests)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(version);
        bw.Write((ushort)quests.Length);

        foreach (var quest in quests)
        {
            var s1 = EucKr.GetBytes(quest.StartScript);
            var s2 = EucKr.GetBytes(quest.InProgressScript);
            var s3 = EucKr.GetBytes(quest.FinishScript);

            // length includes the 2-byte length field itself
            ushort recordLength = (ushort)(2 + quest.FixedData.Length + s1.Length + 1 + s2.Length + 1 + s3.Length + 1);
            bw.Write(recordLength);
            bw.Write(quest.FixedData);
            bw.Write(s1);
            bw.Write((byte)0);
            bw.Write(s2);
            bw.Write((byte)0);
            bw.Write(s3);
            bw.Write((byte)0);
        }

        return ms.ToArray();
    }

    private string WriteTempQuestFile(string fileName, ushort version, QuestRecord[] quests)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, BuildQuestFile(version, quests));
        return path;
    }
}
