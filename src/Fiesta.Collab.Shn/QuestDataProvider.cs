using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Fiesta.Collab.Core.Models;
using Fiesta.Collab.Core.Providers;

namespace Fiesta.Collab.Shn;

/// <summary>
/// <c>QuestData.shn</c> exists in two unrelated formats, and which one you have depends on the client
/// build, so this provider sniffs rather than assumes:
///
///   <b>Monolith</b> (the 2016 build, client and server share the file) - a bespoke format that is NOT a
///   column-SHN. The standard SHN parser throws on it. Not encrypted, little-endian, EUC-KR strings.
///   <b>Columnar</b> (the 2026 build) - an ordinary 33-column SHN of quest headers, with the objectives,
///   rewards and dialogue split into separate normalised tables.
///
/// Only the monolith is handled here. A columnar file is declined by <see cref="CanHandle"/> so the normal
/// SHN provider takes it; that is the version detection, and it is done by reading the file, never by
/// looking at a client version number or a path.
///
/// MONOLITH LAYOUT. Taken from <c>QUEST_DATA</c> in Fiesta.pdb (compiler-computed offsets), not from
/// hand-reverse-engineering, and validated by round-tripping the live file byte for byte:
///
///   file:   u16 marker (6), u16 questCount, then questCount records
///   record: a 680-byte QUEST_DATA exactly as laid out in memory, then the three scripts back to back
///
///     +0    u32 nQuestDataSize   whole record incl. this field; next = off + size
///     +4    u16 ID               matches the wire nQuestID
///     +8    u32 NameID           +12 u32 BrifingID    -> QuestDialog ids (u32, NOT u16)
///     +16   u8  Region           (NOT a level field)
///     +17   u8  Type   +18 Repeatable   +19 nDailyQuestType
///     +24   StartCondition       the accept gate; each b* flag gates the field after it
///     +88   EndCondition         the hand-in gate, and the objectives:
///           +92  NPCMobList[5] stride 8     kill / find / talk targets
///           +132 ItemList[5]    stride 6    collect targets
///     +192  i32 NumOfActions     +196 Action[10] stride 32
///     +516  Reward[12] stride 12
///     +660  u16 SizeOfScriptStart, +662 u16 SizeOfScriptEnd, +664 u16 SizeOfScriptDoing
///           (note End before Doing in the SIZE triple, while the DATA order is Start, Doing, End)
///     +668  three dead char* script pointers - heap garbage on disk, preserved verbatim
///     +680  scripts, each NUL-terminated with the terminator counted in its declared length
///
/// An earlier revision of this provider used a hand-RE'd table that is wrong for this client: it read
/// dataLength as u16, put the level gate at +16 (that is Region), the objectives at +74 stride 6 (they are
/// at +92 stride 8) and the items at +104 (they are at +132). The offsets below supersede it. The
/// Z:/QuestEditor reference tool and its documentation describe a different, older revision again.
///
/// The queryable columns are decoded for SQL; <c>FixedData</c> keeps the whole 680-byte block so the file
/// round-trips exactly, and edits to the decoded columns are written back over it on save.
/// </summary>
public sealed class QuestDataProvider : IDataProvider
{
    private const int FixedSize = 680;
    private const ushort MonolithMarker = 6;

    // Head
    private const int OffSize = 0, OffId = 4, OffNameId = 8, OffBrifingId = 12,
                      OffRegion = 16, OffType = 17, OffRepeatable = 18, OffDailyType = 19;
    // Nested structs
    private const int OffStart = 24, OffEnd = 88, OffNumActions = 192, OffAction = 196, OffReward = 516;
    // Within StartCondition
    private const int StIsWaitListView = 0, StIsWaitListProgress = 1,
                      StNeedsLevel = 2, StLevelMin = 3, StLevelMax = 4, StNeedsNpc = 5, StNpcId = 6;
    // Within EndCondition
    private const int EnNpcMobList = 4, EnItemList = 44;
    private const int NpcMobStride = 8, NpcMobCount = 5, ItemStride = 6, ItemCount = 5;
    private const int RewardStride = 12, RewardSlots = 12;
    // Script sizes
    private const int OffSizeStart = 660, OffSizeEnd = 662, OffSizeDoing = 664;

    private static readonly Encoding Cp949 = CodePagesEncodingProvider.Instance.GetEncoding(949)
                                             ?? Encoding.GetEncoding(949);

    private readonly ILogger<QuestDataProvider> _logger;

    public QuestDataProvider(ILogger<QuestDataProvider> logger) => _logger = logger;

    public string FormatId => "questdata";
    public IReadOnlyList<string> SupportedExtensions => [".shn"];

    /// <summary>True only for a monolith QuestData. A columnar one is left to the SHN provider.</summary>
    public bool CanHandle(string filePath)
    {
        if (!Path.GetExtension(filePath).Equals(".shn", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Path.GetFileNameWithoutExtension(filePath).Equals("QuestData", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            return IsMonolith(File.ReadAllBytes(filePath), out _, out _);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Walks the record chain to decide the format. A marker check alone is not enough - the columnar file
    /// begins with an encrypted header whose first bytes can be anything - so the records must actually
    /// chain from the header to exactly the end of the file.
    /// </summary>
    private static bool IsMonolith(byte[] b, out int count, out string why)
    {
        count = 0;
        why = "";
        if (b.Length < 8) { why = "shorter than a header"; return false; }
        if (BitConverter.ToUInt16(b, 0) != MonolithMarker)
        {
            why = $"marker 0x{BitConverter.ToUInt16(b, 0):x4}, not 0x{MonolithMarker:x4} - columnar or another format";
            return false;
        }
        int n = BitConverter.ToUInt16(b, 2);
        if (n == 0) { why = "quest count 0"; return false; }

        var off = 4;
        for (var i = 0; i < n; i++)
        {
            if (off + FixedSize > b.Length) { why = $"record {i} runs past the end"; return false; }
            var size = (int)BitConverter.ToUInt32(b, off + OffSize);
            var scripts = BitConverter.ToUInt16(b, off + OffSizeStart)
                        + BitConverter.ToUInt16(b, off + OffSizeEnd)
                        + BitConverter.ToUInt16(b, off + OffSizeDoing);
            if (size != FixedSize + scripts)
            {
                why = $"record {i} size {size} != 680 + scripts {scripts}";
                return false;
            }
            off += size;
        }
        if (off != b.Length) { why = $"records end at {off}, file is {b.Length}"; return false; }
        count = n;
        return true;
    }

    public Task<IReadOnlyList<TableEntry>> ReadAsync(string filePath, CancellationToken ct = default)
    {
        var b = File.ReadAllBytes(filePath);
        if (!IsMonolith(b, out var count, out var why))
            throw new InvalidDataException($"{Path.GetFileName(filePath)} is not a monolith QuestData ({why})");

        _logger.LogDebug("Reading monolith QuestData {File}: {Count} quests", filePath, count);

        var columns = new List<ColumnDefinition>
        {
            Col("ID", ColumnType.UInt16, 2),
            Col("NameID", ColumnType.UInt32, 4),
            Col("BrifingID", ColumnType.UInt32, 4),
            Col("Region", ColumnType.Byte, 1),
            Col("Type", ColumnType.Byte, 1),
            Col("Repeatable", ColumnType.Byte, 1),
            Col("DailyType", ColumnType.Byte, 1),
            Col("IsWaitListView", ColumnType.Byte, 1),
            Col("IsWaitListProgress", ColumnType.Byte, 1),
            Col("NeedsLevel", ColumnType.Byte, 1),
            Col("LevelMin", ColumnType.Byte, 1),
            Col("LevelMax", ColumnType.Byte, 1),
            Col("NeedsNPC", ColumnType.Byte, 1),
            Col("StartNPC", ColumnType.UInt16, 2),
            Col("Objectives", ColumnType.String, 0),
            Col("ItemObjectives", ColumnType.String, 0),
            Col("Rewards", ColumnType.String, 0),
            Col("FixedData", ColumnType.String, FixedSize * 2),
            Col("StartScript", ColumnType.String, 0),
            Col("DoingScript", ColumnType.String, 0),
            Col("EndScript", ColumnType.String, 0),
        };

        var rows = new List<Dictionary<string, object?>>(count);
        var off = 4;
        for (var i = 0; i < count; i++)
        {
            var size = (int)BitConverter.ToUInt32(b, off + OffSize);
            var fixedData = b[off..(off + FixedSize)];

            // DATA order is Start, Doing, End; the SIZE triple is Start, End, Doing.
            int sStart = BitConverter.ToUInt16(fixedData, OffSizeStart);
            int sEnd = BitConverter.ToUInt16(fixedData, OffSizeEnd);
            int sDoing = BitConverter.ToUInt16(fixedData, OffSizeDoing);
            var p = off + FixedSize;
            var start = Script(b, p, sStart); p += sStart;
            var doing = Script(b, p, sDoing); p += sDoing;
            var end = Script(b, p, sEnd);

            rows.Add(new Dictionary<string, object?>
            {
                ["ID"] = BitConverter.ToUInt16(fixedData, OffId),
                ["NameID"] = BitConverter.ToUInt32(fixedData, OffNameId),
                ["BrifingID"] = BitConverter.ToUInt32(fixedData, OffBrifingId),
                ["Region"] = fixedData[OffRegion],
                ["Type"] = fixedData[OffType],
                ["Repeatable"] = fixedData[OffRepeatable],
                ["DailyType"] = fixedData[OffDailyType],
                ["IsWaitListView"] = fixedData[OffStart + StIsWaitListView],
                ["IsWaitListProgress"] = fixedData[OffStart + StIsWaitListProgress],
                ["NeedsLevel"] = fixedData[OffStart + StNeedsLevel],
                ["LevelMin"] = fixedData[OffStart + StLevelMin],
                ["LevelMax"] = fixedData[OffStart + StLevelMax],
                ["NeedsNPC"] = fixedData[OffStart + StNeedsNpc],
                ["StartNPC"] = BitConverter.ToUInt16(fixedData, OffStart + StNpcId),
                ["Objectives"] = JsonSerializer.Serialize(ReadObjectives(fixedData)),
                ["ItemObjectives"] = JsonSerializer.Serialize(ReadItemObjectives(fixedData)),
                ["Rewards"] = JsonSerializer.Serialize(ReadRewards(fixedData)),
                ["FixedData"] = Convert.ToHexString(fixedData),
                ["StartScript"] = start,
                ["DoingScript"] = doing,
                ["EndScript"] = end,
            });
            off += size;
        }

        var schema = new TableSchema
        {
            TableName = Path.GetFileNameWithoutExtension(filePath),
            SourceFormat = FormatId,
            Columns = columns,
            Metadata = new Dictionary<string, object>
            {
                ["questDataFormat"] = "monolith",
                ["marker"] = MonolithMarker,
            }
        };
        return Task.FromResult<IReadOnlyList<TableEntry>>([new TableEntry { Schema = schema, Rows = rows }]);
    }

    public Task WriteAsync(string filePath, IReadOnlyList<TableEntry> tables, CancellationToken ct = default)
    {
        var table = tables[0];
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(MonolithMarker);
        w.Write((ushort)table.Rows.Count);

        foreach (var row in table.Rows)
        {
            var fixedData = Convert.FromHexString(Str(row, "FixedData") ?? "");
            if (fixedData.Length != FixedSize)
                throw new InvalidDataException(
                    $"quest {row.GetValueOrDefault("ID")}: FixedData is {fixedData.Length} bytes, expected {FixedSize}");

            // The decoded columns are what SQL edits, so they are written back over the raw block. Anything
            // this provider does not decode keeps the bytes it was read with, which is what makes an
            // untouched file round-trip exactly.
            PutU16(fixedData, OffId, row, "ID");
            PutU32(fixedData, OffNameId, row, "NameID");
            PutU32(fixedData, OffBrifingId, row, "BrifingID");
            PutU8(fixedData, OffRegion, row, "Region");
            PutU8(fixedData, OffType, row, "Type");
            PutU8(fixedData, OffRepeatable, row, "Repeatable");
            PutU8(fixedData, OffDailyType, row, "DailyType");
            PutU8(fixedData, OffStart + StIsWaitListView, row, "IsWaitListView");
            PutU8(fixedData, OffStart + StIsWaitListProgress, row, "IsWaitListProgress");
            PutU8(fixedData, OffStart + StNeedsLevel, row, "NeedsLevel");
            PutU8(fixedData, OffStart + StLevelMin, row, "LevelMin");
            PutU8(fixedData, OffStart + StLevelMax, row, "LevelMax");
            PutU8(fixedData, OffStart + StNeedsNpc, row, "NeedsNPC");
            PutU16(fixedData, OffStart + StNpcId, row, "StartNPC");

            var start = Bytes(Str(row, "StartScript"));
            var doing = Bytes(Str(row, "DoingScript"));
            var end = Bytes(Str(row, "EndScript"));

            BitConverter.GetBytes((ushort)start.Length).CopyTo(fixedData, OffSizeStart);
            BitConverter.GetBytes((ushort)end.Length).CopyTo(fixedData, OffSizeEnd);
            BitConverter.GetBytes((ushort)doing.Length).CopyTo(fixedData, OffSizeDoing);
            BitConverter.GetBytes((uint)(FixedSize + start.Length + doing.Length + end.Length))
                        .CopyTo(fixedData, OffSize);

            w.Write(fixedData);
            w.Write(start);
            w.Write(doing);
            w.Write(end);
        }

        File.WriteAllBytes(filePath, ms.ToArray());
        _logger.LogDebug("Wrote monolith QuestData {File}: {Count} quests", filePath, table.Rows.Count);
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- decode helpers

    private static List<Dictionary<string, object>> ReadObjectives(byte[] f)
    {
        var list = new List<Dictionary<string, object>>();
        for (var i = 0; i < NpcMobCount; i++)
        {
            var o = OffEnd + EnNpcMobList + i * NpcMobStride;
            if (f[o] == 0) continue;
            list.Add(new Dictionary<string, object>
            {
                ["NPCMobID"] = BitConverter.ToUInt16(f, o + 2),
                ["Action"] = f[o + 4],       // 0 turn-in NPC, 1 kill, 2 find, 3 talk
                ["Count"] = f[o + 5],
                ["TargetGroup"] = f[o + 6],
            });
        }
        return list;
    }

    private static List<Dictionary<string, object>> ReadItemObjectives(byte[] f)
    {
        var list = new List<Dictionary<string, object>>();
        for (var i = 0; i < ItemCount; i++)
        {
            var o = OffEnd + EnItemList + i * ItemStride;
            if (f[o] == 0) continue;
            list.Add(new Dictionary<string, object>
            {
                ["ItemID"] = BitConverter.ToUInt16(f, o + 2),
                ["ItemLot"] = BitConverter.ToUInt16(f, o + 4),
            });
        }
        return list;
    }

    private static List<Dictionary<string, object>> ReadRewards(byte[] f)
    {
        var list = new List<Dictionary<string, object>>();
        for (var i = 0; i < RewardSlots; i++)
        {
            var o = OffReward + i * RewardStride;
            if (f[o] == 0) continue;      // Use: 0 disabled, 1 fixed, 2 choice
            var type = f[o + 1];          // 0 exp, 1 money, 2 item, 3 fame
            var r = new Dictionary<string, object> { ["Slot"] = i, ["Use"] = f[o], ["Type"] = type };
            if (type == 2)
            {
                r["ItemID"] = BitConverter.ToUInt16(f, o + 4);
                r["ItemLot"] = BitConverter.ToUInt16(f, o + 6);
            }
            else
            {
                r["Amount"] = BitConverter.ToUInt32(f, o + 4);
            }
            list.Add(r);
        }
        return list;
    }

    private static string Script(byte[] b, int at, int len)
        // The declared length includes the NUL terminator; the text is everything before it.
        => len <= 0 ? "" : Cp949.GetString(b, at, Math.Max(0, len - 1));

    private static byte[] Bytes(string? s)
    {
        var body = Cp949.GetBytes(s ?? "");
        var outp = new byte[body.Length + 1];   // re-add the terminator the length counts
        body.CopyTo(outp, 0);
        return outp;
    }

    private static ColumnDefinition Col(string name, ColumnType t, int len)
        => new() { Name = name, Type = t, Length = len };

    private static string? Str(Dictionary<string, object?> row, string name)
        => row.GetValueOrDefault(name) switch
        {
            null => null,
            JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString(),
            var v => v.ToString()
        };

    private static ulong Num(Dictionary<string, object?> row, string name)
    {
        var v = row.GetValueOrDefault(name);
        if (v is JsonElement je) return je.ValueKind == JsonValueKind.Number ? je.GetUInt64() : 0;
        return v is null ? 0 : Convert.ToUInt64(v);
    }

    private static void PutU8(byte[] f, int at, Dictionary<string, object?> row, string name)
        => f[at] = (byte)Num(row, name);

    private static void PutU16(byte[] f, int at, Dictionary<string, object?> row, string name)
        => BitConverter.GetBytes((ushort)Num(row, name)).CopyTo(f, at);

    private static void PutU32(byte[] f, int at, Dictionary<string, object?> row, string name)
        => BitConverter.GetBytes((uint)Num(row, name)).CopyTo(f, at);
}
