using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// 見出し行の「▾」：ピン留めしたサブフォルダーへの切替（§40.8.1）。以前は見出し行そのものが ComboBox で、
/// リポジトリの印を置く余地が無かった。候補と選択は右クリックの「ピン留めフォルダーへ切替」と同じ
/// （<see cref="FolderTreeViewModel.RootOptionsFor"/>・<see cref="FolderTreeViewModel.SwitchRootOption(FileNodeViewModel, FolderRootOption)"/>）。
/// </summary>
public partial class FolderTreeView
{
    private void OnRootSwitchButtonClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: FileNodeViewModel header } button
            || DataContext is not FolderTreeViewModel vm)
            return;

        var selected = vm.SelectedRootOptionFor(header);
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var option in vm.RootOptionsFor(header))
        {
            var item = new MenuItem
            {
                Header = option.Label,
                IsCheckable = true,
                IsChecked = ReferenceEquals(option, selected),
                ToolTip = option.FullPath,
            };
            var target = option;
            item.Click += (_, _) => vm.SwitchRootOption(header, target);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
