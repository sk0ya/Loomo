using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class FolderTreeView : UserControl
{
    public FolderTreeView() => InitializeComponent();

    private void OnTreeMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeView tree || e.OriginalSource is not DependencyObject source)
            return;

        var item = ItemsControl.ContainerFromElement(tree, source) as TreeViewItem;
        if (item?.DataContext is not FileNodeViewModel node || node.IsDirectory)
            return;

        if (DataContext is FolderTreeViewModel vm)
        {
            vm.NotifyActivated(node.FullPath);
            e.Handled = true;
        }
    }
}
