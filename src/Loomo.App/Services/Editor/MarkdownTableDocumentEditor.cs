using sk0ya.Loomo.Core.Markdown;

namespace sk0ya.Loomo.App.Services;

/// <summary>エディタ本文内の Markdown テーブルを検出・置換・挿入する。</summary>
internal static class MarkdownTableDocumentEditor
{
    public static bool TryFindAtCaret(string text, int caretLine, out MarkdownTableRegion region)
        => MarkdownTableSync.TryFindTableAt(NormalizeLines(text), caretLine, out region);

    public static string ReplaceTable(
        string documentText,
        MarkdownTableRegion region,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var newline = NewlineOf(documentText);
        var lines = NormalizeLines(documentText);
        var table = MarkdownTableSync.SerializeTable(rows, region.Alignments);
        var result = new List<string>(lines.Length);
        result.AddRange(lines[..region.StartLine]);
        if (table.Length > 0)
            result.AddRange(table.Split('\n'));
        result.AddRange(lines[(region.EndLine + 1)..]);
        return string.Join(newline, result);
    }

    public static string? InsertTable(
        string documentText,
        int caretLine,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var table = MarkdownTableSync.SerializeTable(rows, Array.Empty<MarkdownColumnAlignment>());
        if (table.Length == 0)
            return null;
        var lines = NormalizeLines(documentText);
        var result = MarkdownTableSync.InsertTableAt(lines, caretLine, table);
        return string.Join(NewlineOf(documentText), result);
    }

    private static string[] NormalizeLines(string text)
        => text.Replace("\r\n", "\n").Split('\n');

    private static string NewlineOf(string text)
        => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}
