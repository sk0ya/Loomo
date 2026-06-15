using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services;
using Editor.Controls;
using Editor.Controls.Git;
using Editor.Controls.Themes;
using Terminal.Settings;
using Terminal.Tabs;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペイン内分割（vim 風 Ctrl+W v/s/q）と外観適用・PaneSplitView 実装</summary>
public partial class ShellWindow
{
    // ===== ペイン内分割の操作（Ctrl+W v/s/q） =====

    /// <summary>
    /// フォーカス中ペインが内部分割しているなら、その分割ビューポートを1枚畳む。
    /// 畳めた（＝分割があった）場合のみ true。分割が無ければ false（呼び元はペイン非表示へフォールバック）。
    /// </summary>
    private bool CloseFocusedViewport()
    {
        switch (_focusedRegion?.Pane)
        {
            case PaneKind.Editor when _editorViews is { LeafCount: > 1 }:
                CloseEditorView();
                return true;
            case PaneKind.Terminal when _terminalViews is { LeafCount: > 1 }:
                CloseTerminalView();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Ctrl+W v/s/q を、フォーカス中ペイン（Editor / Terminal のみ）の分割操作へ振り分ける。</summary>
    private void HandleViewportSplitKey(Key key)
    {
        switch (_focusedRegion?.Pane)
        {
            case PaneKind.Editor:
                if (key == Key.V) SplitEditorView(SplitKind.Columns);
                else if (key == Key.S) SplitEditorView(SplitKind.Rows);
                else CloseEditorView();
                break;
            case PaneKind.Terminal:
                if (key == Key.V) SplitTerminalView(SplitKind.Columns);
                else if (key == Key.S) SplitTerminalView(SplitKind.Rows);
                else CloseTerminalView();
                break;
        }
    }

    /// <summary>
    /// Editor ペインを分割し、新しいビューポートを隣に置く。<paramref name="filePath"/> を指定した
    /// （<c>:vsplit foo</c> / <c>:split foo</c> 由来の）場合はそのファイルを開き、無指定なら
    /// フォーカス中タブと同じ内容を別コントロールへ複製する（真 vim 風）。
    /// </summary>
    private void SplitEditorView(SplitKind orientation, string? filePath = null)
    {
        if (_editorViews is null)
            return;
        var src = _editorViews.FocusedTabId is { } sid
            ? _editorTabs.FirstOrDefault(t => t.Id == sid)
            : _activeEditorTab;

        var openPath = ResolveEditorPath(filePath, src);

        var newTab = CreateEditorTab();
        _editorTabs.Add(newTab);
        _vm.Tabs.AddEditorTab(newTab.Id, openPath ?? src?.Control.FilePath, src?.Control.IsModified ?? false, false);

        if (openPath is not null)
        {
            newTab.Control.LoadFile(openPath);
        }
        else if (src is not null)
        {
            // 同じ内容をもう1つのコントロールで開く（保存済みファイルは読み直し、未保存はテキストを複製）。
            if (!string.IsNullOrWhiteSpace(src.Control.FilePath) && File.Exists(src.Control.FilePath) && !src.Control.IsModified)
                newTab.Control.LoadFile(src.Control.FilePath);
            else
                newTab.Control.SetText(src.Control.Text);
        }

        _editorViews.SplitFocused(orientation, newTab.Id);
        SetActiveEditorTab(newTab);
        UpdateEditorTab(newTab);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>
    /// エディタ由来のウィンドウ/タブ操作で渡されたパス（相対可）を、開ける実ファイルへ解決する。
    /// 絶対パス→ソースタブのあるフォルダ→ワークスペースルートの順に探し、存在しなければ null。
    /// </summary>
    private string? ResolveEditorPath(string? filePath, EditorTab? src)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;
        if (Path.IsPathRooted(filePath))
            return File.Exists(filePath) ? Path.GetFullPath(filePath) : null;

        var bases = new[]
        {
            src is { } s && !string.IsNullOrWhiteSpace(s.Control.FilePath)
                ? Path.GetDirectoryName(s.Control.FilePath)
                : null,
            _activeWorkspace?.RootPath,
            _terminal.CurrentDirectory,
        };
        foreach (var dir in bases)
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            var candidate = Path.GetFullPath(Path.Combine(dir, filePath));
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>エディタの <c>:tabnew</c> 由来：ファイル指定があればそれを、無ければ空タブを新規エディタタブで開く。</summary>
    private async Task OpenEditorTabFromEditorAsync(string? filePath)
    {
        var openPath = ResolveEditorPath(filePath, _activeEditorTab);
        if (openPath is not null)
        {
            await OpenFileInNewEditorTabAsync(openPath);
            return;
        }

        var tab = CreateEditorTab();
        _editorTabs.Add(tab);
        _vm.Tabs.AddEditorTab(tab.Id, null, false, false);
        ActivateEditorTab(tab.Id);
        UpdateEditorTab(tab);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>エディタの <c>gt</c> / <c>gT</c> 由来：アクティブなエディタタブを巡回切り替えする。</summary>
    private void CycleEditorTab(int step)
    {
        if (_editorTabs.Count <= 1)
            return;
        var index = _activeEditorTab is { } active ? _editorTabs.FindIndex(t => t.Id == active.Id) : 0;
        if (index < 0)
            index = 0;
        var count = _editorTabs.Count;
        var next = ((index + step) % count + count) % count;
        ActivateEditorTab(_editorTabs[next].Id);
    }

    /// <summary>エディタの <c>:tabclose</c> 由来：アクティブなエディタタブを閉じる。</summary>
    private void CloseActiveEditorTab()
    {
        if (_activeEditorTab is not { } active)
            return;
        CloseEditorTab(active.Id);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>Editor のフォーカス中ビューポートを畳む（タブ自体は閉じない）。</summary>
    private void CloseEditorView()
    {
        if (_editorViews?.CloseFocused() != true)
            return;
        if (_editorViews.FocusedTabId is { } id && _editorTabs.FirstOrDefault(t => t.Id == id) is { } tab)
            SetActiveEditorTab(tab);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>Terminal ペインを分割し、同じ作業ディレクトリの新しいターミナルを隣のビューポートに置く。</summary>
    private void SplitTerminalView(SplitKind orientation)
    {
        if (_terminalViews is null)
            return;
        var src = _terminalViews.FocusedTabId is { } sid
            ? _terminalTabs.FirstOrDefault(t => t.Id == sid)
            : _activeTerminalTab;
        var cwd = src?.View.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
            cwd = _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory;

        var newTab = CreateTerminalTab(cwd);
        _terminalTabs.Add(newTab);
        _vm.Tabs.AddTerminalTab(newTab.Id, $"Terminal {CurrentTerminalWorkspace.NextTabNumber++}", false);

        _terminalViews.SplitFocused(orientation, newTab.Id);
        SetActiveTerminalTab(newTab);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>Terminal のフォーカス中ビューポートを畳む（タブ自体は閉じない）。</summary>
    private void CloseTerminalView()
    {
        if (_terminalViews?.CloseFocused() != true)
            return;
        if (_terminalViews.FocusedTabId is { } id && _terminalTabs.FirstOrDefault(t => t.Id == id) is { } tab)
            SetActiveTerminalTab(tab);
        SaveActiveWorkspaceSnapshot();
    }

    private TerminalTab CreateTerminalTab(string startDirectory, Guid? requestedId = null)
    {
        var view = new TerminalTabView("pwsh.exe", startDirectory)
        {
            // 初回セッションの自動起動（ConPTY・非同期）が完了時にフォーカスを奪わないようにする。
            // これが true だと、ワークスペース復元の最後に舞台へ入れたフォーカスを
            // 約1秒後のセッション起動が横取りする（sk0ya.Terminal.Controls 1.0.9）。
            AutoFocusOnStart = false,
        };
        ApplyTerminalAppearance(view);
        var tab = new TerminalTab(requestedId ?? Guid.NewGuid(), view);
        view.HeaderTitleChanged += (_, title) => UpdateTerminalTab(tab, title);
        // ターミナル本文の URL クリックを Loomo で受け、http/https は内蔵ブラウザへ振り分ける
        // （sk0ya.Terminal.Controls 1.0.10）。
        view.HyperlinkActivated += OnTerminalLinkActivated;
        HookTerminalActivity(tab);
        return tab;
    }

    private EditorTab CreateEditorTab(Guid? requestedId = null)
    {
        // GitServiceFactory を渡すと、エディタが行の差分（追加/変更/削除）をガター（行番号脇）に
        // マーク表示し、ステータスバーにブランチ名を出す。読込/保存/編集のたびに自動で再計算される
        // （RefreshGitDiff はコントロール内部で発火）。未指定だと NullEditorGitService となり無効。
        var control = new VimEditorControl(new VimEditorControlOptions
        {
            GitServiceFactory = () => new GitDiffProvider()
        })
        {
            VimEnabled = _settings.Vim.Enabled,
            Visibility = Visibility.Collapsed
        };
        ApplyEditorAppearance(control);
        // 分割時もステータスバーを1つに集約する（sk0ya.Editor.Controls 1.0.5 の共有ステータスバー機能）。
        // 各コントロールの内蔵バーは隠れ、フォーカス中エディタの状態だけが下端の共有バーへ流れる。
        control.SetSharedStatusBar(EditorSharedStatusBar);
        var tab = new EditorTab(requestedId ?? Guid.NewGuid(), control);
        control.BufferChanged += (_, _) =>
        {
            UpdateEditorTab(tab);
            if (ReferenceEquals(_editorSupportSourceTab, tab))
                ScheduleEditorSupportUpdate();
        };
        control.SaveRequested += (_, _) =>
        {
            QueueEditorTabUpdate(tab);
            if (ReferenceEquals(_editorSupportSourceTab, tab))
                ScheduleEditorSupportUpdate();
        };
        // エディタからの明示的なプレビュー要求は、EditorSupport ペインを「手動で開いた」扱いにする。
        control.MarkdownPreviewRequested += async (_, _) => await OpenEditorSupportAsync(tab);
        // エディタ本文中のURLを Ctrl+Click / gx で開く操作（sk0ya.Editor.Controls 1.0.6）は、
        // OS の既定ブラウザではなく Loomo 内蔵のブラウザペインで開く（Handled=true で既定動作を抑止）。
        control.LinkClicked += OnEditorLinkClicked;
        control.FileLinkClicked += OnEditorFileLinkClicked;

        // エディタ内の Vim ウィンドウ/タブ操作（:vsplit / :split / :tabnew / gt / gT / :tabclose / :close）を、
        // ホスト側の分割・タブ実装へ橋渡しする
        // （sk0ya.Editor.Controls 1.0.5 の公開API：エディタはイベントを発火するだけで、レイアウトはホストが担う）。
        // イベントはフォーカス中のエディタから発火するので、その時点のフォーカス領域に対して作用させる。
        // ※ Ctrl+W 系のウィンドウ移動（h/j/k/l/w）はシェルの OnPaneNavKey が Window の PreviewKeyDown で
        //   先取りして処理するため WindowNavRequested は購読しない（購読しても発火しないデッド配線になる）。
        control.SplitRequested += (_, e) => SplitEditorView(e.Vertical ? SplitKind.Columns : SplitKind.Rows, e.FilePath);
        control.NewTabRequested += async (_, e) => await OpenEditorTabFromEditorAsync(e.FilePath);
        control.NextTabRequested += (_, _) => CycleEditorTab(+1);
        control.PrevTabRequested += (_, _) => CycleEditorTab(-1);
        control.CloseTabRequested += (_, _) => CloseActiveEditorTab();
        control.WindowCloseRequested += (_, _) => CloseEditorView();
        return tab;
    }

    private void ApplyVimEnabledToOpenEditorTabs()
    {
        foreach (var tab in _editorTabs)
            tab.Control.VimEnabled = _settings.Vim.Enabled;
    }

    /// <summary>
    /// エディタの配色は設定で選んだプリセット（<see cref="AppearanceSettings.EditorTheme"/>）を
    /// ベースにしつつ、選択ハイライトだけを Loomo のアクセント色（半透明）へ差し替える。
    /// 既定の選択色は暗い背景に埋もれて見えないため。<see cref="EditorTheme"/> は複製手段が無いので、
    /// リフレクションで全プロパティを写し取り <c>SelectionBg</c> だけ上書きする
    /// （ライブラリ側がパレットを更新しても追従でき、Loomo 側に色定義が漏れない）。
    /// </summary>
    private EditorTheme BuildEditorTheme()
    {
        var accent = (Application.Current?.TryFindResource("Accent") as SolidColorBrush)?.Color
                     ?? Color.FromRgb(0x61, 0x48, 0xDE);
        var selection = new SolidColorBrush(Color.FromArgb(0x99, accent.R, accent.G, accent.B));

        var baseTheme = ResolveEditorTheme(_settings.Appearance.EditorTheme);
        var clone = new EditorTheme();
        foreach (var prop in typeof(EditorTheme).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite)
                continue;
            var value = prop.Name == nameof(EditorTheme.SelectionBg) ? selection : prop.GetValue(baseTheme);
            prop.SetValue(clone, value);
        }
        return clone;
    }

    /// <summary>設定のテーマ名から <see cref="EditorTheme"/> の組み込みプリセットを解決する。未知名は Dracula。</summary>
    private static EditorTheme ResolveEditorTheme(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "dark" => EditorTheme.Dark,
        "nord" => EditorTheme.Nord,
        "tokyonight" => EditorTheme.TokyoNight,
        "onedark" => EditorTheme.OneDark,
        _ => EditorTheme.Dracula,
    };

    /// <summary>エディタへ配色テーマとフォント（設定値、未指定なら触らない）を適用する。</summary>
    private void ApplyEditorAppearance(VimEditorControl control)
    {
        control.SetTheme(BuildEditorTheme());
        var ap = _settings.Appearance;
        if (!string.IsNullOrWhiteSpace(ap.EditorFontFamily))
            control.FontFamily = new FontFamily(ap.EditorFontFamily);
        if (ap.EditorFontSize > 0)
            control.FontSize = ap.EditorFontSize;
    }

    /// <summary>ターミナルへ配色（背景/文字色/ANSIパレット）とフォントを適用する。
    /// sk0ya.Terminal.Controls 1.0.5 の <see cref="TerminalTabView.SetColorTheme"/> / <see cref="TerminalTabView.SetFont"/>
    /// を使う。WPF の <c>Background</c>/<c>FontFamily</c> を直接書いても描画サーフェスには届かないため、
    /// 必ずこの専用APIを経由する。フォントは未指定なら現状値を保つ。</summary>
    private void ApplyTerminalAppearance(TerminalTabView view)
    {
        var ap = _settings.Appearance;
        view.SetColorTheme(BuildTerminalColorTheme(ap.TerminalTheme));

        var family = string.IsNullOrWhiteSpace(ap.TerminalFontFamily)
            ? view.FontFamilyName
            : ap.TerminalFontFamily;
        var size = ap.TerminalFontSize > 0 ? ap.TerminalFontSize : view.TerminalFontSize;
        view.SetFont(family, size);
    }

    /// <summary>ターミナル配色プリセット名 → <see cref="TerminalColorTheme"/>（背景/文字色/16色ANSIパレット）。
    /// 外観パネルの代表色（背景/文字色）と一致させる。未知名は Dark。</summary>
    private static TerminalColorTheme BuildTerminalColorTheme(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "light" => MakeTerminalTheme("#1F1F1F", "#FFFFFF", LightAnsiPalette, "#1F1F1F", "#FFB3D7FF"),
        "dracula" => MakeTerminalTheme("#F8F8F2", "#282A36", DraculaAnsiPalette, "#F8F8F0", "#6644475A"),
        "nord" => MakeTerminalTheme("#D8DEE9", "#2E3440", NordAnsiPalette, "#D8DEE9", "#66434C5E"),
        "solarizeddark" => MakeTerminalTheme("#93A1A1", "#002B36", SolarizedDarkAnsiPalette, "#93A1A1", "#66073642"),
        _ => MakeTerminalTheme("#D4D4D4", "#1E1E1E", DarkAnsiPalette, "#5FAFFF", "#664D4D4D"),
    };

    private static TerminalColorTheme MakeTerminalTheme(
        string fg, string bg, string[] ansiPalette, string cursor, string selection) =>
        new(
            ParseColor(fg),
            ParseColor(bg),
            ansiPalette.Select(ParseColor).ToArray(),
            ParseColor(cursor),
            ParseColor(selection));

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    // 16色ANSIパレット（0-7=標準, 8-15=明色）。
    private static readonly string[] DarkAnsiPalette =
    {
        "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00", "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
        // index 8(明色の黒)はPSReadLineが引数/演算子に使う。背景#1E1E1Eに埋もれない明度へ(#767676→)。
        "#9D9D9D", "#E74856", "#16C60C", "#F9F1A5", "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2",
    };

    private static readonly string[] LightAnsiPalette =
    {
        "#000000", "#C50F1F", "#13A10E", "#B58900", "#0037DA", "#881798", "#3A96DD", "#777777",
        "#5A5A5A", "#A4262C", "#0E8016", "#986801", "#0037DA", "#A100A1", "#178C92", "#1F1F1F",
    };

    private static readonly string[] DraculaAnsiPalette =
    {
        "#21222C", "#FF5555", "#50FA7B", "#F1FA8C", "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2",
        // index 8(引数/演算子色)はDraculaのcomment#6272A4だと背景#282A36でコントラスト不足→明るめに。
        "#8A95C2", "#FF6E6E", "#69FF94", "#FFFFA5", "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF",
    };

    private static readonly string[] NordAnsiPalette =
    {
        "#3B4252", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#88C0D0", "#E5E9F0",
        // index 8(引数/演算子色)はNordのnord3#4C566Aだと背景#2E3440に埋もれる→明るめのフロスト寄りに。
        "#909FBB", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#8FBCBB", "#ECEFF4",
    };

    private static readonly string[] SolarizedDarkAnsiPalette =
    {
        "#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
        // index 8(引数/演算子色)は本来base03#002B36で背景と同色＝不可視。base0#839496へ変更し可読に。
        "#839496", "#CB4B16", "#586E75", "#657B83", "#839496", "#6C71C4", "#93A1A1", "#FDF6E3",
    };

    /// <summary>外観設定の変更を、開いている全エディタ／ターミナルタブと EditorSupport ペインへ即時反映する。</summary>
    private void ApplyAppearanceToOpenTabs()
    {
        foreach (var tab in _editorTabs)
            ApplyEditorAppearance(tab.Control);
        foreach (var tab in _terminalTabs)
            ApplyTerminalAppearance(tab.View);
        if (_editorSupportSourceTab is not null)
            ScheduleEditorSupportUpdate();
    }

    private void QueueEditorTabUpdate(EditorTab tab)
    {
        _ = tab.Control.Dispatcher.BeginInvoke(new Action(() => UpdateEditorTab(tab)));
    }

    private void OnTabStripMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || scrollViewer.ScrollableWidth <= 0)
            return;

        var nextOffset = Math.Clamp(
            scrollViewer.HorizontalOffset - e.Delta,
            0,
            scrollViewer.ScrollableWidth);

        scrollViewer.ScrollToHorizontalOffset(nextOffset);
        e.Handled = true;
    }

    // ===== ペイン内分割（vim 風 Ctrl+W v/s）=====

    /// <summary>ビューポート木の1ノード。</summary>
    private abstract class ViewNode
    {
        /// <summary>親スプリット内での star 比率。</summary>
        public double Weight { get; set; } = 1;
        /// <summary>直近の描画で割り当てられた Grid トラック番号（未描画は -1）。サイズ取り込み用。</summary>
        public int TrackIndex { get; set; } = -1;
    }

    /// <summary>リーフ＝1ビューポート。<see cref="TabId"/> のコントロールを <see cref="Container"/> に映す。</summary>
    private sealed class ViewLeaf : ViewNode
    {
        /// <summary>ビューポートの安定ID（フォーカス追跡・ナビ用。タブIDとは別）。</summary>
        public Guid Id { get; } = Guid.NewGuid();
        /// <summary>このビューポートが表示しているタブ。</summary>
        public Guid TabId { get; set; }
        /// <summary>コントロールを内包する枠（フォーカス時にアクセント枠を出す）。再構築で再利用する。</summary>
        public Border Container { get; } = new() { BorderThickness = new Thickness(0), Focusable = false };
    }

    /// <summary>スプリット＝入れ子の行（上下）または列（左右）。</summary>
    private sealed class ViewSplit : ViewNode
    {
        public SplitKind Orientation { get; init; }
        public List<ViewNode> Children { get; } = new();
        public Grid? Host { get; set; }
    }

    /// <summary>
    /// 1つのペイン（Editor / Terminal）の <c>ContentHost</c>（Grid）内を複数ビューポートへ分割管理する。
    /// 各ビューポートは既存タブの1つを表示する（コントロールは1タブ＝1インスタンスのため）。表示していない
    /// コントロールは <see cref="_parking"/>（非表示）へ退避し破棄しない。トップレベルのペイン木とは独立。
    /// </summary>
    private sealed class PaneSplitView
    {
        private readonly Grid _host;
        private readonly Func<Guid, FrameworkElement?> _resolve;        // タブID → コントロール
        private readonly Func<IEnumerable<FrameworkElement>> _allControls;
        private readonly Func<Brush> _border;
        private readonly Func<Brush> _accent;
        private readonly Action<FrameworkElement> _focusControl;
        private readonly Action _onChanged;
        private readonly Grid _parking = new() { Visibility = Visibility.Collapsed };

        private ViewNode? _root;
        private ViewLeaf? _focused;

        public PaneSplitView(
            Grid host,
            Func<Guid, FrameworkElement?> resolve,
            Func<IEnumerable<FrameworkElement>> allControls,
            Func<Brush> border,
            Func<Brush> accent,
            Action<FrameworkElement> focusControl,
            Action onChanged)
        {
            _host = host;
            _resolve = resolve;
            _allControls = allControls;
            _border = border;
            _accent = accent;
            _focusControl = focusControl;
            _onChanged = onChanged;
        }

        public int LeafCount => Leaves().Count();
        public Guid? FocusedTabId => _focused?.TabId;
        public Guid? FocusedViewportId => _focused?.Id;
        public bool IsShown(Guid tabId) => Leaves().Any(l => l.TabId == tabId);

        /// <summary>木を捨ててコンテンツホストを空にする（ワークスペース切替時）。コントロールは破棄しない。</summary>
        public void Reset()
        {
            _root = null;
            _focused = null;
            _host.Children.Clear();
            _host.RowDefinitions.Clear();
            _host.ColumnDefinitions.Clear();
        }

        /// <summary>指定タブを表示する。既に表示中ならそのビューポートへフォーカス、無ければフォーカス中ビューポートへ割り当てる。</summary>
        public void Activate(Guid tabId)
        {
            if (_root is null)
            {
                var leaf = new ViewLeaf { TabId = tabId };
                _root = leaf;
                _focused = leaf;
            }
            else if (FindLeafByTab(tabId) is { } shown)
            {
                _focused = shown;
            }
            else
            {
                _focused ??= Leaves().FirstOrDefault();
                if (_focused is null)
                {
                    var leaf = new ViewLeaf { TabId = tabId };
                    _root = leaf;
                    _focused = leaf;
                }
                else
                {
                    _focused.TabId = tabId;
                }
            }
            Rebuild();
            FocusFocused();
        }

        /// <summary>フォーカス中ビューポートの隣へ新しいビューポート（newTabId 表示）を挿入し、そこへフォーカスする。</summary>
        public void SplitFocused(SplitKind orientation, Guid newTabId)
        {
            CaptureSizes();
            var target = _focused ?? Leaves().FirstOrDefault();
            var leaf = new ViewLeaf { TabId = newTabId };
            if (target is null || _root is null)
            {
                _root = leaf;
            }
            else
            {
                Insert(target, leaf, orientation);
                _root = Normalize(_root);
            }
            _focused = leaf;
            Rebuild();
            FocusFocused();
        }

        /// <summary>フォーカス中ビューポートを畳む（2枚以上のときのみ）。タブ自体は閉じない。</summary>
        public bool CloseFocused()
        {
            if (LeafCount <= 1 || _focused is null)
                return false;
            CaptureSizes();
            Remove(_focused);
            _root = Normalize(_root);
            _focused = Leaves().FirstOrDefault();
            Rebuild();
            FocusFocused();
            return true;
        }

        /// <summary>タブが閉じられたとき：それを表示していたビューポートを畳む（最後の1枚なら残す）。</summary>
        public void RemoveTab(Guid tabId)
        {
            var leaf = FindLeafByTab(tabId);
            if (leaf is null)
                return;
            if (LeafCount > 1)
            {
                Remove(leaf);
                _root = Normalize(_root);
                if (_focused == leaf)
                    _focused = Leaves().FirstOrDefault();
            }
        }

        /// <summary>表示中タブが有効IDの集合に無いビューポートを、未使用の有効タブへ振り直す。</summary>
        public void RepairTabs(IEnumerable<Guid> validTabIds)
        {
            var valid = validTabIds.ToHashSet();
            var used = new HashSet<Guid>();
            foreach (var leaf in Leaves())
            {
                if (!valid.Contains(leaf.TabId))
                {
                    var replacement = valid.FirstOrDefault(v => !used.Contains(v));
                    if (replacement != default || valid.Contains(default))
                        leaf.TabId = replacement;
                }
                used.Add(leaf.TabId);
            }
        }

        /// <summary>指定ビューポートをフォーカスして中身のコントロールへキーボードフォーカスを移す。</summary>
        public void FocusViewport(Guid viewportId)
        {
            if (FindLeafById(viewportId) is not { } leaf)
                return;
            _focused = leaf;
            UpdateFocusBorders();
            if (_resolve(leaf.TabId) is { } control)
                _focusControl(control);
        }

        /// <summary>キーボードフォーカスを得た要素から、それを内包するビューポートをフォーカス扱いにする（再フォーカスはしない）。</summary>
        public Guid? SetFocusedFromElement(DependencyObject element)
        {
            foreach (var leaf in Leaves())
            {
                for (DependencyObject? cur = element; cur is not null; cur = VisualTreeHelper.GetParent(cur))
                {
                    if (ReferenceEquals(cur, leaf.Container))
                    {
                        _focused = leaf;
                        UpdateFocusBorders();
                        return leaf.Id;
                    }
                }
            }
            return null;
        }

        /// <summary>フォーカス中ビューポートのコントロールへキーボードフォーカスを移す。</summary>
        public void FocusFocused()
        {
            if (_focused is { } leaf && _resolve(leaf.TabId) is { } control)
                _focusControl(control);
        }

        /// <summary>フォーカス中ビューポートから指定方向の最寄りビューポートへ移る。移れたときだけ true。</summary>
        public bool FocusInDirection(DropZone direction, Visual relativeTo)
        {
            if (_focused is null || LeafCount <= 1)
                return false;

            var rects = ViewportRects(relativeTo).ToList();
            var fromEntry = rects.FirstOrDefault(r => r.Id == _focused.Id);
            if (fromEntry.Id == default)
                return false;

            var from = fromEntry.Rect;
            var fromCenter = new Point(from.X + from.Width / 2, from.Y + from.Height / 2);
            Guid bestId = default;
            var bestScore = double.MaxValue;

            foreach (var (id, rect) in rects)
            {
                if (id == _focused.Id)
                    continue;

                const double tolerance = 1.0;
                var inDirection = direction switch
                {
                    DropZone.Left => rect.X + rect.Width <= from.X + tolerance,
                    DropZone.Right => rect.X >= from.X + from.Width - tolerance,
                    DropZone.Above => rect.Y + rect.Height <= from.Y + tolerance,
                    _ => rect.Y >= from.Y + from.Height - tolerance,
                };
                if (!inDirection)
                    continue;

                var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
                var (axis, perpendicular) = direction is DropZone.Left or DropZone.Right
                    ? (Math.Abs(center.X - fromCenter.X), Math.Abs(center.Y - fromCenter.Y))
                    : (Math.Abs(center.Y - fromCenter.Y), Math.Abs(center.X - fromCenter.X));
                var score = axis + perpendicular * 2;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestId = id;
                }
            }

            if (bestId == default)
                return false;

            FocusViewport(bestId);
            return true;
        }

        /// <summary>各ビューポートの矩形（relativeTo 座標系）を列挙する。</summary>
        public IEnumerable<(Guid Id, Rect Rect)> ViewportRects(Visual relativeTo)
        {
            foreach (var leaf in Leaves())
            {
                var c = leaf.Container;
                if (!c.IsVisible || c.ActualWidth <= 0 || c.ActualHeight <= 0)
                    continue;
                var topLeft = c.TransformToVisual(relativeTo).Transform(new Point(0, 0));
                yield return (leaf.Id, new Rect(topLeft, new Size(c.ActualWidth, c.ActualHeight)));
            }
        }

        // ----- レンダリング -----

        public void Rebuild()
        {
            if (_root is null)
            {
                _host.Children.Clear();
                return;
            }
            _root = Normalize(_root);

            // 既存の親から全コントロール・全コンテナを外してから組み直す。
            foreach (var el in _allControls())
                Detach(el);
            foreach (var leaf in Leaves())
            {
                leaf.Container.Child = null;
                Detach(leaf.Container);
            }
            Detach(_parking);
            _host.Children.Clear();
            _host.RowDefinitions.Clear();
            _host.ColumnDefinitions.Clear();

            var visual = Build(_root!);
            _host.Children.Add(visual);

            // 各ビューポートに表示コントロールを差し込み、残りは parking へ退避。
            var shown = new HashSet<FrameworkElement>();
            foreach (var leaf in Leaves())
            {
                if (_resolve(leaf.TabId) is { } control)
                {
                    Detach(control);
                    control.Visibility = Visibility.Visible;
                    leaf.Container.Child = control;
                    shown.Add(control);
                }
            }
            _parking.Children.Clear();
            foreach (var el in _allControls())
            {
                if (shown.Contains(el))
                    continue;
                Detach(el);
                el.Visibility = Visibility.Collapsed;
                _parking.Children.Add(el);
            }
            _host.Children.Add(_parking);

            UpdateFocusBorders();
        }

        private FrameworkElement Build(ViewNode node)
        {
            if (node is ViewLeaf leaf)
                return leaf.Container;

            var split = (ViewSplit)node;
            split.Host = null;
            foreach (var c in split.Children)
                c.TrackIndex = -1;
            if (split.Children.Count == 1)
                return Build(split.Children[0]);

            var grid = new Grid();
            split.Host = grid;
            var cols = split.Orientation == SplitKind.Columns;
            var min = cols ? 160.0 : 80.0;

            for (var i = 0; i < split.Children.Count; i++)
            {
                if (i > 0)
                {
                    ShellWindow.AddTrack(grid, cols, new GridLength(ShellWindow.SplitterThickness));
                    var splitter = NewSplitter(cols);
                    ShellWindow.SetTrack(splitter, cols, i * 2 - 1);
                    grid.Children.Add(splitter);
                }
                var child = split.Children[i];
                ShellWindow.AddTrack(grid, cols, new GridLength(child.Weight <= 0 ? 1 : child.Weight, GridUnitType.Star), min);
                child.TrackIndex = i * 2;
                var visual = Build(child);
                ShellWindow.SetTrack(visual, cols, i * 2);
                grid.Children.Add(visual);
            }
            return grid;
        }

        private GridSplitter NewSplitter(bool cols)
        {
            var splitter = new GridSplitter
            {
                Width = cols ? ShellWindow.SplitterThickness : double.NaN,
                Height = cols ? double.NaN : ShellWindow.SplitterThickness,
                ResizeDirection = cols ? GridResizeDirection.Columns : GridResizeDirection.Rows,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = _border(),
                Cursor = cols ? Cursors.SizeWE : Cursors.SizeNS,
                ToolTip = "ドラッグでリサイズ"
            };
            splitter.MouseEnter += (_, _) => splitter.Background = _accent();
            splitter.MouseLeave += (_, _) => splitter.Background = _border();
            splitter.DragCompleted += (_, _) => { CaptureSizes(); _onChanged(); };
            return splitter;
        }

        private void UpdateFocusBorders()
        {
            var multi = LeafCount > 1;
            foreach (var leaf in Leaves())
            {
                var on = multi && ReferenceEquals(leaf, _focused);
                leaf.Container.BorderThickness = new Thickness(on ? 1 : 0);
                leaf.Container.BorderBrush = on ? _accent() : null;
            }
        }

        // ----- ツリー操作 -----

        internal static void Detach(FrameworkElement el)
        {
            switch (el.Parent)
            {
                case Panel p: p.Children.Remove(el); break;
                case Decorator d: d.Child = null; break;
                case ContentControl c: c.Content = null; break;
            }
        }

        private IEnumerable<ViewLeaf> Leaves(ViewNode? node = null)
        {
            node ??= _root;
            if (node is ViewLeaf leaf)
                yield return leaf;
            else if (node is ViewSplit split)
                foreach (var child in split.Children)
                    foreach (var l in Leaves(child))
                        yield return l;
        }

        private ViewLeaf? FindLeafByTab(Guid tabId) => Leaves().FirstOrDefault(l => l.TabId == tabId);
        private ViewLeaf? FindLeafById(Guid id) => Leaves().FirstOrDefault(l => l.Id == id);

        private ViewSplit? FindParent(ViewNode target, ViewNode? current = null)
        {
            current ??= _root;
            if (current is not ViewSplit split)
                return null;
            if (split.Children.Contains(target))
                return split;
            foreach (var child in split.Children)
                if (FindParent(target, child) is { } found)
                    return found;
            return null;
        }

        private void Insert(ViewNode target, ViewNode node, SplitKind orientation)
        {
            var parent = FindParent(target);
            if (parent is not null && parent.Orientation == orientation)
            {
                parent.Children.Insert(parent.Children.IndexOf(target) + 1, node);
                return;
            }

            var split = new ViewSplit { Orientation = orientation, Weight = target.Weight };
            target.Weight = 1;
            node.Weight = 1;
            split.Children.Add(target);
            split.Children.Add(node);
            if (parent is null)
                _root = split;
            else
                parent.Children[parent.Children.IndexOf(target)] = split;
        }

        private void Remove(ViewNode node)
        {
            var parent = FindParent(node);
            if (parent is null)
                _root = null;
            else
                parent.Children.Remove(node);
        }

        private ViewNode? Normalize(ViewNode? node)
        {
            if (node is not ViewSplit split)
                return node;

            var kids = new List<ViewNode>();
            foreach (var child in split.Children)
            {
                var n = Normalize(child);
                if (n is null)
                    continue;
                if (n is ViewSplit inner && inner.Orientation == split.Orientation)
                {
                    var total = inner.Children.Sum(c => c.Weight > 0 ? c.Weight : 1);
                    var scale = total > 0 ? (inner.Weight > 0 ? inner.Weight : 1) / total : 1;
                    foreach (var c in inner.Children)
                    {
                        c.Weight = (c.Weight > 0 ? c.Weight : 1) * scale;
                        kids.Add(c);
                    }
                }
                else
                {
                    kids.Add(n);
                }
            }

            if (kids.Count == 0)
                return null;
            if (kids.Count == 1)
            {
                kids[0].Weight = split.Weight;
                return kids[0];
            }
            split.Children.Clear();
            split.Children.AddRange(kids);
            return split;
        }

        private void CaptureSizes(ViewNode? node = null)
        {
            node ??= _root;
            if (node is not ViewSplit split)
                return;
            if (split.Host is { } grid)
            {
                var cols = split.Orientation == SplitKind.Columns;
                foreach (var child in split.Children)
                {
                    var index = child.TrackIndex;
                    if (index < 0)
                        continue;
                    if (cols && index < grid.ColumnDefinitions.Count)
                    {
                        var w = grid.ColumnDefinitions[index].ActualWidth;
                        if (w > 0) child.Weight = w;
                    }
                    else if (!cols && index < grid.RowDefinitions.Count)
                    {
                        var h = grid.RowDefinitions[index].ActualHeight;
                        if (h > 0) child.Weight = h;
                    }
                }
            }
            foreach (var child in split.Children)
                CaptureSizes(child);
        }
    }
}
