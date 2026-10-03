using System.Globalization;
using System.Text.Json;

namespace Fiesta.Collab.Core.Models;

/// <summary>The template action orderBy: a table's rows sorted by one column (stable - equal values keep their order;
/// row environments move with their rows). Numbers sort as numbers, everything else as ordinal text, NULL first.</summary>
public static class TableOrdering
{
    public static TableFile OrderBy(TableFile t, string column, bool descending = false)
    {
        var keyed = Enumerable.Range(0, t.Data.Count).Select(i => (i, k: Key(t.Data[i].GetValueOrDefault(column)))).ToList();
        var cmp = Comparer<(int kind, double num, string text)>.Create((a, b) =>
        {
            var c = a.kind.CompareTo(b.kind);
            if (c == 0) c = a.num.CompareTo(b.num);
            if (c == 0) c = string.CompareOrdinal(a.text, b.text);
            return c;
        });
        var order = (descending ? keyed.OrderByDescending(x => x.k, cmp) : keyed.OrderBy(x => x.k, cmp)).Select(x => x.i).ToList();
        return new TableFile
        {
            Header = t.Header,
            Columns = t.Columns,
            Data = order.Select(i => t.Data[i]).ToList(),
            RowEnvironments = t.RowEnvironments is null ? null : order.Select(i => t.RowEnvironments[i]).ToList()
        };
    }

    private static (int kind, double num, string text) Key(object? v) => v switch
    {
        null => (0, 0, ""),
        JsonElement { ValueKind: JsonValueKind.Null } => (0, 0, ""),
        JsonElement { ValueKind: JsonValueKind.Number } e => (1, e.GetDouble(), ""),
        JsonElement e => (2, 0, e.ToString()),
        string s => (2, 0, s),
        IConvertible c => (1, Convert.ToDouble(c, CultureInfo.InvariantCulture), ""),
        _ => (2, 0, v.ToString() ?? ""),
    };
}
