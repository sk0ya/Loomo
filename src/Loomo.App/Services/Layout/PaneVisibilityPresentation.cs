namespace sk0ya.Loomo.App.Services;

internal readonly record struct PaneMainHeader(string Label, bool HasDetail, string ToolTip);
internal readonly record struct PaneMenuToolTips(string MainAction, string VisibilityAction);
internal readonly record struct PaneMenuState(bool Active, bool Enabled, PaneMenuToolTips ToolTips);

/// <summary>ペイン名とメイン選択メニューに出す説明文。</summary>
internal static class PaneVisibilityPresentation
{
    public static PaneKind? ResolveMainPane(
        bool stageActive, PaneKind stagePane, bool dockActive, PaneKind? dockPane, PaneKind? topLeftPane)
        => stageActive ? stagePane : dockActive ? dockPane : topLeftPane;

    public static string Label(PaneKind kind) => kind switch
    {
        PaneKind.Terminal => "ターミナル",
        PaneKind.Editor => "エディタ",
        PaneKind.EditorSupport => "エディタサポート",
        PaneKind.Browser => "ブラウザ",
        PaneKind.Ai => "AI",
        PaneKind.Git => "Git",
        PaneKind.Diff => "Diff",
        PaneKind.Trace => "トレース",
        PaneKind.Debug => "IDE",
        PaneKind.Search => "検索",
        PaneKind.TsIde => "TS IDE",
        PaneKind.Files => "ファイル一覧",
        _ => kind.ToString(),
    };

    public static PaneMainHeader MainHeader(
        PaneKind? main, string layoutLabel, string unsavedLayoutLabel, string modeLabel, bool showPaneName)
    {
        var label = showPaneName
            ? main is { } pane ? Label(pane) : ""
            : layoutLabel == unsavedLayoutLabel ? "" : layoutLabel;
        var hasDetail = label.Length > 0;
        var toolTip = main is { } selected
            ? showPaneName
                ? $"{modeLabel}／メイン: {Label(selected)}"
                : $"{modeLabel}／配置: {(hasDetail ? layoutLabel : unsavedLayoutLabel)}／メイン: {Label(selected)}"
            : "並べ方、配置、メイン画面を変更";
        return new PaneMainHeader(label, hasDetail, toolTip);
    }

    public static PaneMenuToolTips MenuToolTips(
        PaneKind kind, bool docked, bool enabled, DockRegion region)
    {
        var name = Label(kind);
        var regionLabel = region switch
        {
            DockRegion.Right => "右の領域",
            DockRegion.Bottom => "下の領域",
            _ => "中央",
        };
        var mainAction = docked
            ? $"{name} を{regionLabel}に出す（右クリックで場所を変更）"
            : $"{name} をメインにする";
        var visibilityAction = docked
            ? enabled ? $"{name} を畳む" : $"{name} を{regionLabel}に出す"
            : enabled ? $"{name} を部屋からしまう" : $"{name} を部屋に出す";
        return new PaneMenuToolTips(mainAction, visibilityAction);
    }

    public static PaneMenuState MenuState(
        PaneKind kind, PaneKind? main, bool docked, bool dockOpen, bool sessionEnabled, DockRegion region)
    {
        var enabled = docked ? dockOpen : sessionEnabled;
        var active = docked ? dockOpen : main == kind;
        return new PaneMenuState(active, enabled, MenuToolTips(kind, docked, enabled, region));
    }
}
