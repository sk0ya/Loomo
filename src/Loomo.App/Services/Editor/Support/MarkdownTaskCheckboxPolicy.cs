namespace sk0ya.Loomo.App.Services;

/// <summary>Markdown task list の指定行を切り替えた本文を作る。</summary>
internal static class MarkdownTaskCheckboxPolicy
{
    public static string? ToggleLine(string text, int lineIndex)
    {
        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        if (lineIndex < 0 || lineIndex >= lines.Length)
            return null;
        var toggled = MarkdownRenderer.ToggleTaskListLine(lines[lineIndex]);
        if (toggled is null)
            return null;
        lines[lineIndex] = toggled;
        return string.Join(eol, lines);
    }
}
