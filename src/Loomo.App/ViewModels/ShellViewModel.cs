using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>サイドバーに表示するパネル種別。</summary>
public enum SidebarPanel
{
    Explorer,
    Sessions,
    Settings
}

/// <summary>ルートウィンドウの ViewModel。各ペインの VM を束ねる。</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public FolderTreeViewModel FolderTree { get; }
    public AiBarViewModel AiBar { get; }
    public SessionsViewModel Sessions { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>サイドバーの表示状態。ActivityBar のクリックで開閉する。</summary>
    [ObservableProperty] private bool _isSidebarVisible = true;

    /// <summary>サイドバーに現在表示しているパネル。</summary>
    [ObservableProperty] private SidebarPanel _activePanel = SidebarPanel.Explorer;

    public ShellViewModel(
        FolderTreeViewModel folderTree,
        AiBarViewModel aiBar,
        SessionsViewModel sessions,
        SettingsViewModel settings)
    {
        FolderTree = folderTree;
        AiBar = aiBar;
        Sessions = sessions;
        Settings = settings;

        // 設定保存時に AIバーのプロバイダ表示を更新する
        Settings.Saved += AiBar.RefreshProviderLabel;
    }

    /// <summary>ActivityBar のエクスプローラアイコン。</summary>
    [RelayCommand]
    private void ShowExplorer() => Activate(SidebarPanel.Explorer);

    /// <summary>ActivityBar の AIセッションアイコン。</summary>
    [RelayCommand]
    private void ShowSessions() => Activate(SidebarPanel.Sessions);

    /// <summary>ActivityBar の設定アイコン。</summary>
    [RelayCommand]
    private void ShowSettings() => Activate(SidebarPanel.Settings);

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
