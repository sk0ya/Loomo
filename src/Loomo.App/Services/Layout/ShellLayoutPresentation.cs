using sk0ya.Loomo.App.Layout;

namespace sk0ya.Loomo.App.Services;

/// <summary>保存レイアウトと表示モードのラベルを組み立てる。</summary>
internal static class ShellLayoutPresentation
{
    public static string ModeName(DisplayMode mode) => mode switch
    {
        DisplayMode.Solo => "集中",
        DisplayMode.Dock => "ドック",
        _ => "分割",
    };

    public static string ShortcutHint(string? gesture, string suffix)
        => string.IsNullOrWhiteSpace(gesture) ? "" : $"{gesture} {suffix}";

    public static string ShortcutSuffix(string? gesture, string suffix)
        => ShortcutHint(gesture, suffix) is { Length: > 0 } hint ? $"（{hint}）" : "";

    public static string LayoutSummary(SavedLayout layout, Func<PaneKind, string> paneLabel)
    {
        var panes = PaneLayoutTree.SnapshotPaneKinds(layout.Tree).Select(paneLabel);
        return $"{layout.Name}  ({string.Join(" · ", panes)})";
    }
}
