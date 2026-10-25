using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Ctrl+Tab の巡回（§24.65）でパレットに並べる 1 行を作る（純ロジック）。見出しは TABS の行と同じ（名前・シェルのタイトル・ファイル名）。
/// ファイルのタブは <see cref="PaletteCommand.Target"/> を持つので、選んでいる間は右の欄にそのファイルの中身が出る。
/// ターミナルとブラウザは中身を引けないので、右の欄には場所（cwd と最後のコマンド／URL）を出す。
/// </summary>
internal static class RoomTabSwitcherItems
{
    public const string TerminalCategory = "ターミナル";
    public const string EditorCategory = "エディタ";
    public const string BrowserCategory = "ブラウザ";

    public static PaletteCommand Terminal(TabEntryViewModel entry, string? workingDirectory, Action activate)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            lines.Add(workingDirectory);
        if (!string.IsNullOrWhiteSpace(entry.TerminalSummaryToolTip ?? entry.TerminalSummary))
            lines.Add((entry.TerminalSummaryToolTip ?? entry.TerminalSummary)!);
        return new PaletteCommand(TerminalCategory, entry.Title, activate)
        {
            Detail = Join(lines),
        };
    }

    public static PaletteCommand Editor(
        TabEntryViewModel entry, string? filePath, Func<string, string> toDisplayPath, Action activate)
    {
        var hasFile = !string.IsNullOrEmpty(filePath) && File.Exists(filePath);
        return new PaletteCommand(
            hasFile ? $"{EditorCategory} · {FolderOf(toDisplayPath(filePath!))}" : EditorCategory,
            entry.Title, activate)
        {
            Target = hasFile ? new PaletteTarget(filePath!) : null,
            Detail = hasFile ? null : "保存していないタブ（中身は開いてから）",
        };
    }

    public static PaletteCommand Browser(TabEntryViewModel entry, Action activate)
        => new(BrowserCategory, string.IsNullOrWhiteSpace(entry.Title) ? entry.BrowserUrl ?? "新しいタブ" : entry.Title, activate)
        {
            Detail = Join([entry.BrowserUrl]),
        };

    private static string Join(IEnumerable<string?> lines)
    {
        var text = string.Join(Environment.NewLine, lines.Where(l => !string.IsNullOrWhiteSpace(l)));
        return (text.Length == 0 ? "" : text + Environment.NewLine + Environment.NewLine)
            + "Ctrl を離すとこのタブへ・Esc でやめる";
    }

    private static string FolderOf(string displayPath)
    {
        var folder = Path.GetDirectoryName(displayPath);
        return string.IsNullOrEmpty(folder) ? "." : folder;
    }
}
