
namespace sk0ya.Loomo.App.Views;

public partial class ShellWindow : Window
{
    private readonly TerminalService _terminal;
    private readonly EditorService _editor;
    private readonly BrowserService _browser;
    private readonly IWorkspaceService _workspace;
    private readonly IWorkspaceSearchService _search;
    private readonly PaletteSearchCoordinator _paletteSearch;
    private readonly TabIconService _tabIcons;
    private readonly AiSettings _settings;
    private readonly ShellAppearanceCoordinator _appearance;
    private readonly EditorSupportNavigationService _editorSupportNavigation;
    private readonly EditorSupportRegistry _editorSupports;
    // 対応プロバイダの無いバイナリのフォールバック表示（Hex ダンプ）。registry 外。
    private readonly HexEditorSupport _hexSupport;
    // 専用プロバイダの無いコードファイルのフォールバック表示（LSP 構造アウトライン）。registry 外。
    private readonly CodeEditorSupport _codeSupport;
    // コード案内ページの「インストール」導線に使う LSP 管理サービス（判定＋可視ターミナル実行）。
    private readonly sk0ya.Loomo.Services.Lsp.LspManagementService _lspManagement;
    private readonly KeybindingService _keybindings;
    private readonly ShellViewModel _vm;
    // キーボードショートカットのディスパッチャ（実効バインド→コマンド実行）。
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
    // FolderTree の単クリックで開いたプレビュータブ（VS Code 風）。1つだけ使い回し、 別ファイルのクリックで中身を差し替える。編集（modified）された時点で通常タブへ昇格し null へ戻る。
    private EditorTab? _previewEditorTab;
    // ===== EditorSupport ペイン =====
    // アクティブなエディタタブのファイルに対応する IEditorSupportProvider が登録されていれば
    // （Markdown プレビュー等）、その HTML を専用 WebView2 へ自動表示する。
    // IEditorSupportVisualProvider（CSV/TSV グリッド等）は WebView2 の代わりに WPF コントロールを表示し、
    // IEditorSupportUriProvider（PDF/SVG/HTML 等）はファイルを WebView2 へ直接ナビゲートして表示する。
    private readonly EditorSupportController _editorSupport;
    private DispatcherTimer? _editorSupportDebounceTimer;
    // プレビュー用仮想ホストの現在のマップ先フォルダ（未マップは null）。 WebView2 の初回初期化 Task（起動時に殺到する描画要求が同じ初期化を共有し、多重 EnsureCoreWebView2Async を防ぐ）。
    // 最新の描画内容（init 完了後・初回 ready 後の再描画に使う）。
    // 最新の本文差し替え内容（同一ページの編集中のみ。フル HTML 描画時は null）。
    // 最新描画のページ体裁の鍵（インクリメンタル提供者のみ。フル再構築の要否判定に使う）。
    // WebView2 が読込完了し本文差し替え（setBody）を受け付けられるページ体裁の鍵。一致する間はフル再ナビゲートしない。
    // 最新のナビゲート先 URI（PDF 等の URI プロバイダ。HTML 描画時は null）。
    // 現在 WebView2 が表示中のナビゲート URI（同一 URI への再ナビゲートでスクロール位置を失わないためのガード）。
    // 最新の描画に対応する仮想ホストのマップ先（プロバイダ無しなら null）。
    // 起動直後の初回ナビゲーション取りこぼし対策（初回完了時に最新内容を一度だけ描き直す）を実施済みか。
    // プレビューページ（フル HTML）を WebView2 へ配信する一時フォルダ。NavigateToString の 約 2MB 上限を避け、大きな Markdown でもページを表示するためファイル経由で配信する。
    private static readonly string EditorSupportPreviewFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "WebView2", "preview-page");
    // プレビューページの配信バージョン（?v= に載せて毎回新 URL にし WebView2 のキャッシュを避ける）。

    // WebView2 のユーザーデータフォルダ（Cookie・保存パスワード・サイト権限の保存先）。 既定だと実行ファイル隣に作られ再ビルドで消えるため、%APPDATA%/Loomo 配下に固定して パスワード自動保存やフォルダ等の権限許可をセッションをまたいで永続化する。
    private static readonly string WebViewUserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "WebView2");

    // ワークスペース内の file:// ページから、同じワークスペースに置いた JS・JSON 等を読み込めるようにする。 Loomo はユーザーが明示的に開いた開発ワークスペースを扱うため、URL を仮想ホストへ変換せず Chromium のローカルファイル間アクセスを許可し、既存ページの location.href/file: 判定を保つ。
    private const string WebViewAdditionalBrowserArguments = "--allow-file-access-from-files";

    private static CoreWebView2CreationProperties CreateWebViewCreationProperties()
        => new()
        {
            UserDataFolder = WebViewUserDataFolder,
            AdditionalBrowserArguments = WebViewAdditionalBrowserArguments
        };
    private bool _syncingEditorFromSupport;
    private WorkspaceSnapshot? _activeWorkspace;
    private DispatcherOperation? _pendingWorkspaceSnapshotSave;
    private const string DefaultBrowserUrl = "https://www.google.com/";

    // サイドバーを閉じる直前の幅を保持し、再表示時に復元する。
    private GridLength _savedSidebarWidth = new(220);

    // 分割スプリッターのトラック厚（px）。見た目の線は細いが、掴み判定をこの幅で確保する。
    private const double SplitterThickness = 6;
    // 一時的に全面表示（ズーム）しているペイン。null なら通常のタイル表示。ツリーは保持する。
    private PaneKind? _zoomedPane;
    // メイン領域のレイアウトツリー（リーフ＝ペイン、スプリット＝行/列の入れ子）。
    private readonly PaneLayoutCoordinator _paneLayout = new();
    private PaneNode? _root { get => _paneLayout.Root; set => _paneLayout.Root = value; }
    // ペイン種別 → そのライブコントロールを内包するルート要素。
    private readonly Dictionary<PaneKind, FrameworkElement> _paneElements = new();
    // ドラッグ判定中に一時的にマウスを捕捉しているタイトル要素。
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
    // ドラッグ元が袖（ミニチュア）か。true なら _dragSource はツリー外のペインで、 ドロップ時は移動でなく配置（入れ替え／分割挿入）になる。
    private bool _dragFromWing;
    // ソロモードのミニチュアからのドラッグか。true ならドロップ先は舞台1枚で、 中央＝舞台のペインを入れ替え・端＝レイアウトモードへ切り替えて分割挿入になる （HandleStageDrop）。
    private bool _stageDrag;
    // ドロップ先セルの中央ゾーン（=入れ替え）にいるか。端なら分割挿入。
    private bool _dragCenter;
    // セルの外縁ぎりぎりにいて、単体ペインではなく（直交する）祖先スプリット全体の辺へ 落とす「スパン挿入」状態か（例：左右2ペインの下にフル幅で挿入）。
    private bool _dragSpan;

    // ===== ペイン間フォーカス移動（Ctrl+W h/j/k/l） =====
    // 直近でキーボードフォーカスを得た領域（移動の起点）。ペイン本体またはサイドバー。
    private FocusTarget? _focusedRegion;
    // リサイズモードのヒント表示が出ているか（モード本体の状態は KeyboardDispatcher が持つ）。
    private bool _resizeMode;
    // リサイズ自身が起こすフォーカス移動でモードを抜けてしまうのを防ぐガード。
    private bool _suppressResizeExit;
    // リサイズモード中に表示する操作ヒント（下部中央の小バナー）。
    private Popup? _resizeHintPopup;

    // ===== ペイン内分割（vim 風 Ctrl+W v/s）。トップレベルの4ペイン木とは独立に各ペインの中身を分割する。 =====
    private PaneSplitView? _editorViews;
    private PaneSplitView? _terminalViews;

    // フォーカス移動の対象領域：ペイン本体（Pane あり）またはサイドバー（null）。
    private readonly record struct FocusTarget(PaneKind? Pane, Guid ViewportId = default)
    {
        public bool IsSidebar => Pane is null;
        public static FocusTarget Sidebar => new((PaneKind?)null);
        public static FocusTarget Of(PaneKind kind) => new(kind);
        // ペイン内分割のビューポートを指す対象（hjkl 移動でビュー横断に使う）。
        public static FocusTarget Viewport(PaneKind kind, Guid viewportId) => new(kind, viewportId);
    }

    public ShellWindow(
        ShellViewModel vm,
        TerminalService terminal,
        EditorService editor,
        BrowserService browser,
        IWorkspaceService workspace,
        IWorkspaceSearchService search,
        TabIconService tabIcons,
        AiSettings settings,
        EditorSupportRegistry editorSupports,
        HexEditorSupport hexSupport,
        CodeEditorSupport codeSupport,
        sk0ya.Loomo.Services.Lsp.LspManagementService lspManagement,
        sk0ya.Loomo.Services.GitService git,
        KeybindingService keybindings)
    {
        StartupProfiler.Mark("ShellWindow ctor 開始");
        InitializeComponent();
        StartupProfiler.Mark("InitializeComponent 完了");
        DataContext = vm;
        _trailBar = new TrailBarController(vm.Trail, TrailScroll, TrailDots,
            TrailDateTimePopup, TrailCalendar, TrailDateTimePopupRoot, JumpToTrailEntry);
        _vm = vm;
        _terminal = terminal;
        _editor = editor;
        _browser = browser;
        _editor.NewVirtualDocumentTabRequested += OpenVirtualDocumentTab;
        // OpenFileAsync（ツールの write_file/edit_file、Git/Diff ペインの「エディタで開く」等）は
        // ここで専用エディタタブを作成・アクティブ化して開く（FolderTree のファイル活性化と同じ流儀）。
        _editor.FileOpenRequested += async path => await OpenFileInNewEditorTabAsync(path);
        _workspace = workspace;
        _search = search;
        _paletteSearch = new PaletteSearchCoordinator(search);
        _tabIcons = tabIcons;
        _settings = settings;
        _appearance = new ShellAppearanceCoordinator(settings, () =>
            (Application.Current?.TryFindResource("Accent") as SolidColorBrush)?.Color
            ?? Color.FromRgb(0x61, 0x48, 0xDE));
        _editorSupportNavigation = new EditorSupportNavigationService(EditorSupportPreviewFolder);
        var editorSupportWebView = new EditorSupportWebViewController(
            EditorSupportContentHost, _editorSupportNavigation, CreateWebViewCreationProperties,
            EditorSupport_WebMessageReceived, EditorSupport_ContextMenuRequested);
        _editorSupport = new EditorSupportController(editorSupportWebView);
        editorSupportWebView.NavigationCompleted += (_, _) =>
        {
            if (_editorSupport.Source is not null)
                PostEditorSupportScrollRatio(_editorSupport.Source.Control.VerticalScrollRatio);
        };
        _editorSupports = editorSupports;
        _hexSupport = hexSupport;
        _codeSupport = codeSupport;
        _lspManagement = lspManagement;
        _git = git;
        _keybindings = keybindings;
        _keyboard = BuildKeyboardDispatcher();
        _terminalTabs = _scratchTerminalWorkspace.Tabs;
        _editorTabs = _scratchEditorWorkspace.Tabs;
        _browserTabs = _scratchBrowserWorkspace.Tabs;

        InitializePanes();
        HookBranchSwitchers();

        // マウスのサイドボタン（戻る=XButton1／進む=XButton2）でエディタのファイル履歴を行き来する
        // （IDE 標準の手触り）。Window レベルのトンネル（Preview）で各 WPF ペインより先に受ける。
        PreviewMouseDown += OnShellPreviewMouseNavigate;

        // サイドバーのスプリッターもペイン用と同じ手触りに：ホバーで光らせ、ダブルクリックで既定幅へ。
        SidebarSplitter.Cursor = Cursors.SizeWE;
        SidebarSplitter.MouseEnter += (_, _) => SidebarSplitter.Background = (Brush)FindResource("Accent");
        SidebarSplitter.MouseLeave += (_, _) => SidebarSplitter.Background = (Brush)FindResource("Border");
        SidebarSplitter.MouseDoubleClick += (_, _) => SidebarColumn.Width = new GridLength(220);
        // ドラッグ中は袖（ミニチュア）の組み直しを止める（RebuildWings の強制 UpdateLayout が
        // GridSplitter のドラッグ中キャプチャを奪い、ドラッグが何度も分断されて離した瞬間の幅が
        // 実質ランダムになる＝「リサイズしても元に戻る」不具合の実体だった）。
        SidebarSplitter.DragStarted += (_, _) => _paneSplitterDragging = true;
        SidebarSplitter.DragCompleted += (_, _) =>
        {
            _paneSplitterDragging = false;
            PaneLayoutDebugLog.Log($"SidebarSplitter DragCompleted -> SidebarColumn.Width={SidebarColumn.Width}");
            ScheduleLayoutWings();
        };

        // 診断用：誰が（どの経路で）SidebarColumn / WingColumn の幅を書き換えているかを追跡する
        // （LOOMO_PANE_DEBUG=1 のときだけ %APPDATA%/Loomo/panelayout-debug.log へ記録）。
        if (PaneLayoutDebugLog.Enabled)
        {
            DependencyPropertyDescriptor.FromProperty(ColumnDefinition.WidthProperty, typeof(ColumnDefinition))
                ?.AddValueChanged(SidebarColumn, (_, _) =>
                    PaneLayoutDebugLog.Log($"SidebarColumn.Width -> {SidebarColumn.Width}", withCaller: true));
            DependencyPropertyDescriptor.FromProperty(ColumnDefinition.WidthProperty, typeof(ColumnDefinition))
                ?.AddValueChanged(WingColumn, (_, _) =>
                    PaneLayoutDebugLog.Log($"WingColumn.Width -> {WingColumn.Width}", withCaller: true));
        }

        // サイドバーの開閉に追従して列幅・スプリッターを切り替える
        vm.PropertyChanged += OnShellPropertyChanged;
        vm.Settings.Saved += ApplyVimEnabledToOpenEditorTabs;
        vm.Settings.Saved += ApplyEditorSettingsToOpenEditorTabs;
        vm.Appearance.AppearanceChanged += ApplyAppearanceToOpenTabs;
        vm.Appearance.AppearanceChanged += RebuildWings;
        vm.Tabs.TabActivated += OnSidebarTabActivated;
        vm.Tabs.TabCloseRequested += OnSidebarTabCloseRequested;
        vm.Tabs.TabCloseOthersRequested += OnSidebarTabCloseOthersRequested;
        vm.Tabs.TabCloseAllRequested += OnSidebarTabCloseAllRequested;
        vm.Tabs.TabDetachRequested += OnSidebarTabDetachRequested;
        vm.Workspaces.WorkspaceActivated += OnWorkspaceActivated;
        vm.Workspaces.WorkspaceRemoved += OnWorkspaceRemoved;
        InitializeDebugWiring();
        StateChanged += OnWindowStateChanged;
        Closing += OnClosing;
        Closed += OnClosed;
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
            _vm.Tabs.AddEditorTab(editorTab.Id, editorTab.PeekFilePath, editorTab.PeekIsModified, false);
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
        // FolderTree でのリネーム／削除を、開いているエディタタブへ反映する（パス追従／タブを閉じる）。
        vm.FolderTree.EntryRenamed += (_, e) => OnFolderTreeEntryRenamed(e);
        vm.FolderTree.EntryDeleted += (_, path) => OnFolderTreeEntryDeleted(path);
        // FolderTree の「現在のファイルを選択」ボタン／ショートカット：エディタでアクティブな
        // ファイルをツリーで展開・選択する（同期）。
        vm.FolderTree.RevealCurrentFileRequested += (_, _) => RevealActiveFileInFolderTree();
        // サイドバー検索：選択ヒットはプレビュータブで該当行へ、確定（Enter/ダブルクリック）は通常タブへ開く。
        vm.SearchPanel.PreviewRequested += async (_, h) =>
        {
            await OpenFileInPreviewTabAsync(h.FullPath);
            // 検索ヒットは1始まり、NavigateTo は0始まりなので変換する。
            _activeEditorTab?.Control.NavigateTo(h.Line - 1, Math.Max(0, h.Column - 1));
            // grep のリテラル検索ワードはエディタで全マッチをハイライトする（ファイル名検索／正規表現は空）。
            _activeEditorTab?.Control.HighlightSearch(h.Highlight);
        };
        vm.SearchPanel.ActivateRequested += async (_, h) =>
        {
            await OpenFileInNewEditorTabAsync(h.FullPath);
            // 検索ヒットは1始まり、NavigateTo は0始まりなので変換する。
            _activeEditorTab?.Control.NavigateTo(h.Line - 1, Math.Max(0, h.Column - 1));
            _activeEditorTab?.Control.HighlightSearch(h.Highlight);
        };
        // 検索ワードを消す／Esc／ファイル名検索へ切替で、エディタの検索ハイライトを消す。
        vm.SearchPanel.ClearHighlightRequested += (_, _) => _activeEditorTab?.Control.HighlightSearch("");
        // サイドバー検索「ターミナル」モード：アクティブなターミナルの一致を供給し、選択でその箇所へジャンプする。
        vm.SearchPanel.TerminalSearchProvider = (query, caseSensitive) =>
        {
            if (_activeTerminalTab?.View is not { } view || string.IsNullOrWhiteSpace(query))
                return Array.Empty<TerminalSearchHit>();
            return view.FindMatches(query, caseSensitive)
                .Select(m => new TerminalSearchHit(m.LineIndex, m.Column, m.Length, m.LineText))
                .ToList();
        };
        vm.SearchPanel.TerminalRevealRequested += (_, h) =>
        {
            if (_activeTerminalTab?.View is not { } view)
                return;
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Terminal);
            view.SelectMatch(new TerminalMatch(h.LineIndex, h.Column, h.Length, h.LineText));
            view.FocusTerminal();
        };
        // FolderTree の「ターミナルにセット」：フォルダは cd、ファイルはパスをプロンプトへ入力する。
        vm.FolderTree.SetInTerminalRequested += OnSetInTerminalRequested;
        // FolderTree の「このフォルダーで検索」：検索パネルを開き、そのフォルダを検索の開始フォルダーにする。
        vm.FolderTree.SearchInFolderRequested += (_, path) =>
        {
            vm.SearchPanel.SetSearchRoot(path);
            vm.RevealSearchPanel();
        };
        // 検索の既定の開始フォルダーを FolderTree の表示ルートに追従させる。
        vm.FolderTree.CurrentRootChanged += (_, root) => vm.SearchPanel.SetDefaultRoot(root);
        // FolderTree の「AI-誤字脱字チェック」：AIバーを /clear して当該ファイルの誤字脱字チェックを実行する。
        vm.FolderTree.TypoCheckRequested += (_, path) => vm.AiBar.RunTypoCheck(path);
        // FolderTree の「AIワークフロー」：AIバーをワークフローモードへ切替え、ファイルを構造化 input として実行する。
        vm.FolderTree.WorkflowRequested += (_, req) => RunWorkflowWithInput(req.WorkflowId, req.Input);
        // FolderTree の「Git」>「Git Blame」：エディタペインでファイルを開き、VimEditorControl
        // のネイティブ Git Blame 表示（:Gblame。行ごとの短縮ハッシュ・著者・日付をインライン表示する
        // トグル。GitServiceFactory で渡している GitDiffProvider が実処理を持つので、ここではファイルを
        // 開いて ExecuteCommand で ex コマンドを流すだけでよい）をトリガーする。
        vm.FolderTree.GitBlameRequested += async (_, fullPath) =>
        {
            fullPath = Path.GetFullPath(fullPath);
            await OpenFileInNewEditorTabAsync(fullPath);
            FocusPane(PaneKind.Editor);
            var tab = _editorTabs.FirstOrDefault(t =>
                string.Equals(t.PeekFilePath, fullPath, StringComparison.OrdinalIgnoreCase));
            tab?.Control.ExecuteCommand("Gblame");
        };
        // FolderTree の「Git」>「履歴を表示」：Git ペインを前面に出し、そのファイル／フォルダの履歴に絞る。
        vm.FolderTree.GitHistoryRequested += async (_, fullPath) => await ShowGitHistoryAsync(fullPath);
        // FolderTree のピン留め・表示ルート切替をワークスペーススナップショットへ保存する。
        vm.FolderTree.RootStateChanged += (_, _) => SaveActiveWorkspaceSnapshot();
        // Git セッションの「DIFF ペインで差分を表示」：エディタ/「AIに聞く」等と同じく、出ていなければ
        // 左上ペインと入れ替えて前面に出す（最下段への新規挿入ではなく入れ替えで一貫させる）。
        vm.GitSession.DiffOpenRequested += (_, _) =>
        {
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Diff);
            FocusPane(PaneKind.Diff);
        };
        // Diff で単一コミットを表示中の「Git 一覧で表示」：一覧を全履歴へ戻して対象を選択し、
        // Git ペインを前面に出す。古いコミットは VM 側がページを追加取得して見つける。
        vm.DiffSession.CommitOpenInGitRequested += async (_, hash) =>
        {
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Git);
            await vm.GitSession.SelectCommitAsync(hash);
            FocusPane(PaneKind.Git);
        };
        // サイドバー Git パネルの「差分を開く」も同様に左上入れ替えで Diff ペインを前面に出す。
        vm.GitPanel.DiffOpenRequested += (_, _) =>
        {
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Diff);
            FocusPane(PaneKind.Diff);
        };
        // Git の状態変化（チェックアウト・pull・外部変更検出等）を受けて、開いているエディタタブと
        // EditorSupport プレビューをディスクの最新内容へ追従させる（ブランチ切り替えでファイルが
        // 書き換わる/消える/元に戻るケースで古い内容のまま取り残されるのを防ぐ）。UI スレッドとは
        // 限らないイベントなので Dispatcher へ積む。
        vm.GitSession.RepositoryChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => _ = RefreshOpenEditorTabsFromDiskAsync()));
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
        InitializeTrail();
        StartupProfiler.Mark("ShellWindow ctor 完了");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartupProfiler.Mark("OnLoaded 開始");
        // カクつき計測（LOOMO_JANK_PROFILE=1 のときだけ動く。既定は完全に無効）。
        UiJankProfiler.Start(Dispatcher);
        try
        {
            if (_vm.Workspaces.ActiveWorkspace is { } workspace)
                await SwitchWorkspaceAsync(workspace, captureCurrent: false, deferHydration: true);
            else
            {
                LoadLayouts(System.Array.Empty<SavedLayout>(), scratch: null, activeIndex: -1, dirty: false);
                ApplyIdePaneApplicability(root: null);
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

        // ドラッグ用オーバーレイを先に実体化しておく（初回ドラッグで生成・表示と同フレームに掴むと
        // IsVisible 未確定で Mouse.Capture が失敗し、ミニチュアのドラッグが不発になるため）。
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(EnsureDragOverlay));

        StartupProfiler.Mark("OnLoaded 完了");
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ShellViewModel vm) return;
        if (e.PropertyName == nameof(ShellViewModel.IsSidebarVisible))
        {
            ApplySidebarVisibility(vm.IsSidebarVisible);
            // 同じパネルを閉じて再表示した場合は ActivePanel が変化しないため、表示側でも記録する。
            if (vm.IsSidebarVisible)
                RecordTrailPanel(vm.ActivePanel);
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsSettingsOverlayOpen) && vm.IsSettingsOverlayOpen)
            EnsureSettingsOverlayCreated();
        else if (e.PropertyName == nameof(ShellViewModel.ActivePanel))
            RecordTrailPanel(vm.ActivePanel);   // サイドバーのパネル切替も軌跡（操作ログ）へ
    }

    // 設定オーバーレイの中身は初回オープン時にだけ生成する（起動コストを払わない）。 DataContext は ContentControl から ShellViewModel を継承する。
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
