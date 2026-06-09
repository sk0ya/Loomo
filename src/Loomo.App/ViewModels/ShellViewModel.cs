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
    Appearance
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

    /// <summary>サイドバーの表示状態。ActivityBar のクリックで開閉する。</summary>
    [ObservableProperty] private bool _isSidebarVisible = true;

    /// <summary>サイドバーに現在表示しているパネル。</summary>
    [ObservableProperty] private SidebarPanel _activePanel = SidebarPanel.Explorer;

    public ShellViewModel(
        FolderTreeViewModel folderTree,
        WorkspaceListViewModel workspaces,
        AiBarViewModel aiBar,
        TabsViewModel tabs,
        SessionsViewModel sessions,
        SettingsViewModel settings,
        AppearanceViewModel appearance)
    {
        FolderTree = folderTree;
        Workspaces = workspaces;
        AiBar = aiBar;
        Tabs = tabs;
        Sessions = sessions;
        Settings = settings;
        Appearance = appearance;

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

    /// <summary>ActivityBar の設定アイコン。開くときに Ollama のモデル一覧を自動取得する。</summary>
    [RelayCommand]
    private void ShowSettings()
    {
        Activate(SidebarPanel.Settings);
        if (ActivePanel == SidebarPanel.Settings && IsSidebarVisible)
            Settings.EnsureModelsLoaded();
    }

    /// <summary>ActivityBar の外観（テーマ）アイコン。</summary>
    [RelayCommand]
    private void ShowAppearance() => Activate(SidebarPanel.Appearance);

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
