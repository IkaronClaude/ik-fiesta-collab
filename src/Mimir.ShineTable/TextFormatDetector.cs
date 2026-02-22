namespace Mimir.ShineTable;

internal enum TextFormat { Unknown, Table, Define }

internal static class TextFormatDetector
{
    /// <summary>
    /// Peeks at a .txt file's first non-empty, non-comment line to determine the text format.
    /// Returns Unknown for non-.txt files or unrecognized content.
    /// </summary>
    public static TextFormat Detect(string filePath)
    {
        if (!Path.GetExtension(filePath).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return TextFormat.Unknown;

        foreach (var line in File.ReadLines(filePath, ShineTableDataProvider.TextEncoding))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';')) continue;

            if (trimmed.StartsWith("#DEFINE", StringComparison.OrdinalIgnoreCase))
                return TextFormat.Define;

            if (trimmed.StartsWith("#table", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("#ignore", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("#exchange", StringComparison.OrdinalIgnoreCase))
                return TextFormat.Table;

            return TextFormat.Unknown;
        }

        return TextFormat.Unknown;
    }
}
