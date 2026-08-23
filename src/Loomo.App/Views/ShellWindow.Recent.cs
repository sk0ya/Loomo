using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

public partial class ShellWindow
{
    private void OnRecentNavigationRequested(object? sender, RecentUsageItem item)
    {
        if (item.IsDirectory)
        {
            _vm.Files.Reveal(item.FullPath);
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Files);
            FocusPane(PaneKind.Files);
            return;
        }

        if (!File.Exists(item.FullPath))
            return;
        _ = OpenFileInNewEditorTabAsync(item.FullPath);
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
        FocusPane(PaneKind.Editor);
    }
}
