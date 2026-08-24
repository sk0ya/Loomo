using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

public partial class ShellWindow
{
    private void OnRecentNavigationRequested(object? sender, RecentUsageItem item)
    {
        if (item.IsDirectory)
        {
            // 「場所」も住所欄もファイル一覧の持ち物なので、頻繁フォルダーもそこへ開く
            // （サイドバーのツリーの根を勝手に打ち替えない）。
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Files);
            _vm.Files.Reveal(item.FullPath);
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
