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
using Editor.Controls.Lsp;
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
        // 右クリックメニューへ「AIに聞く」「ブラウザで調べる」を追加する（選択時のみ・sk0ya.Terminal.Controls 1.0.19）。
        view.ContextMenuBuilding += OnTerminalContextMenuBuilding;
        HookTerminalActivity(tab);
        return tab;
    }

    /// <summary>空（または即時使用）のエディタタブを作る。コントロールは <see cref="EditorTab.Control"/> の
    /// 初回アクセスで実体化されるが、ここで作るタブは生成直後に LoadFile 等で使われるため実質その場で実体化する。</summary>
    private EditorTab CreateEditorTab(Guid? requestedId = null) =>
        new(requestedId ?? Guid.NewGuid()) { Realizer = RealizeEditorControl };

    /// <summary>保存済みスナップショットだけを持つ<b>未実体化</b>タブを作る（起動時の遅延復元用）。コントロールは
    /// アクティブ化・本文取得で初めて生成され、その際 <see cref="EditorTab.Pending"/> から本文が復元される。</summary>
    private EditorTab CreatePendingEditorTab(EditorTabSnapshot snapshot) =>
        new(snapshot.Id == Guid.Empty ? Guid.NewGuid() : snapshot.Id)
        {
            Realizer = RealizeEditorControl,
            Pending = snapshot
        };

    /// <summary><see cref="EditorTab.Control"/> 初回アクセス時の実体化本体。コントロールを生成・配線し、
    /// <see cref="EditorTab.SetControl"/> で<b>先に</b>確定してから Pending を復元する（LoadFile→BufferChanged が
    /// Control へ再入しても無限再帰しない）。</summary>
    private void RealizeEditorControl(EditorTab tab)
    {
        var control = BuildEditorControl(tab);
        tab.SetControl(control);
        if (tab.Pending is { } snapshot)
        {
            RestoreEditor(control, snapshot);
            tab.Pending = null;
        }
    }

    private VimEditorControl BuildEditorControl(EditorTab tab)
    {
        // GitServiceFactory を渡すと、エディタが行の差分（追加/変更/削除）をガター（行番号脇）に
        // マーク表示し、ステータスバーにブランチ名を出す。読込/保存/編集のたびに自動で再計算される
        // （RefreshGitDiff はコントロール内部で発火）。未指定だと NullEditorGitService となり無効。
        // LspManagerFactory を渡すと補完・診断・定義ジャンプ等の LSP 機能が有効になる（対象言語の
        // 言語サーバーが PATH に必要）。拡張子→サーバーの対応はエディタ側 LspServerRegistry が所有・
        // 永続化し、ユーザーは :LspAdd/:LspRemove/:LspList/:LspReset で追加削除する（Loomo は持たない）。
        var control = new VimEditorControl(new VimEditorControlOptions
        {
            GitServiceFactory = () => new GitDiffProvider(),
            LspManagerFactory = dispatcher => new LspManager(dispatcher)
        })
        {
            VimEnabled = _settings.Vim.Enabled,
            Visibility = Visibility.Collapsed
        };
        ApplyEditorAppearance(control);
        // 分割時もステータスバーを1つに集約する（sk0ya.Editor.Controls 1.0.5 の共有ステータスバー機能）。
        // 各コントロールの内蔵バーは隠れ、フォーカス中エディタの状態だけが下端の共有バーへ流れる。
        control.SetSharedStatusBar(EditorSharedStatusBar);
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
        // 使用箇所一覧（Find References / gr）：エディタは結果を表示せず FindReferencesResult を
        // 発火するだけなので、ホストが受けてポップアップに一覧表示する（ShellWindow.References.cs）。
        control.FindReferencesResult += OnEditorFindReferencesResult;
        // 右クリックメニューへ「AIに聞く」「ブラウザで調べる」を追加する（選択時のみ・sk0ya.Editor.Controls 1.0.19）。
        control.ContextMenuBuilding += OnEditorContextMenuBuilding;

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
        // デバッグ：ブレークポイント列を有効化し、トグル/同期/実行行ハイライトを配線する。
        WireEditorForDebug(control);
        return control;
    }

    private void ApplyVimEnabledToOpenEditorTabs()
    {
        // 未実体化タブは実体化しない（生成時に現在の Vim 設定が適用されるため不要）。
        foreach (var tab in _editorTabs)
            if (tab.IsRealized)
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
        view.SetFontLigaturesEnabled(ap.TerminalFontLigatures);
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
        // 未実体化タブは実体化しない（生成時に現在の外観が適用されるため不要）。
        foreach (var tab in _editorTabs)
            if (tab.IsRealized)
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
}
