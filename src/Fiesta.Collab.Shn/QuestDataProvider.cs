using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Fiesta.Collab.Core.Models;
using Fiesta.Collab.Core.Providers;

namespace Fiesta.Collab.Shn;

/// <summary>
/// Reads <c>QuestData.shn</c>, a bespoke quest-definition format that is NOT a
/// standard column-SHN (the normal SHN crypto/parser throws on it). It is not
/// encrypted; everything is little-endian and strings are EUC-KR (cp949).
///
/// File layout (reverse-engineered + validated against all 2304 quests of the
/// live client — script-length sums equal the record size exactly and the file
/// is consumed to EOF):
///
///   [0:2]  u16 marker/version (0x0006 on the live client)
///   [2:4]  u16 questCount
///   then questCount records, each:
///     +0   u32 dataLength (whole record incl. this field; next = off + dataLength)
///     +4   u16 QuestID            (matches the wire nQuestID)
///     +16  u8  IsNeedLevel, +17 MinLevel, +18 MaxLevel (best-effort)
///     +30  u16 StartNPC           (mobId of the giver NPC)
///     +51  u8  Class              (0 = all classes; best-effort)
///     +74  Mobs[5]  stride 6:  en u8, isNpc u8, id u16, toKill u8, amount u8
///     +104 Items[10] stride 6:  en u8, type u8, id u16, amount u16
///     +164 opaque item-data blob (mostly zero) up to +516
///     +516 Rewards[12] stride 12: method u8 (0=Disabled,1=Fixed,2=Choice),
///                                  type u8 (0=EXP,1=Money,2=Item,3=Fame), pad u16,
///                                  then 8-byte payload — Item: {id u16, count u16, pad u32},
///                                  otherwise an 8-byte amount (u64).
///     +660 scriptLengths: StartLen u16, FinishLen u16, ActionLen u16 (note order!)
///     +666 14-byte opaque scriptdata
///     +680 scripts in this order: Start, Action, Finish — each EUC-KR and
///          null-terminated (the terminator is counted in its declared length).
///
/// NOTE the framing reads dataLength as a <see cref="ushort"/>: every live record
/// is well under 64 KiB (max 1868 B), and the high two bytes of the u32 are zero,
/// so they fall harmlessly into the head of <c>fixedData</c>. This keeps the
/// fixed region a constant <see cref="FixedDataSize"/> bytes and lets the
/// QuestID/StartNPC/etc. offsets below sit at (record offset − 2).
///
/// The decoded <c>Mobs</c>/<c>Items</c>/<c>Rewards</c> columns are emitted as
/// compact JSON for queryability; the raw <c>FixedData</c> hex is retained so
/// <see cref="WriteAsync"/> can round-trip the file byte-for-byte.
///
/// The Z:/QuestEditor reference tool (and its <c>QuestData Documentation.txt</c>)
/// describe an OLDER revision with different offsets (StartNPC@20, fixed=616,
/// 18-byte scriptdata) — do not trust those for this client.
/// </summary>
public sealed class QuestDataProvider : IDataProvider
{
    // ── Fixed region, measured in the "data" frame (record bytes after the
    //    2-byte recordLength read), i.e. record offset − 2. ──
    private const int FixedDataSize = 678;   // record fixed region 680 − 2

    private const int QuestIdOffset = 2;     // record +4
    private const int IsNeedLevelOffset = 14; // record +16
    private const int MinLevelOffset = 15;   // record +17
    private const int MaxLevelOffset = 16;   // record +18
    private const int StartNpcOffset = 28;   // record +30
    private const int ClassOffset = 49;      // record +51

    private const int MobsOffset = 72;       // record +74
    private const int MobCount = 5;
    private const int MobStride = 6;

    private const int ItemsOffset = 102;     // record +104
    private const int ItemCount = 10;
    private const int ItemStride = 6;

    private const int RewardsOffset = 514;   // record +516
    private const int RewardCount = 12;
    private const int RewardStride = 12;

    private const int ScriptLenOffset = 658; // record +660 (Start, Finish, Action u16)

    private static readonly Encoding EucKr;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

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

        var columns = new List<ColumnDefinition>
        {
            new() { Name = "QuestID", Type = ColumnType.UInt16, Length = 2 },
            new() { Name = "StartNPC", Type = ColumnType.UInt16, Length = 2 },
            new() { Name = "IsNeedLevel", Type = ColumnType.Byte, Length = 1 },
            new() { Name = "MinLevel", Type = ColumnType.Byte, Length = 1 },
            new() { Name = "MaxLevel", Type = ColumnType.Byte, Length = 1 },
            new() { Name = "Class", Type = ColumnType.Byte, Length = 1 },
            new() { Name = "ExpReward", Type = ColumnType.UInt64, Length = 8 },
            new() { Name = "Mobs", Type = ColumnType.String, Length = 0 },
            new() { Name = "Items", Type = ColumnType.String, Length = 0 },
            new() { Name = "Rewards", Type = ColumnType.String, Length = 0 },
            new() { Name = "FixedData", Type = ColumnType.String, Length = FixedDataSize * 2 },
            new() { Name = "StartScript", Type = ColumnType.String, Length = 0 },
            new() { Name = "InProgressScript", Type = ColumnType.String, Length = 0 },
            new() { Name = "FinishScript", Type = ColumnType.String, Length = 0 }
        };

        var rows = new List<Dictionary<string, object?>>(questCount);

        for (int i = 0; i < questCount; i++)
        {
            ushort recordLength = reader.ReadUInt16();
            byte[] data = reader.ReadBytes(recordLength - 2);

            // Fixed region is a constant size for this format version. Validate it
            // against the script-length fields when those are populated (real data).
            int fixedDataSize = FixedDataSize;
            if (data.Length >= ScriptLenOffset + 6)
            {
                int sLen = U16(data, ScriptLenOffset);
                int fLen = U16(data, ScriptLenOffset + 2);
                int aLen = U16(data, ScriptLenOffset + 4);
                int scriptTotal = sLen + aLen + fLen;
                int derived = data.Length - scriptTotal;
                if (scriptTotal > 0 && derived != FixedDataSize)
                    _logger.LogWarning(
                        "Quest {Index} fixed region {Derived} != expected {Expected} (unexpected QuestData version?)",
                        i, derived, FixedDataSize);
            }

            byte[] fixedData = data[..fixedDataSize];
            byte[] scriptBytes = data[fixedDataSize..];
            var scripts = SplitNullTerminatedStrings(scriptBytes, 3);

            ushort questId = U16(fixedData, QuestIdOffset);
            ushort startNpc = U16(fixedData, StartNpcOffset);

            var rewards = ReadRewards(fixedData);
            ulong expReward = 0;
            foreach (var r in rewards)
                if (r.Type == RewardType.Exp) expReward += r.Amount;

            rows.Add(new Dictionary<string, object?>
            {
                ["QuestID"] = questId,
                ["StartNPC"] = startNpc,
                ["IsNeedLevel"] = fixedData[IsNeedLevelOffset],
                ["MinLevel"] = fixedData[MinLevelOffset],
                ["MaxLevel"] = fixedData[MaxLevelOffset],
                ["Class"] = fixedData[ClassOffset],
                ["ExpReward"] = expReward,
                ["Mobs"] = JsonSerializer.Serialize(ReadMobs(fixedData), JsonOpts),
                ["Items"] = JsonSerializer.Serialize(ReadItems(fixedData), JsonOpts),
                ["Rewards"] = JsonSerializer.Serialize(rewards, JsonOpts),
                ["FixedData"] = Convert.ToHexString(fixedData),
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
                ["fixedDataSize"] = FixedDataSize
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

            // Use actual byte length of FixedData — metadata value may belong to a different env
            ushort recordLength = (ushort)(2 + fixedData.Length + s1.Length + 1 + s2.Length + 1 + s3.Length + 1);

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

    // ── Decoded sub-structures ──

    public enum RewardMethod : byte { Disabled = 0, Fixed = 1, Choice = 2 }
    public enum RewardType : byte { Exp = 0, Money = 1, Item = 2, Fame = 3 }

    public sealed record QuestMob(bool IsNpc, ushort Id, bool ToKill, byte Amount);
    public sealed record QuestItemReq(byte Type, ushort Id, ushort Amount);
    public sealed record QuestReward(RewardMethod Method, RewardType Type, ushort ItemId, ushort ItemCount, ulong Amount);

    private static List<QuestMob> ReadMobs(byte[] f)
    {
        var list = new List<QuestMob>();
        for (int m = 0; m < MobCount; m++)
        {
            int o = MobsOffset + m * MobStride;
            if (o + MobStride > f.Length || f[o] == 0) continue; // disabled
            list.Add(new QuestMob(f[o + 1] != 0, U16(f, o + 2), f[o + 4] != 0, f[o + 5]));
        }
        return list;
    }

    private static List<QuestItemReq> ReadItems(byte[] f)
    {
        var list = new List<QuestItemReq>();
        for (int it = 0; it < ItemCount; it++)
        {
            int o = ItemsOffset + it * ItemStride;
            if (o + ItemStride > f.Length || f[o] == 0) continue; // disabled
            list.Add(new QuestItemReq(f[o + 1], U16(f, o + 2), U16(f, o + 4)));
        }
        return list;
    }

    private static List<QuestReward> ReadRewards(byte[] f)
    {
        var list = new List<QuestReward>();
        for (int r = 0; r < RewardCount; r++)
        {
            int o = RewardsOffset + r * RewardStride;
            if (o + RewardStride > f.Length) break;
            var method = (RewardMethod)f[o];
            var type = (RewardType)f[o + 1];
            if (method == RewardMethod.Disabled) continue;
            if (type == RewardType.Item)
                list.Add(new QuestReward(method, type, U16(f, o + 4), U16(f, o + 6), 0));
            else
                list.Add(new QuestReward(method, type, 0, 0, BitConverter.ToUInt64(f, o + 4)));
        }
        return list;
    }

    private static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));

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

    private static ushort ConvertToUInt16(object? v)
    {
        if (v is System.Text.Json.JsonElement je)
            return je.ValueKind == System.Text.Json.JsonValueKind.String
                ? ushort.Parse(je.GetString()!)
                : (ushort)je.GetUInt32();
        return Convert.ToUInt16(v);
    }
}
