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
    private readonly sk0ya.Loomo.Services.Terminal.PtyHostClient _ptyHost;
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
    public ShellWindow( ShellViewModel vm, TerminalService terminal, EditorService editor, BrowserService browser, IWorkspaceService workspace, TabIconService tabIcons, LoomoSettings settings, TaskbarWorkspaceRecentService taskbarWorkspaceRecent, EditorSupportRegistry editorSupports, EditorSupportResolver editorSupportResolver, CodeEditorSupport codeSupport, IEditorSupportViewFactory editorSupportViewFactory, sk0ya.Loomo.Services.Lsp.LspManagementService lspManagement, sk0ya.Loomo.Services.Lsp.LspWorkspaceService lspWorkspace, ILspServerAdmin lspServerAdmin, Editor.Core.Engine.VimEngineServices editorEngineServices, sk0ya.Loomo.Services.GitService git, KeybindingService keybindings, DiffSessionFactory diffSessions, sk0ya.Loomo.CSharp.Configuration.StyleCopDiagnosticService styleCopDiagnostics, sk0ya.Loomo.CSharp.Configuration.StyleCopCodeFixService styleCopCodeFix, sk0ya.Loomo.CSharp.Configuration.CSharpCompilerDiagnosticService compilerDiagnostics, sk0ya.Loomo.CSharp.Configuration.CSharpEditorConfigService csharpEditorConfig, sk0ya.Loomo.CSharp.Configuration.CSharpDiagnosticExplanationService diagnosticExplanations, IWorkspaceSearchService search, sk0ya.Loomo.Ai.Completion.FimCompletionClient fimCompletion, sk0ya.Loomo.Services.Terminal.PtyHostClient ptyHost, EditorGutterBaseTracker gutterBase, sk0ya.Loomo.CSharp.Projects.ISolutionModelService? solutionModel = null) {
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
        // 保存前処理が入れる変更（整形・C# の整理）は人の編集ではないので、軌跡の「編集」に数えない。
        _editor.BeforeSaveAsync = async (control, targetPath) => {
            _trailEditCommit.BeginSavePreparation(control);
            try { await PrepareEditorSaveAsync(control, targetPath); }
            finally { _trailEditCommit.EndSavePreparation(control); }
        };
        _browser = browser;
        // フロントデバッグ（TS IDE）が dev URL をペインへ出すためのフック：可視化＋フォーカス＋実体化して遷移。
        _browser.ShowAndNavigateRequested = url => ShowBrowserPaneAndNavigateAsync(url);
        _editor.NewVirtualDocumentTabRequested += OpenVirtualDocumentTab;
        _editor.FileSaved += _editorDocumentEvents.RaiseSaved;
        _editor.FileSaved += RecordSavedToLocalHistory;
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
        _workspaceSearch = search;
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
        // ペイン本体は初めて見えたときに作る（ShellWindow.PaneContent.cs）。起動直後に配置へ載らない
        // ペインの View まで、ここで組み立てない。
        DeferPaneContent(GitSessionHost, () => new GitSessionView());
        DeferPaneContent(SearchPaneHost, () => new SearchPanelView());
        DeferPaneContent(FilesPaneHost, () => new FilesPaneView());
        DeferPaneContent(HarnessPaneHost, () => new HarnessPaneView());
        DeferPaneContent(TerminalSupportPaneHost, () => {
            // Claude の会話を読み物として描く（§39.4）。道具は Diff ペインと同じく部屋のプレビューのもの。
            var support = new TerminalSupportPaneView();
            support.ConfigureConversation(editorSupportViewFactory, EditorSupportPreviewFolder,
                () => _settings.Appearance.MarkdownPreviewTheme);
            support.PageLinkClicked += (_, href) => OnTerminalSupportConversationLink(href);
            return support;
        });
        DeferPaneContent(TraceSessionHost, () => new TraceSessionView());
        DeferPaneContent(DebugPaneHost, () => new DebugView());
        DeferPaneContent(TsIdePaneHost, () => new TsDebugView());
        DeferPaneContent(AiSessionsHost, () => new SessionsView());
        DeferPaneContent(AiBarHost, () => new AiBarView());
        DeferPaneContent(DiffSessionHost, () => {
            var diff = new DiffSessionView();
            // Markdown 差分のレンダリング表示（§24.10）。Diff ペインは XAML から生えて DI が届かないので、
            // 部屋の他のプレビューと同じ道具（WebView2 のファクトリと一時ページの置き場）をここで渡す。
            diff.ConfigureMarkdownRender(editorSupportViewFactory, EditorSupportPreviewFolder);
            // 左右並びの本文はエディタ2つ。エディタペインと同じ構文の登録・見た目で作る。
            diff.ConfigureEditors(CreateDiffEditorControl, ApplyDiffEditorAppearance, DiffWorkingDocuments);
            // 本文のリンクは EditorSupport のプレビューと同じ振り分け（URL＝ブラウザ／ファイル＝エディタ）へ流す。
            diff.MarkdownLinkClicked += (_, e) => _ = HandleEditorSupportLinkClickedAsync(e.Href, e.SourcePath);
            return diff;
        });
        _editorSupports = editorSupports;
        _editorSupportResolver = editorSupportResolver;
        _codeSupport = codeSupport;
        _lspManagement = lspManagement;
        _lspWorkspace = lspWorkspace;
        _editorLspNotices = new EditorLspNoticeCoordinator(lspManagement, lspWorkspace, vm.LspPrompt);
        _editorEngineServices = editorEngineServices;
        _lspServerAdmin = lspServerAdmin;
        _fimCompletion = fimCompletion;
        _ptyHost = ptyHost;
        _git = git;
        _gutterBase = gutterBase;
        _gutterBase.Changed += (_, _) => Dispatcher.BeginInvoke(RefreshRealizedEditorGutters);
        _keybindings = keybindings;
        _fileAiSelection = new FileAiSelectionContextBuilder(workspace);
        _keyboard = BuildKeyboardDispatcher();
        _terminalTabs = _scratchTerminalWorkspace.Tabs;
        _editorTabs = _scratchEditorWorkspace.Tabs;
        _browserTabs = _scratchBrowserWorkspace.Tabs;
        InitializePanes();
        HookBranchSwitchers();
        HookExternalFileChanges();
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
        InitializeActivityBar();
        InitializeWorkItems();
        HookAiActivity();
        WireTerminalForegroundBusy();
        WireTerminalStatusRows();
        InitializeRunningPanel();
        InitializeTerminalSupport();
        InitializeDebugMonitor();
        InitializeClaudeHooks();   // パイプ名を先に決める（ターミナルの環境変数に入る）
        InitializeClaudeIde();
        InitializeEditWait();
        vm.Settings.Saved += ApplyVimEnabledToOpenEditorTabs;
        vm.Settings.Saved += ApplyEditorSettingsToOpenEditorTabs;
        vm.Appearance.AppearanceChanged += ApplyAppearanceToOpenTabs;
        vm.Appearance.AppearanceChanged += RebuildWings;
        vm.Tabs.TabActivated += OnSidebarTabActivated;
        vm.Tabs.TabCloseRequested += OnSidebarTabCloseRequested;
        vm.Tabs.TabCloseOthersRequested += OnSidebarTabCloseOthersRequested;
        vm.Tabs.TabCloseAllRequested += OnSidebarTabCloseAllRequested;
        vm.Tabs.TabDetachRequested += OnSidebarTabDetachRequested;
        vm.Tabs.TabNewRequested += OnSidebarTabNewRequested;
        vm.Tabs.TabRenameRequested += OnSidebarTabRenameRequested;
        vm.Tabs.TabNoteRequested += (_, entry) => EditTerminalTabNote(entry.Id);
        vm.Tabs.TabColorChanged += OnSidebarTabColorChanged;
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
        PreviewKeyUp += OnTabSwitcherKeyUp;
        PreviewGotKeyboardFocus += OnWindowPreviewGotKeyboardFocus;
        Deactivated += OnWindowDeactivated;
        InitializeAwaySummary();
        InitializeTabShelf();
        InitializeHttpRequests();
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
            csharpExplorer.NuGetManagerRequested += (_, projectPath) => OpenNuGetPackageManager(projectPath);
        }
        vm.FolderTree.OpenInBrowserRequested += async (_, path) => await OpenFileInBrowserAsync(path);
        vm.FolderTree.EntryRenamed += (_, e) => OnFolderTreeEntryRenamed(e);
        vm.FolderTree.EntryDeleted += (_, path) => OnFolderTreeEntryDeleted(path);
        vm.FolderTree.RevealCurrentFileRequested += (_, _) => RevealActiveFileInFolderTree();
        if (vm.TodoTree is { } todoTree) {
            todoTree.OpenRequested += async (_, hit) => await NavigateTodoAsync(hit, preview: false);
            todoTree.PreviewRequested += async (_, hit) => await NavigateTodoAsync(hit, preview: true);
        }
        vm.SearchPanel.PreviewRequested += async (_, h) => {
            await OpenFileInPreviewTabAsync(h.FullPath);
            RevealSearchHit(h);
        };
        vm.SearchPanel.ActivateRequested += async (_, h) => {
            await OpenFileInNewEditorTabAsync(h.FullPath);
            RevealSearchHit(h);
        };
        vm.SearchPanel.ClearHighlightRequested += (_, _) => _activeEditorTab?.Control.HighlightSearch("");
        WireEditorFooterEncodingEntry();
        vm.SearchPanel.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(SearchPanelViewModel.CurrentSearchRootPath)
                && vm.SearchPanel.CurrentSearchRootPath is { } searchRoot)
                vm.Recent.RecordFolder(searchRoot);
        };
        vm.SearchPanel.SupportHighlightChanged += (_, _) => ApplyEditorSupportSearchHighlight();
        // 残した検索結果のタブ（§23.3.1）は部屋の状態として保存する。
        vm.SearchPanel.TabsChanged += (_, _) => SaveActiveWorkspaceSnapshot();
        // 検索結果の書き戻し（§24.86）は、未保存の編集があるタブのファイルへは書かない。
        vm.SearchPanel.HasUnsavedEdits = path => _editorTabs.Any(t =>
            t.PeekIsModified && FilePathRelations.AreEqual(t.PeekFilePath, path));
        vm.SearchPanel.FilesReplacedOnDisk += async (_, paths) =>
            await ReloadEditorTabsAfterReplaceAsync(paths, vm.SearchPanel.HighlightTerm);
        // この部屋のターミナルのタブを全部探す（§24.67）。前面のタブを先頭に、あとはタブの並び順。
        // まだ一度も表示していないタブはスクロールバックを持っていない（常駐ホストにだけある）ので当たらない。
        vm.SearchPanel.TerminalSearchProvider = (query, caseSensitive) => {
            if (string.IsNullOrWhiteSpace(query))
                return Array.Empty<TerminalSearchHit>();
            var tabs = _terminalTabs.OrderBy(t => t == _activeTerminalTab ? 0 : 1).ToList();
            return tabs.SelectMany(tab => tab.View.FindMatches(query, caseSensitive)
                    .Select(m => new TerminalSearchHit(m.LineIndex, m.Column, m.Length, m.LineText,
                        tab.Id, TerminalTabTitle(tab.Id))))
                .ToList();
        };
        // まだ表示していないタブ（常駐ホストにだけ画面がある）は、ホストに探してもらう（§24.67）。
        vm.SearchPanel.TerminalHostSearchProvider = async query => {
            if (!PersistentTerminalsEnabled)
                return Array.Empty<TerminalSearchHit>();
            var unseen = _terminalTabs.Where(t => !_remoteTerminalSessions.ContainsKey(t.Id)).ToList();
            if (unseen.Count == 0)
                return Array.Empty<TerminalSearchHit>();
            var matches = await _ptyHost.SearchAsync(query, unseen.Select(t => t.Id).ToList());
            return matches.Select(m => new TerminalSearchHit(
                    TerminalSearchHits.UnseenLine, m.Column, query.Length, m.LineText, m.SessionId, TerminalTabTitle(m.SessionId)))
                .ToList();
        };
        vm.SearchPanel.TerminalRevealRequested += async (_, h) => {
            var tab = _terminalTabs.FirstOrDefault(t => t.Id == h.TabId) ?? _activeTerminalTab;
            if (tab is null)
                return;
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Terminal);
            if (tab != _activeTerminalTab)
                ActivateTerminalTab(tab.Id);
            if (h.LineIndex == TerminalSearchHits.UnseenLine) {
                // ホストの画面での行番号は端末とは揃わない。表示して繋ぎ直した画面（再生）が届くのを待って、同じ行の文字で探し直す。
                await RevealUnseenTerminalHitAsync(tab, h);
                return;
            }
            tab.View.SelectMatch(new TerminalMatch(h.LineIndex, h.Column, h.Length, h.LineText));
            tab.View.FocusTerminal();
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
        // ハーネスペイン（§37）：根拠の記録・読まれたファイル・書き出したタスクはエディタで開く（素材の流れ）。
        // 行へ飛ぶのは開いたタブがそのファイルのときだけ（記録が消えていて開けなかったとき、無関係なタブのカーソルを動かさない）。
        vm.Harness.OpenRequested += async (_, request) => await OpenPathInEditorAsync(request.Path, request.Line, 0);
        vm.Harness.TicketOpenRequested += (_, request) => _ = OpenUrlInBrowserAsync(request.Url, request.Title);
        vm.Harness.StateChanged += (_, _) => SaveActiveWorkspaceSnapshot();
        vm.FolderTree.RevealInFilesPaneRequested += (_, path) => {
            vm.Files.Reveal(path);
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Files);
            FocusPane(PaneKind.Files);
        };
        vm.GitSession.DiffOpenRequested += (_, target) => ShowDiff(target);
        WireWorktreeRequests();
        // コミット詳細のファイルをダブルクリック＝そのファイルの差分だけを別ウィンドウで開く
        // （ペインの DIFF は動かさないので、一覧と見比べたまま何枚でも開ける）。行き先は
        // ShowDiffInDetachedWindow と同じ約束＝既に切り離しウィンドウが出ていればそこのタブ。
        vm.GitSession.DiffWindowRequested += (_, request) => ShowDiffInDetachedWindow(
            new DiffOpenTarget.CommitFile(request.Hash, request.Label, request.FullPath, LineInCommit: 0,
                FromHash: request.FromHash));
        // 差分本体の行から、その実ファイルの同じ行をエディタで開く（行が特定できないときは 0＝開くだけ）。
        vm.DiffSession.EditorLineOpenRequested += async (_, target) => {
            await OpenPathInEditorAsync(Path.GetFullPath(target.Path), target.Line, column: 0);
            FocusPane(PaneKind.Editor);
        };
        // コミットへのリンク（§24.74）。部屋（フォルダー）はここで決める。
        vm.GitSession.CommitLinkCopyRequested += (hash, subject) => {
            if (vm.GitSession.RepositoryRoot is not { Length: > 0 } repository)
                return;
            ToastService.Info(LoomoLinkClipboard.CopyCommit(_workspace, repository, hash, subject)
                ? "このコミットへのリンクをコピーしました"
                : "リンクをコピーしました（loomo:// を Windows に登録できなかったので、踏んでも開きません）");
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
    /// <summary>Claude Code 使用量のツールチップを開く直前に、残り時間を今の時刻で書き直す。
    /// クリックで詳細を開いている間は、同じ中身を重ねて出さない。</summary>
    private void OnClaudeUsageToolTipOpening(object sender, System.Windows.Controls.ToolTipEventArgs e) {
        if (ClaudeUsagePopup.IsOpen) { e.Handled = true; return; }
        _vm.ClaudeUsage?.RefreshDetail();
    }

    /// <summary>Claude Code 使用量のクリック：取り直し（Command）に加えて、ホバーを待たずに詳細を開く。
    /// 開いている最中の再クリックは閉じる（外クリックで閉じた直後に開き直さないよう、押下時点の状態で判定）。</summary>
    private void OnClaudeUsageClick(object sender, RoutedEventArgs e) {
        if (_claudeUsagePopupWasOpen) { _claudeUsagePopupWasOpen = false; return; }
        _vm.ClaudeUsage?.RefreshDetail();
        if (ClaudeUsageButton.ToolTip is System.Windows.Controls.ToolTip tip) tip.IsOpen = false;
        ClaudeUsagePopup.IsOpen = true;
    }

    private bool _claudeUsagePopupWasOpen;

    private void OnClaudeUsagePreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        _claudeUsagePopupWasOpen = ClaudeUsagePopup.IsOpen;

    private async void OnLoaded(object sender, RoutedEventArgs e) {
        StartupProfiler.Mark("OnLoaded 開始");
        UiJankProfiler.Start(Dispatcher);
        _vm.ClaudeUsage?.Start();
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
