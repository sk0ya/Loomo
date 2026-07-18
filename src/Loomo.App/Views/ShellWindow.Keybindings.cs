
namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: キーボードショートカットの結線。<see cref="CommandCatalog"/> の各コマンド Id を
/// 実体アクションへ結び、<see cref="KeyboardDispatcher"/> を組み立てる。ディスパッチャは
/// <see cref="KeybindingService"/> から実効バインドを得るので、設定画面での再割り当てが即反映される。
/// 新しいショートカットは、カタログに 1 行足してここへアクションを 1 行結ぶだけで有効になる。
/// </summary>
public partial class ShellWindow
{
    /// <summary>コマンド Id → 実体アクションの対応を組み、ディスパッチャを生成する。
    /// ここに登録しないコマンド（例 <c>composer.run</c>）はウィンドウ全体では消費せず、
    /// より内側のスコープ別ハンドラ（コンポーザ）に委ねる。</summary>
    private KeyboardDispatcher BuildKeyboardDispatcher()
    {
        var actions = new Dictionary<string, Action>(StringComparer.Ordinal)
        {
            // パレット
            ["palette.open"] = OpenCommandPalette,
            ["palette.openFromPrefix"] = OpenCommandPalette,

            // ペイン：フォーカス移動
            ["pane.focus.left"] = () => FocusPaneInDirection(DropZone.Left),
            ["pane.focus.down"] = () => FocusPaneInDirection(DropZone.Below),
            ["pane.focus.up"] = () => FocusPaneInDirection(DropZone.Above),
            ["pane.focus.right"] = () => FocusPaneInDirection(DropZone.Right),

            // ペイン：リサイズ（実行で resize モードへ＝以降 bare h/j/k/l で連続伸縮）
            ["pane.resize.left"] = () => ResizeFocusedPane(DropZone.Left),
            ["pane.resize.down"] = () => ResizeFocusedPane(DropZone.Below),
            ["pane.resize.up"] = () => ResizeFocusedPane(DropZone.Above),
            ["pane.resize.right"] = () => ResizeFocusedPane(DropZone.Right),

            // ペイン：ズーム／閉じる／分割
            ["pane.zoom"] = ToggleZoom,
            ["pane.fullscreen"] = TogglePaneFullscreen,
            ["pane.close"] = () => { if (!CloseFocusedViewport()) HideFocusedRegion(); },
            ["pane.split.vertical"] = () => HandleViewportSplitKey(Key.V),
            ["pane.split.horizontal"] = () => HandleViewportSplitKey(Key.S),
            ["pane.split.closeView"] = () => HandleViewportSplitKey(Key.Q),

            // セッション：Ctrl+T はモードに応じた巡回（ソロ＝舞台のもの／レイアウト＝保存レイアウト）
            ["stage.cycle"] = () => CycleInActiveMode(1),
            // Ctrl+Shift+T：ソロ⇄レイアウトの切替
            ["mode.toggle"] = ToggleDisplayMode,

            // タブ（既定未割当。キーを割り当てると有効）
            ["tab.newTerminal"] = () => OnTerminalNewTab(this, new RoutedEventArgs()),
            ["tab.newEditor"] = () => OnEditorNewTab(this, new RoutedEventArgs()),
            ["tab.newBrowser"] = () => OnBrowserNewTab(this, new RoutedEventArgs()),

            // サイドバー（既定未割当）
            ["sidebar.explorer"] = () => _vm.ShowExplorerCommand.Execute(null),
            ["sidebar.search"] = () => _vm.ShowSearchCommand.Execute(null),
            ["sidebar.tabs"] = () => _vm.ShowTabsCommand.Execute(null),
            ["sidebar.sessions"] = () => _vm.Sessions.ToggleOpenCommand.Execute(null),
            ["sidebar.git"] = () => _vm.ShowGitCommand.Execute(null),
            ["sidebar.pegboard"] = () => _vm.ShowPegboardCommand.Execute(null),
            ["sidebar.settings"] = () => _vm.ShowSettingsCommand.Execute(null),
            ["sidebar.appearance"] = () => _vm.ShowAppearanceCommand.Execute(null),
            ["explorer.revealActiveFile"] = RevealActiveFileInFolderTree,
        };

        return new KeyboardDispatcher(
            _keybindings,
            actions,
            onEnterMode: mode => { if (mode == CommandCatalog.ResizeMode) SetResizeMode(true); },
            onExitMode: mode => { if (mode == CommandCatalog.ResizeMode) SetResizeMode(false); });
    }
}
