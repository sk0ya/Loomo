using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class GitPanelView
{
    public GitPanelView()
    {
        InitializeComponent();
        new GitPanelInteractionController(
            StagedList, WorkingTree, () => DataContext as GitPanelViewModel);
    }

    /// <summary>「⤒」は左クリックで当て方（クリップボード／ファイル）のメニューを開く（§24.38.1）。</summary>
    private void OnApplyPatchMenuClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { ContextMenu: { } menu } button) return;
        menu.DataContext = DataContext;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
