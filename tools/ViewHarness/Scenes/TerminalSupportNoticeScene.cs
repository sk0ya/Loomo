using System.Windows;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;

namespace ViewHarness.Scenes;

/// <summary>ターミナルサポートペインの知らせ欄（Claude Code へ渡せなかった理由）。実物の
/// <see cref="TerminalSupportPaneView"/> と <see cref="TerminalSupportViewModel"/> を使い、知らせが無い状態・
/// 短い知らせ・長い知らせ（折り返し）の 3 つを撮る（2026-11-18）。</summary>
public sealed class TerminalSupportNoticeScene : IScene
{
    private TerminalSupportViewModel? _vm;

    public string Name => "terminal-support-notice";
    public string Description => "ターミナルサポートの知らせ欄（Claude Code へ渡せなかった理由・下に重ねて出す）";
    public Size Size => new(420, 520);

    public FrameworkElement Build()
    {
        _vm = new TerminalSupportViewModel();
        var view = new TerminalSupportPaneView { DataContext = _vm };
        return view;
    }

    public IEnumerable<SceneStep> Steps(FrameworkElement root) =>
    [
        new SceneStep("notice-short", () => _vm!.ReportNotice("Claude Code が動いているターミナルがありません。ターミナルで claude を起動してください。")),
        new SceneStep("notice-long", () => _vm!.ReportNotice(
            "渡すものを書けませんでした: 一時フォルダー C:\\Users\\koya\\AppData\\Local\\Temp\\Loomo\\handoff\\terminal-selection-120000-1a2b3c4d.txt にアクセスできません（別のプロセスが使用中か、書き込み権限がありません）。")),
        new SceneStep("notice-cleared", () => _vm!.ReportNotice("")),
    ];
}
