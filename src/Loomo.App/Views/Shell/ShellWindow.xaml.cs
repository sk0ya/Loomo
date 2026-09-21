namespace sk0ya.Loomo.App.Views;
public partial class ShellWindow : Window {
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) {
        base.OnRenderSizeChanged(sizeInfo);
        if (!IsLoaded || _paneFullscreen)
            return;
        if (_stageActive)
            QueueStageResize();
        // 袖のミニチュアは幅にしか追従しない（描画元の幅＝Main 領域の幅）。高さだけの変化でも
        // 組み直していると、窓の下辺を掴んだだけで1刻みごとにカードを全部作り直すことになる。
        else if (!_dockActive && sizeInfo.WidthChanged && WingHost.Visibility == Visibility.Visible)
            ScheduleLayoutWings();
    }

    private readonly TerminalService _terminal;
    private readonly EditorService _editor;
    private readonly BrowserService _browser;
    private readonly IWorkspaceService _workspace;
    // コマンドパレットの「探して飛ぶ」側（§24.2）。検索は検索ペインと同じ実装（ripgrep ／ 無ければ
    // インプロセス走査）を共有するので、ファイル名・全文の当たり方が2か所で食い違わない。
    // 待ち・キャンセル・供給元の振り分けは Coordinator 側（ShellWindow 責務境界）。
    private readonly PaletteSearchCoordinator _paletteSearch;
    private readonly CommandPaletteViewController _paletteView;
    private readonly TabIconService _tabIcons;
    private readonly DiffSessionFactory _diffSessions;
    private readonly LoomoSettings _settings;
    private readonly TaskbarWorkspaceRecentService _taskbarWorkspaceRecent;
    private readonly ShellAppearanceCoordinator _appearance;
    private readonly EditorSupportNavigationService _editorSupportNavigation;
    private readonly EditorSupportRegistry _editorSupports;
    private readonly EditorSupportResolver _editorSupportResolver;
    private readonly CodeEditorSupport _codeSupport;
    private readonly sk0ya.Loomo.Services.Lsp.LspManagementService _lspManagement;
    private readonly EditorLspNoticeCoordinator _editorLspNotices;
    // LSP セッションはワークスペース単位でアプリに1つ。タブ経由ではなくここから直接使う（設計書 §30）。
    // 型が実装（ILspWorkspace ではなく）なのは、サーバーの実行時状態（ServerStatuses＝起動失敗の理由）が
    // Loomo 側の概念で、エディタへ渡すインターフェースには載っていないため（案内の出し分けに要る）。
    private readonly sk0ya.Loomo.Services.Lsp.LspWorkspaceService _lspWorkspace;
    private readonly Editor.Core.Engine.VimEngineServices _editorEngineServices;
    /// <summary>入力の先読みを作る別プロセスへの窓口。設定が無効／モデル未設定／ワーカーが落ちている、
    /// のいずれでも黙って何も返さない——先読みの不調で入力が止まる経路を作らない。</summary>
    private readonly sk0ya.Loomo.Ai.Completion.FimCompletionClient _fimCompletion;
    private readonly ILspServerAdmin _lspServerAdmin;
    private readonly KeybindingService _keybindings;
    private readonly FileAiSelectionContextBuilder _fileAiSelection;
    private readonly sk0ya.Loomo.CSharp.Projects.ISolutionModelService? _solutionModel;
    private readonly sk0ya.Loomo.CSharp.Configuration.StyleCopDiagnosticService _styleCopDiagnostics;
    private readonly sk0ya.Loomo.CSharp.Configuration.StyleCopCodeFixService _styleCopCodeFix;
    private readonly sk0ya.Loomo.CSharp.Configuration.CSharpCompilerDiagnosticService _compilerDiagnostics;
    private readonly sk0ya.Loomo.CSharp.Configuration.CSharpEditorConfigService _csharpEditorConfig;
    /// <summary>診断の文面に「なぜ」を添える係（§30.19）。規則名だけでは誤検知に見える診断がある。</summary>
    private readonly sk0ya.Loomo.CSharp.Configuration.CSharpDiagnosticExplanationService _diagnosticExplanations;
    private CancellationTokenSource? _fileAiPreparationCts;
    private readonly ShellViewModel _vm;
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
    private EditorTab? _previewEditorTab;
    private readonly EditorSupportController _editorSupport;
    private DispatcherTimer? _editorSupportDebounceTimer;
    private static readonly string EditorSupportPreviewFolder = Services.WebViewProfile.PreviewPageFolder;
    private static readonly string WebViewUserDataFolder = Services.WebViewProfile.UserDataFolder;
    // 生成条件（プロファイル・ブラウザ引数・拡張機能）の正本は WebViewEnvironment（§21.5.3）。
    // 立て直し（ポートの引き当て直し）で引数が変わりうるので、都度組み立てる。
    private static CoreWebView2CreationProperties CreateWebViewCreationProperties()
        => Services.WebViewEnvironment.CreateProperties();
    private bool _syncingEditorFromSupport;
    private WorkspaceSnapshot? _activeWorkspace;
    private DispatcherOperation? _pendingWorkspaceSnapshotSave;
    private const string DefaultBrowserUrl = "https://www.google.com/";
    private PaneKind? _zoomedPane;
    private readonly PaneLayoutCoordinator _paneLayout = new();
    private PaneNode? _root { get => _paneLayout.Root; set => _paneLayout.Root = value; }
    private readonly Dictionary<PaneKind, FrameworkElement> _paneElements = new();
    private FrameworkElement? _dragHandle;
    private Point _paneDragStart;
    private bool _paneDragArmed;
    private bool _paneDragging;
    private PaneKind _dragSource;
    private PaneKind? _dragTarget;
    private DropZone? _dragZone;
    private bool _dragFromWing;
    private bool _stageDrag;
    private bool _dragCenter;
    private bool _dragSpan;
    private bool _dragToWing;   // 袖の上で離す＝舞台から降ろして袖へしまう
    private PaneFocusTarget? _focusedRegion;
    private bool _resizeMode;
    private bool _suppressResizeExit;
    private Popup? _resizeHintPopup;
    private PaneSplitView? _editorViews;
    private PaneSplitView? _terminalViews;
    public ShellWindow( ShellViewModel vm, TerminalService terminal, EditorService editor, BrowserService browser, IWorkspaceService workspace, TabIconService tabIcons, LoomoSettings settings, TaskbarWorkspaceRecentService taskbarWorkspaceRecent, EditorSupportRegistry editorSupports, EditorSupportResolver editorSupportResolver, CodeEditorSupport codeSupport, IEditorSupportViewFactory editorSupportViewFactory, sk0ya.Loomo.Services.Lsp.LspManagementService lspManagement, sk0ya.Loomo.Services.Lsp.LspWorkspaceService lspWorkspace, ILspServerAdmin lspServerAdmin, Editor.Core.Engine.VimEngineServices editorEngineServices, sk0ya.Loomo.Services.GitService git, KeybindingService keybindings, DiffSessionFactory diffSessions, sk0ya.Loomo.CSharp.Configuration.StyleCopDiagnosticService styleCopDiagnostics, sk0ya.Loomo.CSharp.Configuration.StyleCopCodeFixService styleCopCodeFix, sk0ya.Loomo.CSharp.Configuration.CSharpCompilerDiagnosticService compilerDiagnostics, sk0ya.Loomo.CSharp.Configuration.CSharpEditorConfigService csharpEditorConfig, sk0ya.Loomo.CSharp.Configuration.CSharpDiagnosticExplanationService diagnosticExplanations, IWorkspaceSearchService search, sk0ya.Loomo.Ai.Completion.FimCompletionClient fimCompletion, sk0ya.Loomo.CSharp.Projects.ISolutionModelService? solutionModel = null) {
        StartupProfiler.Mark("ShellWindow ctor 開始");
        InitializeComponent();
        StartupProfiler.Mark("InitializeComponent 完了");
        DataContext = vm;
        _trailBar = new TrailBarController(vm.Trail, TrailScroll, TrailDots, TrailDateTimePopup, TrailCalendar, TrailDateTimePopupRoot, JumpToTrailEntry);
        _vm = vm;
        _ = new ShellWindowStateController(
            this,
            vm,
            SidebarColumn,
            SidebarSplitterColumn,
            SidebarContainer,
            SidebarSplitter,
            RecordTrailPanel,
            FocusSidebar,
            CaptureFocusReturnOrigin,
            RestoreFocusReturnOrigin);
        _terminal = terminal;
        _editor = editor;
        _browser = browser;
        // フロントデバッグ（TS IDE）が dev URL をペインへ出すためのフック：可視化＋フォーカス＋実体化して遷移。
        _browser.ShowAndNavigateRequested = url => ShowBrowserPaneAndNavigateAsync(url);
        _editor.NewVirtualDocumentTabRequested += OpenVirtualDocumentTab;
        _editor.FileOpenRequested += async path => await OpenFileInNewEditorTabAsync(path);
        _workspace = workspace;
        _solutionModel = solutionModel;
        _styleCopDiagnostics = styleCopDiagnostics;
        _styleCopCodeFix = styleCopCodeFix;
        _compilerDiagnostics = compilerDiagnostics;
        _csharpEditorConfig = csharpEditorConfig;
        _diagnosticExplanations = diagnosticExplanations;
        _tabIcons = tabIcons;
        _diffSessions = diffSessions;
        _settings = settings;
        _taskbarWorkspaceRecent = taskbarWorkspaceRecent;
        _appearance = new ShellAppearanceCoordinator(settings, () =>
            (Application.Current?.TryFindResource("Accent") as SolidColorBrush)?.Color
            ?? Color.FromRgb(0x61, 0x48, 0xDE));
        EditorSyntaxColors.Apply(_appearance.BuildEditorTheme()); // Diff 本体の構文色（起動時の1回目）
        _paletteView = new CommandPaletteViewController(
            PaletteList, PaletteBox, PalettePreview,
            (DataTemplate)PaletteBox.FindResource("PaletteCommandRow"),
            (DataTemplate)PaletteBox.FindResource("PaletteNavigationRow"));
        // シンボルの供給口だけは部屋側（LSP セッションは ShellWindow が持つ）。表示パスの綴りは
        // マルチルート対応の ToDisplayPath に任せる。
        _paletteSearch = new PaletteSearchCoordinator(search, async (query, ct) =>
            PaletteNavigationItems.FromSymbols(
                await WorkspaceSymbolSearch.SearchAsync(lspWorkspace, query, isClass: false, ct),
                workspace.ToDisplayPath));
        _editorSupportNavigation = new EditorSupportNavigationService(EditorSupportPreviewFolder);
        // 落ちたインスタンスが置いていった一時ページの掃除（起動を待たせないよう裏で）。
        Task.Run(() => _editorSupportNavigation.CleanStalePages(TimeSpan.FromDays(1)));
        var editorSupportWebView = new EditorSupportWebViewController( EditorSupportContentHost, _editorSupportNavigation, CreateWebViewCreationProperties, EditorSupport_WebMessageReceived, EditorSupport_ContextMenuRequested, editorSupportViewFactory);
        _editorSupport = new EditorSupportController(editorSupportWebView, EditorSupportVisual_ContentEdited);
        editorSupportWebView.NavigationCompleted += (_, _) => {
            if (_editorSupport.Source is not null)
                PostEditorSupportScrollRatio(_editorSupport.Source.Control.VerticalScrollRatio);
            _ = CaptureWebThumbnailAsync(PaneKind.EditorSupport);
        };
        editorSupportWebView.ReloadRequested += OnEditorSupportReloadRequested;
        // Markdown 差分のレンダリング表示（§24.10）。Diff ペインは XAML から生えて DI が届かないので、
        // 部屋の他のプレビューと同じ道具（WebView2 のファクトリと一時ページの置き場）をここで渡す。
        DiffSessionHost.ConfigureMarkdownRender(editorSupportViewFactory, EditorSupportPreviewFolder);
        // 本文のリンクは EditorSupport のプレビューと同じ振り分け（URL＝ブラウザ／ファイル＝エディタ）へ流す。
        DiffSessionHost.MarkdownLinkClicked += (_, e) => _ = HandleEditorSupportLinkClickedAsync(e.Href, e.SourcePath);
        _editorSupports = editorSupports;
        _editorSupportResolver = editorSupportResolver;
        _codeSupport = codeSupport;
        _lspManagement = lspManagement;
        _lspWorkspace = lspWorkspace;
        _editorLspNotices = new EditorLspNoticeCoordinator(lspManagement, lspWorkspace, vm.LspPrompt);
        _editorEngineServices = editorEngineServices;
        _lspServerAdmin = lspServerAdmin;
        _fimCompletion = fimCompletion;
        _git = git;
        _keybindings = keybindings;
        _fileAiSelection = new FileAiSelectionContextBuilder(workspace);
        _keyboard = BuildKeyboardDispatcher();
        _terminalTabs = _scratchTerminalWorkspace.Tabs;
        _editorTabs = _scratchEditorWorkspace.Tabs;
        _browserTabs = _scratchBrowserWorkspace.Tabs;
        InitializePanes();
        HookBranchSwitchers();
        HookWorkspaceSwitcher();
        HookDebugSwitcher();
        HookPaneMenu();
        PreviewMouseDown += OnShellPreviewMouseNavigate;
        SidebarSplitter.Cursor = Cursors.SizeWE;
        SidebarSplitter.MouseEnter += (_, _) => SidebarSplitter.Background = (Brush)FindResource("Accent");
        SidebarSplitter.MouseLeave += (_, _) => SidebarSplitter.Background = (Brush)FindResource("Border");
        SidebarSplitter.MouseDoubleClick += (_, _) => SidebarColumn.Width = new GridLength(220);
        SidebarSplitter.DragStarted += (_, _) => _paneSplitterDragging = true;
        SidebarSplitter.DragCompleted += (_, _) => {
            _paneSplitterDragging = false;
            PaneLayoutDebugLog.Log($"SidebarSplitter DragCompleted -> SidebarColumn.Width={SidebarColumn.Width}");
            ScheduleLayoutWings();
            RebuildStageIfResized();
        };
        WingSplitter.MouseDoubleClick += (_, e) => {
            SetWingWidth(DefaultWingWidth);
            e.Handled = true;
        };
        WingSplitter.DragStarted += (_, _) => _paneSplitterDragging = true;
        WingSplitter.DragDelta += (_, e) => {
            _wingWidth = Math.Clamp(_wingWidth - e.HorizontalChange, MinWingWidth, MaxWingWidth);
            WingColumn.Width = new GridLength(EffectiveWingWidth);
        };
        WingSplitter.DragCompleted += (_, _) => {
            _paneSplitterDragging = false;
            SetWingWidth(_wingWidth);
        };
        if (PaneLayoutDebugLog.Enabled) {
            DependencyPropertyDescriptor.FromProperty(ColumnDefinition.WidthProperty, typeof(ColumnDefinition))
                ?.AddValueChanged(SidebarColumn, (_, _) =>
                    PaneLayoutDebugLog.Log($"SidebarColumn.Width -> {SidebarColumn.Width}", withCaller: true));
            DependencyPropertyDescriptor.FromProperty(ColumnDefinition.WidthProperty, typeof(ColumnDefinition))
                ?.AddValueChanged(WingColumn, (_, _) =>
                    PaneLayoutDebugLog.Log($"WingColumn.Width -> {WingColumn.Width}", withCaller: true));
        }
        InitializeSidebarSections();
        HookAiActivity();
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
        vm.Recent.Changed += (_, _) => SaveActiveWorkspaceSnapshot();
        vm.Recent.NavigationRequested += OnRecentNavigationRequested;
        if (vm.Workspaces.ActiveWorkspace is { } activeWorkspace)
            _taskbarWorkspaceRecent.AddRecent(activeWorkspace);
        InitializeDebugWiring();
        InitializeTestGlyphWiring();
        HookIdeActivity(PaneKind.Debug, _vm.Debug);
        HookIdeActivity(PaneKind.TsIde, _vm.TsIde);
        InitializeProblemsWiring();
        InitializeCSharpDiagnosticsWiring();
        InitializeRefactoringWiring();
        StateChanged += OnWindowStateChanged;
        Closing += OnClosing;
        Closed += OnClosed;
        Loaded += OnLoaded;
        PreviewKeyDown += OnPaneNavKey;
        PreviewGotKeyboardFocus += OnWindowPreviewGotKeyboardFocus;
        Deactivated += OnWindowDeactivated;
        var startDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (vm.Workspaces.ActiveWorkspace is null) {
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
        _workspace.RootChanged += (_, root) => {
            if (_activeTerminalTab is not { } activeTerminal)
                return;
            if (!string.IsNullOrEmpty(root))
                _terminal.SetWorkingDirectory(root);
            UpdateTerminalTab(activeTerminal, activeTerminal.View.HeaderTitle);
        };
        vm.FolderTree.FilePreviewRequested += async (_, path) => await OpenFileInPreviewTabAsync(path);
        vm.FolderTree.FileActivated += async (_, path) => await OpenFileInNewEditorTabAsync(path);
        if (vm.CSharpSolutionExplorer is { } csharpExplorer)
        {
            csharpExplorer.FileOpenRequested += async (_, path) => await OpenFileInNewEditorTabAsync(path);
            csharpExplorer.ActionRequested += OnCSharpSolutionActionRequested;
        }
        vm.FolderTree.OpenInBrowserRequested += async (_, path) => await OpenFileInBrowserAsync(path);
        vm.FolderTree.EntryRenamed += (_, e) => OnFolderTreeEntryRenamed(e);
        vm.FolderTree.EntryDeleted += (_, path) => OnFolderTreeEntryDeleted(path);
        vm.FolderTree.RevealCurrentFileRequested += (_, _) => RevealActiveFileInFolderTree();
        vm.SearchPanel.PreviewRequested += async (_, h) => {
            await OpenFileInPreviewTabAsync(h.FullPath);
            _activeEditorTab?.Control.NavigateTo(h.Line - 1, Math.Max(0, h.Column - 1));
            _activeEditorTab?.Control.HighlightSearch(h.Highlight);
        };
        vm.SearchPanel.ActivateRequested += async (_, h) => {
            await OpenFileInNewEditorTabAsync(h.FullPath);
            _activeEditorTab?.Control.NavigateTo(h.Line - 1, Math.Max(0, h.Column - 1));
            _activeEditorTab?.Control.HighlightSearch(h.Highlight);
        };
        vm.SearchPanel.ClearHighlightRequested += (_, _) => _activeEditorTab?.Control.HighlightSearch("");
        vm.SearchPanel.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(SearchPanelViewModel.CurrentSearchRootPath)
                && vm.SearchPanel.CurrentSearchRootPath is { } searchRoot)
                vm.Recent.RecordFolder(searchRoot);
        };
        vm.SearchPanel.SupportHighlightChanged += (_, _) => ApplyEditorSupportSearchHighlight();
        vm.SearchPanel.FilesReplacedOnDisk += async (_, paths) =>
            await ReloadEditorTabsAfterReplaceAsync(paths, vm.SearchPanel.HighlightTerm);
        vm.SearchPanel.TerminalSearchProvider = (query, caseSensitive) => {
            if (_activeTerminalTab?.View is not { } view || string.IsNullOrWhiteSpace(query))
                return Array.Empty<TerminalSearchHit>();
            return view.FindMatches(query, caseSensitive)
                .Select(m => new TerminalSearchHit(m.LineIndex, m.Column, m.Length, m.LineText))
                .ToList();
        };
        vm.SearchPanel.TerminalRevealRequested += (_, h) => {
            if (_activeTerminalTab?.View is not { } view)
                return;
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Terminal);
            view.SelectMatch(new TerminalMatch(h.LineIndex, h.Column, h.Length, h.LineText));
            view.FocusTerminal();
        };
        vm.SearchPanel.SymbolSearchProvider = async (query, isClass, ct) => {
            // タブを1枚も開いていなくても効く：セッションが必要ならサーバーを起こす。
            var symbols = await WorkspaceSymbolSearch.SearchAsync(_lspWorkspace, query, isClass, ct);
            return new SymbolSearchResult(true, symbols);
        };
        vm.FolderTree.SetInTerminalRequested += OnSetInTerminalRequested;
        vm.FolderTree.SearchInFolderRequested += (_, path) => {
            vm.SearchPanel.SetSearchRoot(path);
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Search);
            FocusPane(PaneKind.Search);
        };
        vm.FolderTree.CurrentRootChanged += (_, root) => {
            vm.SearchPanel.SetDefaultRoot(root);
            vm.Recent.RecordFolder(root);
        };
        vm.FolderTree.TypoCheckRequested += (_, path) => vm.AiBar.RunTypoCheck(path);
        vm.FolderTree.FileAiRequested += (_, request) => _ = RunFileAiAsync(request);
        vm.FolderTree.WorkflowRequested += (_, req) => RunWorkflowWithInput(req.WorkflowId, req.Input);
        vm.FolderTree.GitBlameRequested += async (_, fullPath) => {
            fullPath = Path.GetFullPath(fullPath);
            await OpenFileInNewEditorTabAsync(fullPath);
            FocusPane(PaneKind.Editor);
            var tab = _editorTabs.FirstOrDefault(t =>
                string.Equals(t.PeekFilePath, fullPath, StringComparison.OrdinalIgnoreCase));
            tab?.Control.ExecuteCommand("Gblame");
        };
        vm.FolderTree.GitHistoryRequested += async (_, fullPath) => await ShowGitHistoryAsync(fullPath);
        vm.FolderTree.CompareRequested += (_, request) => CompareFilesInDiff(request);
        vm.FolderTree.RootStateChanged += (_, _) => SaveActiveWorkspaceSnapshot();
        // ファイル一覧ペイン。素材の行き先（エディタ・ターミナル・Diff・検索・ブラウザ）と
        // タブ追従はツリーと同じ受け口へ流す——どちらから操作しても結果が同じであるべきなので。
        vm.Files.FileActivated += async (_, path) => await OpenFileInNewEditorTabAsync(path);
        vm.Files.FolderNavigated += (_, path) => vm.Recent.RecordFolder(path);
        vm.Files.OpenInBrowserRequested += async (_, path) => await OpenFileInBrowserAsync(path);
        vm.Files.EntryRenamed += (_, e) => OnFolderTreeEntryRenamed(e);
        vm.Files.EntryDeleted += (_, path) => OnFolderTreeEntryDeleted(path);
        vm.Files.SetInTerminalRequested += OnSetInTerminalRequested;
        vm.Files.CompareRequested += (_, request) => CompareFilesInDiff(request);
        vm.Files.SearchInFolderRequested += (_, path) => {
            vm.SearchPanel.SetSearchRoot(path);
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Search);
            FocusPane(PaneKind.Search);
        };
        vm.Files.FileAiRequested += (_, request) => _ = RunFileAiAsync(request);
        vm.Files.StateChanged += (_, _) => SaveActiveWorkspaceSnapshot();
        vm.FolderTree.RevealInFilesPaneRequested += (_, path) => {
            vm.Files.Reveal(path);
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Files);
            FocusPane(PaneKind.Files);
        };
        vm.GitSession.DiffOpenRequested += (_, target) => ShowDiff(target);
        // コミット詳細のファイルをダブルクリック＝そのファイルの差分だけを別ウィンドウで開く
        // （ペインの DIFF は動かさないので、一覧と見比べたまま何枚でも開ける）。行き先は
        // ShowDiffInDetachedWindow と同じ約束＝既に切り離しウィンドウが出ていればそこのタブ。
        vm.GitSession.DiffWindowRequested += (_, request) => ShowDiffInDetachedWindow(
            new DiffOpenTarget.CommitFile(request.Hash, request.Label, request.FullPath, LineInCommit: 0));
        // 差分本体の行から、その実ファイルの同じ行をエディタで開く（行が特定できないときは 0＝開くだけ）。
        vm.DiffSession.EditorLineOpenRequested += async (_, target) => {
            await OpenPathInEditorAsync(Path.GetFullPath(target.Path), target.Line, column: 0);
            FocusPane(PaneKind.Editor);
        };
        vm.DiffSession.CommitOpenInGitRequested += async (_, hash) => {
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Git);
            await vm.GitSession.SelectCommitAsync(hash);
            FocusPane(PaneKind.Git);
        };
        vm.GitPanel.DiffOpenRequested += (_, target) => ShowDiff(target);
        vm.GitSession.OpenHostingUrlRequested += (_, url) => _ = OpenUrlInBrowserAsync(url, null);
        vm.GitSession.RepositoryChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => _ = RefreshOpenEditorTabsFromDiskAsync()));
        GitPane.IsVisibleChanged += (_, e) => {
            if (e.NewValue is true) {
                _vm.GitSession.EnsureLoaded();
                _vm.GitSession.StartLiveTracking();
            } else
                _vm.GitSession.StopLiveTracking();
        };
        DiffPane.IsVisibleChanged += (_, e) => {
            if (e.NewValue is true) {
                _vm.DiffSession.EnsureLoaded();
                _vm.DiffSession.StartLiveTracking();
            } else
                _vm.DiffSession.StopLiveTracking();
        };
        TracePane.IsVisibleChanged += (_, e) => {
            if (e.NewValue is true)
                _vm.TraceSession.EnsureLoaded();
        };
        InitializePegboard();
        InitializeBrowserChrome();
        InitializeTrail();
        InitializeDock();
        StartupProfiler.Mark("ShellWindow ctor 完了");
    }

    private void OnToggleWingCollapsed(object sender, RoutedEventArgs e) {
        _isWingCollapsed = !_isWingCollapsed;
        if (WingHost.Visibility == Visibility.Visible)
            WingColumn.Width = new GridLength(EffectiveWingWidth);
        WingSplitter.IsHitTestVisible = !_isWingCollapsed;
        OverviewButton.Visibility = _stageActive && !_isWingCollapsed ? Visibility.Visible : Visibility.Collapsed;
        UpdateWingToolbar();
        RebuildWings();
        RebuildStageIfResized();
        SaveActiveWorkspaceSnapshot();
    }

    private void UpdateWingToolbar() {
        if (WingToolbarActions is null || WingCollapseIcon is null)
            return;
        WingHost.Margin = _isWingCollapsed
            ? new Thickness(0, 8, 0, 10)
            : new Thickness(0, 8, 10, 10);
        WingToolbar.ColumnDefinitions[0].Width = new GridLength(_isWingCollapsed ? 48 : 32);
        WingCollapseButton.Width = _isWingCollapsed ? 48 : 32;
        WingCollapseButton.Height = _isWingCollapsed ? 48 : 28;
        // 折りたたみ時はスクロールバー自体を見せず、ホイール／トラックパッドでの
        // スクロールは残す。アイコン列の横幅をスクロールバーに奪わせない。
        ScrollViewer.SetVerticalScrollBarVisibility(
            WingScrollViewer,
            _isWingCollapsed ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto);
        WingToolbarActions.Visibility = _isWingCollapsed ? Visibility.Collapsed : Visibility.Visible;
        WingCollapseIcon.Data = TryFindResource(_isWingCollapsed ? "WingIcon.Collapsed" : "WingIcon.Expanded") as Geometry;
        WingCollapseButton.ToolTip = _isWingCollapsed ? "袖を展開" : "袖を折りたたむ";
        WingSplitter.IsHitTestVisible = !_isWingCollapsed;
    }
    private async void OnLoaded(object sender, RoutedEventArgs e) {
        StartupProfiler.Mark("OnLoaded 開始");
        UiJankProfiler.Start(Dispatcher);
        try {
            if (_vm.Workspaces.ActiveWorkspace is { } workspace)
                await SwitchWorkspaceAsync(workspace, captureCurrent: false, deferHydration: true);
            else {
                LoadLayouts(System.Array.Empty<SavedLayout>(), scratch: null, activeIndex: -1, dirty: false);
                ApplyIdePaneApplicability(System.Array.Empty<string>());
                PrepareStageSnapshot(solo: true, StageSnapshot.Default());
                PrepareDockSnapshot(dock: false, snapshot: null);
                ApplyDefaultLayout();
                BrowserAddressSuggestions.SetText(DefaultBrowserUrl);
                CreateBrowserTab(DefaultBrowserUrl);
                CompleteStageSnapshotRestore();
            }
        } catch (Exception ex) {
            BrowserAddressSuggestions.SetText($"WebView2 initialization failed: {ex.Message}");
        }
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => _vm.GitSession.EnsureLoaded()));
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(EnsureDragOverlay));
        StartupProfiler.Mark("OnLoaded 完了");
    }
}
