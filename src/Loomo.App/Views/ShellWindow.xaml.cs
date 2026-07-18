namespace sk0ya.Loomo.App.Views;
public partial class ShellWindow : Window {
    private readonly TerminalService _terminal;
    private readonly EditorService _editor;
    private readonly BrowserService _browser;
    private readonly IWorkspaceService _workspace;
    private readonly IWorkspaceSearchService _search;
    private readonly PaletteSearchCoordinator _paletteSearch;
    private readonly CommandPaletteViewController _paletteView;
    private readonly TabIconService _tabIcons;
    private readonly AiSettings _settings;
    private readonly ShellAppearanceCoordinator _appearance;
    private readonly EditorSupportNavigationService _editorSupportNavigation;
    private readonly EditorSupportRegistry _editorSupports;
    private readonly HexEditorSupport _hexSupport;
    private readonly CodeEditorSupport _codeSupport;
    private readonly sk0ya.Loomo.Services.Lsp.LspManagementService _lspManagement;
    private readonly KeybindingService _keybindings;
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
    private static readonly string EditorSupportPreviewFolder = Path.Combine( Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "WebView2", "preview-page");
    private static readonly string WebViewUserDataFolder = Path.Combine( Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "WebView2");
    private const string WebViewAdditionalBrowserArguments = "--allow-file-access-from-files";
    private static CoreWebView2CreationProperties CreateWebViewCreationProperties()
        => new() {
            UserDataFolder = WebViewUserDataFolder, AdditionalBrowserArguments = WebViewAdditionalBrowserArguments
        };
    private bool _syncingEditorFromSupport;
    private WorkspaceSnapshot? _activeWorkspace;
    private DispatcherOperation? _pendingWorkspaceSnapshotSave;
    private const string DefaultBrowserUrl = "https://www.google.com/";
    private GridLength _savedSidebarWidth = new(220);
    private const double SplitterThickness = 6;
    private PaneKind? _zoomedPane;
    private readonly PaneLayoutCoordinator _paneLayout = new();
    private PaneNode? _root { get => _paneLayout.Root; set => _paneLayout.Root = value; }
    private readonly Dictionary<PaneKind, FrameworkElement> _paneElements = new();
    private FrameworkElement? _dragHandle;
    private Point _paneDragStart;
    private bool _paneDragArmed;
    private Canvas? _dragCanvas;
    private Border? _dragPreview;       // ドロップ先の半分を塗るプレビュー矩形
    private Border? _dragTargetOutline; // ドロップ先ペイン全体の枠
    private Border? _dragGhost;         // 掴んでいるペインをカーソル追従で示すチップ
    private bool _paneDragging;
    private PaneKind _dragSource;
    private PaneKind? _dragTarget;
    private DropZone? _dragZone;
    private bool _dragFromWing;
    private bool _stageDrag;
    private bool _dragCenter;
    private bool _dragSpan;
    private FocusTarget? _focusedRegion;
    private bool _resizeMode;
    private bool _suppressResizeExit;
    private Popup? _resizeHintPopup;
    private PaneSplitView? _editorViews;
    private PaneSplitView? _terminalViews;
    private readonly record struct FocusTarget(PaneKind? Pane, Guid ViewportId = default) {
        public bool IsSidebar => Pane is null;
        public static FocusTarget Sidebar => new((PaneKind?)null);
        public static FocusTarget Of(PaneKind kind) => new(kind);
        public static FocusTarget Viewport(PaneKind kind, Guid viewportId) => new(kind, viewportId);
    }
    public ShellWindow( ShellViewModel vm, TerminalService terminal, EditorService editor, BrowserService browser, IWorkspaceService workspace, IWorkspaceSearchService search, TabIconService tabIcons, AiSettings settings, EditorSupportRegistry editorSupports, HexEditorSupport hexSupport, CodeEditorSupport codeSupport, sk0ya.Loomo.Services.Lsp.LspManagementService lspManagement, sk0ya.Loomo.Services.GitService git, KeybindingService keybindings) {
        StartupProfiler.Mark("ShellWindow ctor 開始");
        InitializeComponent();
        StartupProfiler.Mark("InitializeComponent 完了");
        DataContext = vm;
        _trailBar = new TrailBarController(vm.Trail, TrailScroll, TrailDots, TrailDateTimePopup, TrailCalendar, TrailDateTimePopupRoot, JumpToTrailEntry);
        _vm = vm;
        _terminal = terminal;
        _editor = editor;
        _browser = browser;
        _editor.NewVirtualDocumentTabRequested += OpenVirtualDocumentTab;
        _editor.FileOpenRequested += async path => await OpenFileInNewEditorTabAsync(path);
        _workspace = workspace;
        _search = search;
        _paletteSearch = new PaletteSearchCoordinator(search);
        _tabIcons = tabIcons;
        _settings = settings;
        _appearance = new ShellAppearanceCoordinator(settings, () =>
            (Application.Current?.TryFindResource("Accent") as SolidColorBrush)?.Color
            ?? Color.FromRgb(0x61, 0x48, 0xDE));
        _paletteView = new CommandPaletteViewController(
            PaletteList, PalettePreviewHost, PalettePreviewColumn, PaletteBox, PaletteInput, _appearance,
            new Dictionary<PaletteMode, Button> {
                [PaletteMode.All] = PaletteModeAll, [PaletteMode.File] = PaletteModeFile,
                [PaletteMode.Grep] = PaletteModeGrep, [PaletteMode.Class] = PaletteModeClass,
                [PaletteMode.Symbol] = PaletteModeSymbol, [PaletteMode.Terminal] = PaletteModeTerminal,
                [PaletteMode.Command] = PaletteModeCommand });
        _editorSupportNavigation = new EditorSupportNavigationService(EditorSupportPreviewFolder);
        var editorSupportWebView = new EditorSupportWebViewController( EditorSupportContentHost, _editorSupportNavigation, CreateWebViewCreationProperties, EditorSupport_WebMessageReceived, EditorSupport_ContextMenuRequested);
        _editorSupport = new EditorSupportController(editorSupportWebView);
        editorSupportWebView.NavigationCompleted += (_, _) => {
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
        };
        if (PaneLayoutDebugLog.Enabled) {
            DependencyPropertyDescriptor.FromProperty(ColumnDefinition.WidthProperty, typeof(ColumnDefinition))
                ?.AddValueChanged(SidebarColumn, (_, _) =>
                    PaneLayoutDebugLog.Log($"SidebarColumn.Width -> {SidebarColumn.Width}", withCaller: true));
            DependencyPropertyDescriptor.FromProperty(ColumnDefinition.WidthProperty, typeof(ColumnDefinition))
                ?.AddValueChanged(WingColumn, (_, _) =>
                    PaneLayoutDebugLog.Log($"WingColumn.Width -> {WingColumn.Width}", withCaller: true));
        }
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
        vm.FolderTree.SetInTerminalRequested += OnSetInTerminalRequested;
        vm.FolderTree.SearchInFolderRequested += (_, path) => {
            vm.SearchPanel.SetSearchRoot(path);
            vm.RevealSearchPanel();
        };
        vm.FolderTree.CurrentRootChanged += (_, root) => vm.SearchPanel.SetDefaultRoot(root);
        vm.FolderTree.TypoCheckRequested += (_, path) => vm.AiBar.RunTypoCheck(path);
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
        vm.FolderTree.RootStateChanged += (_, _) => SaveActiveWorkspaceSnapshot();
        vm.GitSession.DiffOpenRequested += (_, _) => {
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Diff);
            FocusPane(PaneKind.Diff);
        };
        vm.DiffSession.CommitOpenInGitRequested += async (_, hash) => {
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Git);
            await vm.GitSession.SelectCommitAsync(hash);
            FocusPane(PaneKind.Git);
        };
        vm.GitPanel.DiffOpenRequested += (_, _) => {
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Diff);
            FocusPane(PaneKind.Diff);
        };
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
        InitializeTrail();
        StartupProfiler.Mark("ShellWindow ctor 完了");
    }
    private async void OnLoaded(object sender, RoutedEventArgs e) {
        StartupProfiler.Mark("OnLoaded 開始");
        UiJankProfiler.Start(Dispatcher);
        try {
            if (_vm.Workspaces.ActiveWorkspace is { } workspace)
                await SwitchWorkspaceAsync(workspace, captureCurrent: false, deferHydration: true);
            else {
                LoadLayouts(System.Array.Empty<SavedLayout>(), scratch: null, activeIndex: -1, dirty: false);
                ApplyIdePaneApplicability(root: null);
                PrepareStageSnapshot(solo: true, StageSnapshot.Default());
                ApplyDefaultLayout();
                BrowserAddressBox.Text = DefaultBrowserUrl;
                CreateBrowserTab(DefaultBrowserUrl);
                CompleteStageSnapshotRestore();
            }
        } catch (Exception ex) {
            BrowserAddressBox.Text = $"WebView2 initialization failed: {ex.Message}";
        }
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => _vm.GitSession.EnsureLoaded()));
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(EnsureDragOverlay));
        StartupProfiler.Mark("OnLoaded 完了");
    }
    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (sender is not ShellViewModel vm) return;
        if (e.PropertyName == nameof(ShellViewModel.IsSidebarVisible)) {
            ApplySidebarVisibility(vm.IsSidebarVisible);
            if (vm.IsSidebarVisible)
                RecordTrailPanel(vm.ActivePanel);
        } else if (e.PropertyName == nameof(ShellViewModel.IsSettingsOverlayOpen) && vm.IsSettingsOverlayOpen)
            EnsureSettingsOverlayCreated();
        else if (e.PropertyName == nameof(ShellViewModel.ActivePanel))
            RecordTrailPanel(vm.ActivePanel);   // サイドバーのパネル切替も軌跡（操作ログ）へ
    }
    private void EnsureSettingsOverlayCreated() {
        if (SettingsOverlayHost.Content is null)
            SettingsOverlayHost.Content = new SettingsOverlayView();
    }
    private void ApplySidebarVisibility(bool visible) {
        if (visible) {
            SidebarColumn.MinWidth = 120;
            SidebarColumn.Width = _savedSidebarWidth.Value > 0 ? _savedSidebarWidth : new GridLength(220);
            SidebarSplitterColumn.Width = new GridLength(SplitterThickness);
            SidebarContainer.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;
        } else {
            _savedSidebarWidth = SidebarColumn.Width;
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            SidebarSplitterColumn.Width = new GridLength(0);
            SidebarContainer.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
        }
    }
}
