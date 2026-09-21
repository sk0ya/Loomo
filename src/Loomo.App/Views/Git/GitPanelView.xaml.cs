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
}
