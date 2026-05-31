using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ルートウィンドウの ViewModel。各ペインの VM を束ねる。</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public FolderTreeViewModel FolderTree { get; }
    public AiBarViewModel AiBar { get; }

    /// <summary>サイドバー（FolderTree）の表示状態。ActivityBar のクリックで開閉する。</summary>
    [ObservableProperty] private bool _isSidebarVisible = true;

    public ShellViewModel(FolderTreeViewModel folderTree, AiBarViewModel aiBar)
    {
        FolderTree = folderTree;
        AiBar = aiBar;
    }

    /// <summary>ActivityBar のエクスプローラアイコンから呼ばれ、サイドバーを開閉する。</summary>
    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;
}
