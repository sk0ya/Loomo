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
    Pegboard
}

/// <summary>中央オーバーレイ設定画面のカテゴリ（左ナビ）。</summary>
public enum SettingsCategory
{
    Appearance,
    Editor,
    Terminal,
    Ai
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
    public GitPanelViewModel GitPanel { get; }
    public GitSessionViewModel GitSession { get; }
    public DiffSessionViewModel DiffSession { get; }
    public TraceSessionViewModel TraceSession { get; }
    public PegboardViewModel Pegboard { get; }

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
        GitPanelViewModel gitPanel,
        GitSessionViewModel gitSession,
        DiffSessionViewModel diffSession,
        TraceSessionViewModel traceSession,
        PegboardViewModel pegboard)
    {
        FolderTree = folderTree;
        Workspaces = workspaces;
        AiBar = aiBar;
        Tabs = tabs;
        Sessions = sessions;
        Settings = settings;
        Appearance = appearance;
        GitPanel = gitPanel;
        GitSession = gitSession;
        DiffSession = diffSession;
        TraceSession = traceSession;
        Pegboard = pegboard;

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

    /// <summary>ActivityBar の設定アイコン。中央オーバーレイの設定画面を AI カテゴリで開く
    /// （同じカテゴリで開いていれば閉じる＝トグル）。開くときにローカルのモデル一覧を取得する。</summary>
    [RelayCommand]
    private void ShowSettings() => OpenSettingsOverlay(SettingsCategory.Ai);

    /// <summary>ActivityBar の外観（テーマ）アイコン。設定オーバーレイを外観カテゴリで開く。</summary>
    [RelayCommand]
    private void ShowAppearance() => OpenSettingsOverlay(SettingsCategory.Appearance);

    /// <summary>ActivityBar のエディタアイコン。設定オーバーレイをエディタカテゴリで開く。</summary>
    [RelayCommand]
    private void ShowEditorSettings() => OpenSettingsOverlay(SettingsCategory.Editor);

    /// <summary>ActivityBar のターミナルアイコン。設定オーバーレイをターミナルカテゴリで開く。</summary>
    [RelayCommand]
    private void ShowTerminalSettings() => OpenSettingsOverlay(SettingsCategory.Terminal);

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

    /// <summary>設定オーバーレイを閉じる（Esc・背景クリック・閉じるボタン）。</summary>
    [RelayCommand]
    private void CloseSettingsOverlay() => IsSettingsOverlayOpen = false;

    /// <summary>ActivityBar のペグボードアイコン（§23.3）。</summary>
    [RelayCommand]
    private void ShowPegboard() => Activate(SidebarPanel.Pegboard);

    /// <summary>ActivityBar の Git アイコン。開くときにリポジトリ状態を遅延読込する。</summary>
    [RelayCommand]
    private void ShowGit()
    {
        Activate(SidebarPanel.Git);
        if (ActivePanel == SidebarPanel.Git && IsSidebarVisible)
            GitPanel.EnsureLoaded();
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
