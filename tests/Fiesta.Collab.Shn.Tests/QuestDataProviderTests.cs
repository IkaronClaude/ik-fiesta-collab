using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Fiesta.Collab.Shn.Crypto;
using Shouldly;
using Xunit;

namespace Fiesta.Collab.Shn.Tests;

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

    // ── CanHandle edge cases ──

    [Fact]
    public void CanHandle_TooSmallFile_ReturnsFalse()
    {
        var path = Path.Combine(_tempDir, "QuestData.shn");
        File.WriteAllBytes(path, new byte[7]); // needs at least 8 bytes
        _provider.CanHandle(path).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_ZeroVersion_ReturnsFalse()
    {
        var bytes = BuildQuestFile(version: 0, quests: [MakeQuest(questId: 1)]);
        var path = Path.Combine(_tempDir, "QuestData.shn");
        File.WriteAllBytes(path, bytes);
        _provider.CanHandle(path).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_VersionAbove100_ReturnsFalse()
    {
        var bytes = BuildQuestFile(version: 101, quests: [MakeQuest(questId: 1)]);
        var path = Path.Combine(_tempDir, "QuestData.shn");
        File.WriteAllBytes(path, bytes);
        _provider.CanHandle(path).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_ZeroQuestCount_ReturnsFalse()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((ushort)6); // valid version
        bw.Write((ushort)0); // zero count
        var path = Path.Combine(_tempDir, "QuestData.shn");
        File.WriteAllBytes(path, ms.ToArray());
        _provider.CanHandle(path).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_ShortFirstRecord_ReturnsFalse()
    {
        // Record length < 100 should be rejected (not a quest file)
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((ushort)6);
        bw.Write((ushort)1);
        bw.Write((ushort)50); // record length includes the 2-byte length field itself
        bw.Write(new byte[48]);
        var path = Path.Combine(_tempDir, "QuestData.shn");
        File.WriteAllBytes(path, ms.ToArray());
        _provider.CanHandle(path).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_WrongFilename_ReturnsFalse()
    {
        var bytes = BuildQuestFile(version: 6, quests: [MakeQuest(questId: 1)]);
        var path = Path.Combine(_tempDir, "OtherData.shn");
        File.WriteAllBytes(path, bytes);
        _provider.CanHandle(path).ShouldBeFalse();
    }

    // ── ReadAsync edge cases ──

    [Fact]
    public async Task ReadAsync_SingleQuestAllEmptyScripts_Works()
    {
        // Single quest with all-empty scripts: fixedDataSize detection still works
        // (3 consecutive nulls at the end of the record data are the 3 script terminators)
        var path = WriteTempQuestFile("QuestData.shn", version: 6, quests: [
            MakeQuest(questId: 42)
        ]);

        var tables = await _provider.ReadAsync(path);
        tables[0].Rows.Count.ShouldBe(1);
        tables[0].Rows[0]["QuestID"].ShouldBe((ushort)42);
        tables[0].Rows[0]["StartScript"].ShouldBe(string.Empty);
        tables[0].Rows[0]["InProgressScript"].ShouldBe(string.Empty);
        tables[0].Rows[0]["FinishScript"].ShouldBe(string.Empty);
    }

    [Fact]
    public async Task ReadAsync_AllEmptyScripts_RoundTrip_ByteIdentical()
    {
        var originalBytes = BuildQuestFile(version: 6, quests: [
            MakeQuest(questId: 1),
            MakeQuest(questId: 2),
            MakeQuest(questId: 3),
        ]);
        var inputPath = Path.Combine(_tempDir, "QuestData.shn");
        File.WriteAllBytes(inputPath, originalBytes);

        var tables = await _provider.ReadAsync(inputPath);
        var outputPath = Path.Combine(_tempDir, "QuestData_out.shn");
        await _provider.WriteAsync(outputPath, tables);

        File.ReadAllBytes(outputPath).ShouldBe(originalBytes);
    }

    [Fact]
    public async Task ReadAsync_FixedDataSizeStoredInMetadata()
    {
        var path = WriteTempQuestFile("QuestData.shn", version: 6, quests: [
            MakeQuest(questId: 1)
        ]);

        var tables = await _provider.ReadAsync(path);
        var metadata = tables[0].Schema.Metadata!;
        metadata.ShouldContainKey("fixedDataSize");
        Convert.ToInt32(metadata["fixedDataSize"]).ShouldBe(678);
    }

    [Fact]
    public async Task ReadAsync_FixedDataPreservesAllBytes()
    {
        // Every byte in the fixed region should round-trip exactly
        var fixedData = new byte[678];
        for (int i = 0; i < fixedData.Length; i++)
            fixedData[i] = (byte)(i & 0xFF);
        // QuestID is at offset 2-3; set it so the ID column matches
        fixedData[2] = 7;
        fixedData[3] = 0;

        var path = WriteTempQuestFile("QuestData.shn", version: 6, quests: [
            MakeQuestRaw(fixedData, "", "", "")
        ]);

        var tables = await _provider.ReadAsync(path);
        var hexData = tables[0].Rows[0]["FixedData"]!.ToString()!;
        var roundTripped = Convert.FromHexString(hexData);
        roundTripped.ShouldBe(fixedData);
    }

    [Fact]
    public async Task WriteAsync_QuestIdPatchedIntoFixedData()
    {
        // If QuestID column is changed after read, WriteAsync should patch it into FixedData bytes
        var path = WriteTempQuestFile("QuestData.shn", version: 6, quests: [
            MakeQuest(questId: 42)
        ]);
        var tables = await _provider.ReadAsync(path);

        // Mutate QuestID on the row
        tables[0].Rows[0]["QuestID"] = (ushort)999;

        var outputPath = Path.Combine(_tempDir, "QuestData_patched.shn");
        await _provider.WriteAsync(outputPath, tables);

        // Re-read: QuestID should reflect the patched value
        var tables2 = await _provider.ReadAsync(outputPath);
        tables2[0].Rows[0]["QuestID"].ShouldBe((ushort)999);
    }

    [Fact]
    public async Task WriteAsync_MixedScriptLengths_RoundTrip_ByteIdentical()
    {
        // Multiple quests where some have scripts, some don't — fixedDataSize detection
        // uses the minimum position, so mixed lengths must still round-trip correctly
        var originalBytes = BuildQuestFile(version: 6, quests: [
            MakeQuest(questId: 1, startScript: "SAY 100 NPC\nACCEPT\nEND", inProgressScript: "SAY 101 NPC\nEND", finishScript: ""),
            MakeQuest(questId: 2),
            MakeQuest(questId: 3, startScript: "", inProgressScript: "", finishScript: "DONE\nEND"),
        ]);

        var inputPath = Path.Combine(_tempDir, "QuestData.shn");
        File.WriteAllBytes(inputPath, originalBytes);

        var tables = await _provider.ReadAsync(inputPath);
        var outputPath = Path.Combine(_tempDir, "QuestData_out.shn");
        await _provider.WriteAsync(outputPath, tables);

        File.ReadAllBytes(outputPath).ShouldBe(originalBytes);
    }

    // ── Decoded-field tests (offsets are in the "data" frame = record offset − 2) ──

    private static void SetU16(byte[] f, int off, ushort v) => BitConverter.GetBytes(v).CopyTo(f, off);

    [Fact]
    public async Task ReadAsync_DecodesStartNpc()
    {
        var fixedData = new byte[678];
        BitConverter.GetBytes((ushort)1).CopyTo(fixedData, 2);  // QuestID
        SetU16(fixedData, 28, 111);                             // StartNPC (record +30)

        var path = WriteTempQuestFile("QuestData.shn", version: 6, quests: [MakeQuestRaw(fixedData, "", "", "")]);
        var row = (await _provider.ReadAsync(path))[0].Rows[0];

        row["StartNPC"].ShouldBe((ushort)111);
    }

    [Fact]
    public async Task ReadAsync_DecodesMobs()
    {
        var fixedData = new byte[678];
        BitConverter.GetBytes((ushort)1).CopyTo(fixedData, 2);
        // mob[0] @ data+72: en=1, isNpc=1, id=29 (Julia), toKill=0, amt=0
        int mo = 72;
        fixedData[mo] = 1; fixedData[mo + 1] = 1; SetU16(fixedData, mo + 2, 29); fixedData[mo + 4] = 0; fixedData[mo + 5] = 0;
        // mob[1] @ data+78: en=1, isNpc=0, id=500, toKill=1, amt=10
        mo = 78;
        fixedData[mo] = 1; fixedData[mo + 1] = 0; SetU16(fixedData, mo + 2, 500); fixedData[mo + 4] = 1; fixedData[mo + 5] = 10;

        var path = WriteTempQuestFile("QuestData.shn", version: 6, quests: [MakeQuestRaw(fixedData, "", "", "")]);
        var row = (await _provider.ReadAsync(path))[0].Rows[0];

        var mobs = JsonDocument.Parse(row["Mobs"]!.ToString()!).RootElement;
        mobs.GetArrayLength().ShouldBe(2);
        mobs[0].GetProperty("IsNpc").GetBoolean().ShouldBeTrue();
        mobs[0].GetProperty("Id").GetUInt16().ShouldBe((ushort)29);
        mobs[1].GetProperty("Id").GetUInt16().ShouldBe((ushort)500);
        mobs[1].GetProperty("ToKill").GetBoolean().ShouldBeTrue();
        mobs[1].GetProperty("Amount").GetByte().ShouldBe((byte)10);
    }

    [Fact]
    public async Task ReadAsync_DecodesItemsAndRewards()
    {
        var fixedData = new byte[678];
        BitConverter.GetBytes((ushort)1).CopyTo(fixedData, 2);
        // item[0] @ data+102: en=1, type=5, id=3028, amt=10
        int io = 102;
        fixedData[io] = 1; fixedData[io + 1] = 5; SetU16(fixedData, io + 2, 3028); SetU16(fixedData, io + 4, 10);
        // reward[0] @ data+514: Fixed EXP 1234
        int ro = 514;
        fixedData[ro] = 1; fixedData[ro + 1] = 0; BitConverter.GetBytes((ulong)1234).CopyTo(fixedData, ro + 4);
        // reward[1] @ data+526: Choice Item id=777 count=1
        ro = 526;
        fixedData[ro] = 2; fixedData[ro + 1] = 2; SetU16(fixedData, ro + 4, 777); SetU16(fixedData, ro + 6, 1);

        var path = WriteTempQuestFile("QuestData.shn", version: 6, quests: [MakeQuestRaw(fixedData, "", "", "")]);
        var row = (await _provider.ReadAsync(path))[0].Rows[0];

        var items = JsonDocument.Parse(row["Items"]!.ToString()!).RootElement;
        items.GetArrayLength().ShouldBe(1);
        items[0].GetProperty("Id").GetUInt16().ShouldBe((ushort)3028);
        items[0].GetProperty("Amount").GetUInt16().ShouldBe((ushort)10);

        var rewards = JsonDocument.Parse(row["Rewards"]!.ToString()!).RootElement;
        rewards.GetArrayLength().ShouldBe(2);
        rewards[0].GetProperty("Type").GetString().ShouldBe("Exp");
        rewards[0].GetProperty("Amount").GetUInt64().ShouldBe(1234UL);
        rewards[1].GetProperty("Method").GetString().ShouldBe("Choice");
        rewards[1].GetProperty("ItemId").GetUInt16().ShouldBe((ushort)777);

        row["ExpReward"].ShouldBe(1234UL);
    }

    [Fact]
    public async Task ReadAsync_DecodedFields_DoNotBreakRoundTrip()
    {
        // Adding decoded columns must not affect byte-identical round-trip.
        var fixedData = new byte[678];
        BitConverter.GetBytes((ushort)5).CopyTo(fixedData, 2);
        SetU16(fixedData, 28, 88);
        var original = BuildQuestFile(version: 6, quests: [MakeQuestRaw(fixedData, "SAY 1 NPC\nACCEPT\nEND", "", "DONE\nEND")]);

        var inputPath = Path.Combine(_tempDir, "QuestData.shn");
        File.WriteAllBytes(inputPath, original);
        var tables = await _provider.ReadAsync(inputPath);
        var outputPath = Path.Combine(_tempDir, "QuestData_out.shn");
        await _provider.WriteAsync(outputPath, tables);

        File.ReadAllBytes(outputPath).ShouldBe(original);
    }

    // ── Real-file decode (BYO data: skipped unless the client file is present) ──

    [Fact]
    public async Task ReadAsync_RealClientFile_DecodesKnownQuests()
    {
        const string real = @"Z:/ClientProd2/ressystem/QuestData.shn";
        if (!File.Exists(real))
            return; // BYO data not mounted — skip (no real game files in the repo)

        var rows = (await _provider.ReadAsync(real))[0].Rows;
        rows.Count.ShouldBeGreaterThan(2000); // live client has 2304 quests

        var q1 = rows.Single(r => Convert.ToUInt16(r["QuestID"]) == 1);
        q1["StartNPC"].ShouldBe((ushort)111); // Element Helper Remi
        ((string)q1["StartScript"]!).ShouldContain("ACCEPT");

        var q2 = rows.Single(r => Convert.ToUInt16(r["QuestID"]) == 2);
        q2["StartNPC"].ShouldBe((ushort)29); // Healer Julia
        ((string)q2["FinishScript"]!).ShouldContain("LINK 3"); // chains to quest 3

        var q8 = rows.Single(r => Convert.ToUInt16(r["QuestID"]) == 8);
        q8["StartNPC"].ShouldBe((ushort)88); // Weapon Title Merchant Zach
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
