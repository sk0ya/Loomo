using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>サイドバーに表示するパネル種別。</summary>
public enum SidebarPanel
{
    Explorer,
    Tabs,
    Sessions,
    Settings,
    Appearance,
    Git,
    Pegboard,
    Search
}

/// <summary>中央オーバーレイ設定画面のカテゴリ（左ナビ）。</summary>
public enum SettingsCategory
{
    Appearance,
    Editor,
    Terminal,
    Ai,
    Lsp,
    Formatter,
    Keyboard
}

/// <summary>ルートウィンドウの ViewModel。各ペインの VM を束ねる。</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public FolderTreeViewModel FolderTree { get; }
    public WorkspaceListViewModel Workspaces { get; }
    public AiBarViewModel AiBar { get; }
    public TabsViewModel Tabs { get; }
    public SessionsViewModel Sessions { get; }
    public SettingsViewModel Settings { get; }
    public AppearanceViewModel Appearance { get; }
    public LspSettingsViewModel Lsp { get; }
    public LspPromptViewModel LspPrompt { get; }
    public FormatterSettingsViewModel Formatter { get; }
    public KeybindingsViewModel Keyboard { get; }
    public GitPanelViewModel GitPanel { get; }
    public GitSessionViewModel GitSession { get; }
    public DiffSessionViewModel DiffSession { get; }
    public TraceSessionViewModel TraceSession { get; }
    public PegboardViewModel Pegboard { get; }
    public SearchPanelViewModel SearchPanel { get; }
    public DebugViewModel Debug { get; }
    /// <summary>ウィンドウ最下部の軌跡（操作ログ）バー。クリックで通過した地点へ戻る。</summary>
    public TrailViewModel Trail { get; }

    /// <summary>サイドバーの表示状態。ActivityBar のクリックで開閉する。</summary>
    [ObservableProperty] private bool _isSidebarVisible = true;

    /// <summary>サイドバーに現在表示しているパネル。</summary>
    [ObservableProperty] private SidebarPanel _activePanel = SidebarPanel.Explorer;

    /// <summary>中央オーバーレイの設定画面を開いているか。</summary>
    [ObservableProperty] private bool _isSettingsOverlayOpen;

    /// <summary>設定オーバーレイで選択中のカテゴリ（左ナビ）。</summary>
    [ObservableProperty] private SettingsCategory _settingsCategory = SettingsCategory.Ai;

    public ShellViewModel(
        FolderTreeViewModel folderTree,
        WorkspaceListViewModel workspaces,
        AiBarViewModel aiBar,
        TabsViewModel tabs,
        SessionsViewModel sessions,
        SettingsViewModel settings,
        AppearanceViewModel appearance,
        LspSettingsViewModel lsp,
        LspPromptViewModel lspPrompt,
        FormatterSettingsViewModel formatter,
        KeybindingsViewModel keyboard,
        GitPanelViewModel gitPanel,
        GitSessionViewModel gitSession,
        DiffSessionViewModel diffSession,
        TraceSessionViewModel traceSession,
        PegboardViewModel pegboard,
        SearchPanelViewModel searchPanel,
        DebugViewModel debug,
        TrailViewModel trail)
    {
        FolderTree = folderTree;
        Workspaces = workspaces;
        AiBar = aiBar;
        Tabs = tabs;
        Sessions = sessions;
        Settings = settings;
        Appearance = appearance;
        Lsp = lsp;
        LspPrompt = lspPrompt;
        // 促しバーの「設定を開く」→ LSP 設定オーバーレイを開く。
        LspPrompt.OpenSettingsRequested += () => OpenSettingsOverlay(SettingsCategory.Lsp);
        Formatter = formatter;
        Keyboard = keyboard;
        GitPanel = gitPanel;
        GitSession = gitSession;
        DiffSession = diffSession;
        TraceSession = traceSession;
        Pegboard = pegboard;
        SearchPanel = searchPanel;
        Debug = debug;
        Trail = trail;

        // 設定保存時に AIバーのプロバイダ表示を更新する。
        Settings.Saved += AiBar.RefreshProviderLabel;
    }

    /// <summary>ActivityBar のエクスプローラアイコン。</summary>
    [RelayCommand]
    private void ShowExplorer() => Activate(SidebarPanel.Explorer);

    /// <summary>ActivityBar のタブ一覧アイコン。</summary>
    [RelayCommand]
    private void ShowTabs() => Activate(SidebarPanel.Tabs);

    /// <summary>ActivityBar の AIセッションアイコン。開くときに保存済みセッション一覧を遅延読込する。</summary>
    [RelayCommand]
    private void ShowSessions()
    {
        Activate(SidebarPanel.Sessions);
        if (ActivePanel == SidebarPanel.Sessions && IsSidebarVisible)
            Sessions.EnsureLoaded();
    }

    /// <summary>ActivityBar の設定（歯車）アイコン。中央オーバーレイの設定画面を外観カテゴリで開く
    /// （同じカテゴリで開いていれば閉じる＝トグル）。開くときにローカルのモデル一覧を取得する。</summary>
    [RelayCommand]
    private void ShowSettings() => OpenSettingsOverlay(SettingsCategory.Appearance);

    /// <summary>ActivityBar の外観（テーマ）アイコン。設定オーバーレイを外観カテゴリで開く。</summary>
    [RelayCommand]
    private void ShowAppearance() => OpenSettingsOverlay(SettingsCategory.Appearance);

    /// <summary>ActivityBar のエディタアイコン。設定オーバーレイをエディタカテゴリで開く。</summary>
    [RelayCommand]
    private void ShowEditorSettings() => OpenSettingsOverlay(SettingsCategory.Editor);

    /// <summary>ActivityBar のターミナルアイコン。設定オーバーレイをターミナルカテゴリで開く。</summary>
    [RelayCommand]
    private void ShowTerminalSettings() => OpenSettingsOverlay(SettingsCategory.Terminal);

    /// <summary>設定オーバーレイをキーボードカテゴリで開く。</summary>
    [RelayCommand]
    private void ShowKeyboardSettings() => OpenSettingsOverlay(SettingsCategory.Keyboard);

    /// <summary>設定オーバーレイを指定カテゴリで開く。既に同じカテゴリで開いていればトグルで閉じる。</summary>
    private void OpenSettingsOverlay(SettingsCategory category)
    {
        if (IsSettingsOverlayOpen && SettingsCategory == category)
        {
            IsSettingsOverlayOpen = false;
            return;
        }
        SettingsCategory = category;
        IsSettingsOverlayOpen = true;
        Settings.EnsureModelsLoaded();
    }

    /// <summary>
    /// 設定カテゴリが切り替わったときの追従処理。「言語サーバー」へはナビ項目の双方向バインドで
    /// 直接遷移する経路もある（<see cref="OpenSettingsOverlay"/> を通らない）ため、カテゴリ変化を
    /// 一元的に捕まえて一覧を取り直す。これをしないと一覧（導入状況）が空のまま「追加」フォームだけが見える。
    /// </summary>
    partial void OnSettingsCategoryChanged(SettingsCategory value)
    {
        if (value == SettingsCategory.Lsp)
            Lsp.Refresh();
        else if (value == SettingsCategory.Formatter)
            Formatter.Refresh();
    }

    /// <summary>設定オーバーレイを閉じる（Esc・背景クリック・閉じるボタン）。</summary>
    [RelayCommand]
    private void CloseSettingsOverlay() => IsSettingsOverlayOpen = false;

    /// <summary>ActivityBar のペグボードアイコン（§23.3）。</summary>
    [RelayCommand]
    private void ShowPegboard() => Activate(SidebarPanel.Pegboard);

    /// <summary>ActivityBar の検索アイコン。grep（全文検索）パネルを開く。</summary>
    [RelayCommand]
    private void ShowSearch() => Activate(SidebarPanel.Search);

    /// <summary>検索パネルを開く（トグルせず必ず開く）。フォルダーツリーの「このフォルダーで検索」用。</summary>
    public void RevealSearchPanel()
    {
        ActivePanel = SidebarPanel.Search;
        IsSidebarVisible = true;
    }

    /// <summary>ActivityBar の Git アイコン。表示中は作業ツリーをライブ監視して自動更新する
    /// （実際の開始・停止は <see cref="UpdateGitPanelLive"/> が可視状態の変化に応じて行う）。</summary>
    [RelayCommand]
    private void ShowGit() => Activate(SidebarPanel.Git);

    // サイドバーの表示状態・選択パネルが変わるたびに、Git パネルのライブ監視を入切する。
    partial void OnActivePanelChanged(SidebarPanel value) => UpdateGitPanelLive();
    partial void OnIsSidebarVisibleChanged(bool value) => UpdateGitPanelLive();

    /// <summary>Git パネルが「見えている」ときだけライブ監視する。開いた瞬間に最新化される。</summary>
    private void UpdateGitPanelLive()
    {
        if (IsSidebarVisible && ActivePanel == SidebarPanel.Git)
            GitPanel.StartLiveTracking();
        else
            GitPanel.StopLiveTracking();
    }

    /// <summary>同じパネルを再クリックしたら閉じ、別パネルなら切替えて開く（VS Code 風）。</summary>
    private void Activate(SidebarPanel panel)
    {
        if (IsSidebarVisible && ActivePanel == panel)
        {
            IsSidebarVisible = false;
        }
        else
        {
            ActivePanel = panel;
            IsSidebarVisible = true;
        }
    }
}
