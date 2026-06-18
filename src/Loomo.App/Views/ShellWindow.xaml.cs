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
using sk0ya.Loomo.App.Input;
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

public partial class ShellWindow : Window
{
    private readonly TerminalService _terminal;
    private readonly EditorService _editor;
    private readonly BrowserService _browser;
    private readonly IWorkspaceService _workspace;
    private readonly TabIconService _tabIcons;
    private readonly AiSettings _settings;
    private readonly EditorSupportRegistry _editorSupports;
    private readonly KeybindingService _keybindings;
    private readonly ShellViewModel _vm;
    /// <summary>キーボードショートカットのディスパッチャ（実効バインド→コマンド実行）。</summary>
    private KeyboardDispatcher? _keyboard;
    private readonly Dictionary<Guid, TerminalWorkspaceTabs> _terminalWorkspaces = new();
    private readonly Dictionary<Guid, EditorWorkspaceTabs> _editorWorkspaces = new();
    private readonly Dictionary<Guid, BrowserWorkspaceTabs> _browserWorkspaces = new();
    private readonly TerminalWorkspaceTabs _scratchTerminalWorkspace = new();
    private readonly EditorWorkspaceTabs _scratchEditorWorkspace = new();
    private readonly BrowserWorkspaceTabs _scratchBrowserWorkspace = new();
    private List<TerminalTab> _terminalTabs = new();
    private List<EditorTab> _editorTabs = new();
    private List<BrowserTab> _browserTabs = new();
    private TerminalWorkspaceTabs? _activeTerminalWorkspace;
    private EditorWorkspaceTabs? _activeEditorWorkspace;
    private BrowserWorkspaceTabs? _activeBrowserWorkspace;
    private TerminalTab? _activeTerminalTab;
    private EditorTab? _activeEditorTab;
    private BrowserTab? _activeBrowserTab;
    /// <summary>FolderTree の単クリックで開いたプレビュータブ（VS Code 風）。1つだけ使い回し、
    /// 別ファイルのクリックで中身を差し替える。編集（modified）された時点で通常タブへ昇格し null へ戻る。</summary>
    private EditorTab? _previewEditorTab;
    // ===== EditorSupport ペイン =====
    // アクティブなエディタタブのファイルに対応する IEditorSupportProvider が登録されていれば
    // （Markdown プレビュー等）、その HTML を専用 WebView2 へ自動表示する。
    // IEditorSupportVisualProvider（CSV/TSV グリッド等）は WebView2 の代わりに WPF コントロールを表示し、
    // IEditorSupportUriProvider（PDF/SVG/HTML 等）はファイルを WebView2 へ直接ナビゲートして表示する。
    private EditorTab? _editorSupportSourceTab;
    private WebView2CompositionControl? _editorSupportView;
    /// <summary>現在ペインへ載せている WPF ビジュアル提供者のビュー（未使用は null）。</summary>
    private FrameworkElement? _editorSupportVisual;
    /// <summary>ContentEdited（グリッド編集→エディタ書き戻し）を購読済みのビジュアル提供者。</summary>
    private readonly HashSet<IEditorSupportVisualProvider> _editorSupportEditSubscribed = new();
    private bool _editorSupportWebEventsAttached;
    private DispatcherTimer? _editorSupportDebounceTimer;
    /// <summary>EditorSupport ペイン表示へのユーザー指定。null は自動（対応プロバイダの有無で開閉）。</summary>
    private bool? _editorSupportUserVisibility;
    /// <summary>EditorSupport の追従先を現在のタブに固定し、アクティブタブ変更では差し替えない。</summary>
    private bool _editorSupportSourcePinned;
    /// <summary>自動開閉中の SetPaneVisible をユーザー操作と区別するガード。</summary>
    private bool _editorSupportAutoToggling;
    /// <summary>プレビュー用仮想ホストの現在のマップ先フォルダ（未マップは null）。</summary>
    private string? _editorSupportMappedFolder;
    /// <summary>WebView2 の初回初期化 Task（起動時に殺到する描画要求が同じ初期化を共有し、多重 EnsureCoreWebView2Async を防ぐ）。</summary>
    private Task<bool>? _editorSupportInitTask;
    /// <summary>HTML 描画要求のシーケンス番号（init 待ちの間に積み重なった要求を最新の1つへ畳む）。</summary>
    private int _editorSupportRenderSeq;
    /// <summary>最新の描画内容（init 完了後・初回 ready 後の再描画に使う）。</summary>
    private string? _editorSupportPendingHtml;
    /// <summary>最新のナビゲート先 URI（PDF 等の URI プロバイダ。HTML 描画時は null）。</summary>
    private string? _editorSupportPendingUri;
    /// <summary>現在 WebView2 が表示中のナビゲート URI（同一 URI への再ナビゲートでスクロール位置を失わないためのガード）。</summary>
    private string? _editorSupportNavigatedUri;
    /// <summary>最新の描画に対応する仮想ホストのマップ先（プロバイダ無しなら null）。</summary>
    private string? _editorSupportPendingMapFolder;
    /// <summary>起動直後の初回ナビゲーション取りこぼし対策（初回完了時に最新内容を一度だけ描き直す）を実施済みか。</summary>
    private bool _editorSupportFirstRenderHealed;

    /// <summary>
    /// WebView2 のユーザーデータフォルダ（Cookie・保存パスワード・サイト権限の保存先）。
    /// 既定だと実行ファイル隣に作られ再ビルドで消えるため、%APPDATA%/Loomo 配下に固定して
    /// パスワード自動保存やフォルダ等の権限許可をセッションをまたいで永続化する。
    /// </summary>
    private static readonly string WebViewUserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "WebView2");
    private bool _syncingSupportFromEditor;
    private bool _syncingEditorFromSupport;
    private bool _editorSupportScrollSyncQueued;
    private double _pendingEditorSupportScrollRatio;
    private WorkspaceSnapshot? _activeWorkspace;
    private DispatcherOperation? _pendingWorkspaceSnapshotSave;
    private const string DefaultBrowserUrl = "https://www.google.com/";

    /// <summary>サイドバーを閉じる直前の幅を保持し、再表示時に復元する。</summary>
    private GridLength _savedSidebarWidth = new(220);

    /// <summary>分割スプリッターのトラック厚（px）。見た目の線は細いが、掴み判定をこの幅で確保する。</summary>
    private const double SplitterThickness = 6;
    /// <summary>一時的に全面表示（ズーム）しているペイン。null なら通常のタイル表示。ツリーは保持する。</summary>
    private PaneKind? _zoomedPane;
    /// <summary>メイン領域のレイアウトツリー（リーフ＝ペイン、スプリット＝行/列の入れ子）。</summary>
    private PaneNode? _root;
    /// <summary>ペイン種別 → そのライブコントロールを内包するルート要素。</summary>
    private readonly Dictionary<PaneKind, FrameworkElement> _paneElements = new();
    /// <summary>ドラッグ判定中に一時的にマウスを捕捉しているタイトル要素。</summary>
    private FrameworkElement? _dragHandle;
    private Point _paneDragStart;
    private bool _paneDragArmed;

    // ===== ドラッグ中のスナップ風プレビュー =====
    private Canvas? _dragCanvas;
    private Border? _dragPreview;       // ドロップ先の半分を塗るプレビュー矩形
    private Border? _dragTargetOutline; // ドロップ先ペイン全体の枠
    private Border? _dragGhost;         // 掴んでいるペインをカーソル追従で示すチップ
    private bool _paneDragging;
    private PaneKind _dragSource;
    private PaneKind? _dragTarget;
    private DropZone? _dragZone;
    /// <summary>ドラッグ元が袖（ミニチュア）か。true なら _dragSource はツリー外のペインで、
    /// ドロップ時は移動でなく配置（入れ替え／分割挿入）になる。</summary>
    private bool _dragFromWing;
    /// <summary>ドロップ先セルの中央ゾーン（=入れ替え）にいるか。端なら分割挿入。</summary>
    private bool _dragCenter;

    // ===== ペイン間フォーカス移動（Ctrl+W h/j/k/l） =====
    /// <summary>直近でキーボードフォーカスを得た領域（移動の起点）。ペイン本体またはサイドバー。</summary>
    private FocusTarget? _focusedRegion;
    /// <summary>リサイズモードのヒント表示が出ているか（モード本体の状態は <see cref="KeyboardDispatcher"/> が持つ）。</summary>
    private bool _resizeMode;
    /// <summary>リサイズ自身が起こすフォーカス移動でモードを抜けてしまうのを防ぐガード。</summary>
    private bool _suppressResizeExit;
    /// <summary>リサイズモード中に表示する操作ヒント（下部中央の小バナー）。</summary>
    private Popup? _resizeHintPopup;

    // ===== ペイン内分割（vim 風 Ctrl+W v/s）。トップレベルの4ペイン木とは独立に各ペインの中身を分割する。 =====
    private PaneSplitView? _editorViews;
    private PaneSplitView? _terminalViews;

    /// <summary>フォーカス移動の対象領域：ペイン本体（<see cref="Pane"/> あり）またはサイドバー（null）。</summary>
    private readonly record struct FocusTarget(PaneKind? Pane, Guid ViewportId = default)
    {
        public bool IsSidebar => Pane is null;
        public static FocusTarget Sidebar => new((PaneKind?)null);
        public static FocusTarget Of(PaneKind kind) => new(kind);
        /// <summary>ペイン内分割のビューポートを指す対象（hjkl 移動でビュー横断に使う）。</summary>
        public static FocusTarget Viewport(PaneKind kind, Guid viewportId) => new(kind, viewportId);
    }

    public ShellWindow(
        ShellViewModel vm,
        TerminalService terminal,
        EditorService editor,
        BrowserService browser,
        IWorkspaceService workspace,
        TabIconService tabIcons,
        AiSettings settings,
        EditorSupportRegistry editorSupports,
        KeybindingService keybindings)
    {
        StartupProfiler.Mark("ShellWindow ctor 開始");
        InitializeComponent();
        StartupProfiler.Mark("InitializeComponent 完了");
        DataContext = vm;
        _vm = vm;
        _terminal = terminal;
        _editor = editor;
        _browser = browser;
        _editor.NewVirtualDocumentTabRequested += OpenVirtualDocumentTab;
        // OpenFileAsync（ツールの write_file/edit_file、Git/Diff ペインの「エディタで開く」等）は
        // ここで専用エディタタブを作成・アクティブ化して開く（FolderTree のファイル活性化と同じ流儀）。
        _editor.FileOpenRequested += async path => await OpenFileInNewEditorTabAsync(path);
        _workspace = workspace;
        _tabIcons = tabIcons;
        _settings = settings;
        _editorSupports = editorSupports;
        _keybindings = keybindings;
        _keyboard = BuildKeyboardDispatcher();
        _terminalTabs = _scratchTerminalWorkspace.Tabs;
        _editorTabs = _scratchEditorWorkspace.Tabs;
        _browserTabs = _scratchBrowserWorkspace.Tabs;

        InitializePanes();

        // サイドバーのスプリッターもペイン用と同じ手触りに：ホバーで光らせ、ダブルクリックで既定幅へ。
        SidebarSplitter.Cursor = Cursors.SizeWE;
        SidebarSplitter.MouseEnter += (_, _) => SidebarSplitter.Background = (Brush)FindResource("Accent");
        SidebarSplitter.MouseLeave += (_, _) => SidebarSplitter.Background = (Brush)FindResource("Border");
        SidebarSplitter.MouseDoubleClick += (_, _) => SidebarColumn.Width = new GridLength(220);

        // サイドバーの開閉に追従して列幅・スプリッターを切り替える
        vm.PropertyChanged += OnShellPropertyChanged;
        vm.Settings.Saved += ApplyVimEnabledToOpenEditorTabs;
        vm.Appearance.AppearanceChanged += ApplyAppearanceToOpenTabs;
        vm.Tabs.TabActivated += OnSidebarTabActivated;
        vm.Tabs.TabCloseRequested += OnSidebarTabCloseRequested;
        vm.Workspaces.WorkspaceActivated += OnWorkspaceActivated;
        StateChanged += OnWindowStateChanged;
        Closing += OnClosing;
        Loaded += OnLoaded;

        // Ctrl+W に続けて h/j/k/l でフォーカスを上下左右の隣接ペインへ移す（vim 風）。
        // Terminal/Editor/AI は WPF コントロールなのでトンネリングの PreviewKeyDown で本体より先に拾う。
        // Browser(WebView2) は内部でキー入力を消費するため、Browser ペインにフォーカスがある間は
        // このナビゲーションが効かない場合がある（既知の制限）。
        PreviewKeyDown += OnPaneNavKey;
        PreviewGotKeyboardFocus += OnWindowPreviewGotKeyboardFocus;
        Deactivated += OnWindowDeactivated;

        var startDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // sk0ya コントロールを生成してホストへ配置し、サービスへ結びつける。
        // ただし起動時に復元するワークスペースがある場合、OnLoaded の SwitchWorkspaceAsync が
        // スクラッチタブを Detach して作り直すため、ここでの端末/エディタ生成（各 ~150-300ms の
        // コントロール実体化）は捨てられる純粋な無駄になる。復元予定が無い時だけ作る。
        if (vm.Workspaces.ActiveWorkspace is null)
        {
            var termTab = CreateTerminalTab(startDir);
            _terminalTabs.Add(termTab);
            _vm.Tabs.AddTerminalTab(termTab.Id, termTab.View.HeaderTitle, false);
            ActivateTerminalTab(termTab.Id);
            _terminal.SetWorkingDirectory(startDir);
            UpdateTerminalTab(termTab, termTab.View.HeaderTitle);
            StartupProfiler.Mark("初期ターミナルタブ生成完了");

            var editorTab = CreateEditorTab();
            _editorTabs.Add(editorTab);
            _vm.Tabs.AddEditorTab(editorTab.Id, editorTab.Control.FilePath, editorTab.Control.IsModified, false);
            ActivateEditorTab(editorTab.Id);
            UpdateEditorTab(editorTab);
            StartupProfiler.Mark("初期エディタタブ生成完了");
        }

        // フォルダを開いたらエージェントの作業ディレクトリを同期
        _workspace.RootChanged += (_, root) =>
        {
            if (_activeTerminalTab is not { } activeTerminal)
                return;

            if (!string.IsNullOrEmpty(root))
                _terminal.SetWorkingDirectory(root);
            UpdateTerminalTab(activeTerminal, activeTerminal.View.HeaderTitle);
        };

        // FolderTree の単クリックはプレビュータブ（編集するまで確定せず中身が差し替わる）で開き、
        // ダブルクリック・Enter は通常のエディタタブとして確定する。
        vm.FolderTree.FilePreviewRequested += async (_, path) => await OpenFileInPreviewTabAsync(path);
        vm.FolderTree.FileActivated += async (_, path) => await OpenFileInNewEditorTabAsync(path);
        // FolderTree の HTML を「ブラウザで開く」とアプリ内ブラウザの新規タブで開く。
        vm.FolderTree.OpenInBrowserRequested += async (_, path) => await OpenFileInBrowserAsync(path);
        // FolderTree の「ターミナルにセット」：フォルダは cd、ファイルはパスをプロンプトへ入力する。
        vm.FolderTree.SetInTerminalRequested += OnSetInTerminalRequested;
        // FolderTree のピン留め・表示ルート切替をワークスペーススナップショットへ保存する。
        vm.FolderTree.RootStateChanged += (_, _) => SaveActiveWorkspaceSnapshot();
        // Git セッションの「DIFF ペインで差分を表示」：Diff ペインを表示してフォーカスする。
        vm.GitSession.DiffOpenRequested += (_, _) =>
        {
            SetPaneVisible(PaneKind.Diff, true);
            FocusPane(PaneKind.Diff);
        };
        // サイドバー Git パネルの「差分を開く」も同様に Diff ペインを表示してフォーカスする。
        vm.GitPanel.DiffOpenRequested += (_, _) =>
        {
            SetPaneVisible(PaneKind.Diff, true);
            FocusPane(PaneKind.Diff);
        };
        // Git ペインが（レイアウト復元等で）表示されたら状態を遅延読込し、見えている間だけライブ監視する。
        GitPane.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                _vm.GitSession.EnsureLoaded();
                _vm.GitSession.StartLiveTracking();
            }
            else
                _vm.GitSession.StopLiveTracking();
        };
        // Diff ペインも同様に、初表示で遅延読込し、見えている間だけライブ監視する。
        DiffPane.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                _vm.DiffSession.EnsureLoaded();
                _vm.DiffSession.StartLiveTracking();
            }
            else
                _vm.DiffSession.StopLiveTracking();
        };
        TracePane.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                _vm.TraceSession.EnsureLoaded();
        };
        InitializePegboard();
        StartupProfiler.Mark("ShellWindow ctor 完了");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartupProfiler.Mark("OnLoaded 開始");
        try
        {
            if (_vm.Workspaces.ActiveWorkspace is { } workspace)
                await SwitchWorkspaceAsync(workspace, captureCurrent: false, deferHydration: true);
            else
            {
                LoadLayouts(System.Array.Empty<SavedLayout>(), scratch: null, activeIndex: -1, dirty: false);
                PrepareStageSnapshot(solo: true, StageSnapshot.Default());
                ApplyDefaultLayout();
                BrowserAddressBox.Text = DefaultBrowserUrl;
                // WebView2 の生成は遅延（Browser ペインが見えたら背景で実体化する）。
                CreateBrowserTab(DefaultBrowserUrl);
                CompleteStageSnapshotRestore();
            }
        }
        catch (Exception ex)
        {
            BrowserAddressBox.Text = $"WebView2 initialization failed: {ex.Message}";
        }

        // タイトルバーのブランチ表示は Git ペインを開かなくても要るため、
        // 初期描画が落ち着いてから Git 状態を遅延読込する。
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => _vm.GitSession.EnsureLoaded()));

        StartupProfiler.Mark("OnLoaded 完了");
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ShellViewModel vm) return;
        if (e.PropertyName == nameof(ShellViewModel.IsSidebarVisible))
            ApplySidebarVisibility(vm.IsSidebarVisible);
        else if (e.PropertyName == nameof(ShellViewModel.IsSettingsOverlayOpen) && vm.IsSettingsOverlayOpen)
            EnsureSettingsOverlayCreated();
    }

    /// <summary>設定オーバーレイの中身は初回オープン時にだけ生成する（起動コストを払わない）。
    /// DataContext は ContentControl から ShellViewModel を継承する。</summary>
    private void EnsureSettingsOverlayCreated()
    {
        if (SettingsOverlayHost.Content is null)
            SettingsOverlayHost.Content = new SettingsOverlayView();
    }

    private void ApplySidebarVisibility(bool visible)
    {
        if (visible)
        {
            SidebarColumn.MinWidth = 120;
            SidebarColumn.Width = _savedSidebarWidth.Value > 0 ? _savedSidebarWidth : new GridLength(220);
            SidebarSplitterColumn.Width = new GridLength(SplitterThickness);
            SidebarContainer.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            _savedSidebarWidth = SidebarColumn.Width;
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            SidebarSplitterColumn.Width = new GridLength(0);
            SidebarContainer.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
        }
    }
}
