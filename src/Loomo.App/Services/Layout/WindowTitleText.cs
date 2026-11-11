namespace sk0ya.Loomo.App.Services;

/// <summary>OS に見せるウィンドウタイトル（タスクバー・Alt+Tab・作業記録ツールが読む Window.Title）。
/// 画面上のタイトルバーは自前描画で「Loomo」のまま——こちらは PC の記録に「どのペインのどのタブに居たか」を
/// 残すためだけのもの。形は「ペイン - タブ - Loomo」、タブを持たないペインは「ペイン - Loomo」、
/// ペインの外（サイドバー等）に居るときは「Loomo」。</summary>
internal static class WindowTitleText
{
    public const string AppName = "Loomo";

    public static string Format(PaneKind? pane, string? tabTitle)
    {
        if (pane is not { } kind)
            return AppName;
        var label = PaneVisibilityPresentation.Label(kind);
        var tab = tabTitle?.Trim();
        return string.IsNullOrEmpty(tab) ? $"{label} - {AppName}" : $"{label} - {tab} - {AppName}";
    }
}
