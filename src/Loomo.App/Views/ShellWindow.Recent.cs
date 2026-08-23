using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

public partial class ShellWindow
{
    private void OnRecentNavigationRequested(object? sender, RecentUsageItem item)
    {
        if (item.IsDirectory)
        {
            // 頻繁フォルダーはサイドバーの FolderTree へ戻す。中央 FilesPane に
            // クイックアクセスを置いても、階層を確認する既存の導線は変えない。
            _vm.FolderTree.NavigateAddress(item.FullPath);
            _vm.IsSidebarVisible = true;
            _vm.ActivePanel = SidebarPanel.Explorer;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(FocusSidebar));
            return;
        }

        if (!File.Exists(item.FullPath))
            return;
        _ = OpenFileInNewEditorTabAsync(item.FullPath);
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
        FocusPane(PaneKind.Editor);
    }
}
