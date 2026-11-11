namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: OS に見せるウィンドウタイトルを「フォーカス中のペイン - そのペインの前面タブ」にする
/// （<see cref="WindowTitleText"/>）。PC の作業記録に残すためだけのもので、画面上のタイトルバーは「Loomo」のまま。
/// タブの名前はブラウザのページ題・ターミナルの題のように裏で変わるので、変化の通知を全部結ぶのではなく、
/// 前面に居る間だけ 1 秒ごとに組み立て直して違うときだけ書く（記録ツールが読むのは前面の窓だけ）。
/// </summary>
public partial class ShellWindow {
    private readonly DispatcherTimer _windowTitleTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };

    private void InitializeWindowTitle() {
        _windowTitleTimer.Tick += (_, _) => RefreshWindowTitle();
        Activated += (_, _) => {
            RefreshWindowTitle();
            _windowTitleTimer.Start();
        };
        Deactivated += (_, _) => _windowTitleTimer.Stop();
        Closed += (_, _) => _windowTitleTimer.Stop();
    }

    private void RefreshWindowTitle() {
        var pane = _focusedRegion?.Pane;
        var title = WindowTitleText.Format(pane, pane is { } kind ? ActiveTabTitle(kind) : null);
        if (!string.Equals(Title, title, StringComparison.Ordinal))
            Title = title;
    }

    /// <summary>そのペインの前面タブの名前（タブの帯に出ている名前）。タブを持たないペインは null。</summary>
    private string? ActiveTabTitle(PaneKind kind) => kind switch {
        PaneKind.Editor when _activeEditorTab is { } tab => _vm.Tabs.EditorTabs.FirstOrDefault(t => t.Id == tab.Id)?.Title,
        PaneKind.Terminal when _activeTerminalTab is { } tab => _vm.Tabs.TerminalTabs.FirstOrDefault(t => t.Id == tab.Id)?.Title,
        PaneKind.Browser when _activeBrowserTab is { } tab => _vm.Tabs.BrowserTabs.FirstOrDefault(t => t.Id == tab.Id)?.Title,
        _ => null,
    };
}
