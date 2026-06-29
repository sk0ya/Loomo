using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class GitPanelView
{
    public GitPanelView()
    {
        InitializeComponent();
    }

    private GitPanelViewModel? Vm => DataContext as GitPanelViewModel;

    // ステージ済みリストの選択をビュー → VM へ渡す（一括アンステージは VM 保持の選択を対象にする）。
    private void StagedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb) Vm?.SetStagedSelection(lb.SelectedItems);
    }

    // ダブルクリックでその行のファイルを開く（単クリックは選択のみ）。
    private void ChangeList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindItem(e.OriginalSource) is GitChangeItem item)
            Vm?.OpenFileCommand.Execute(item);
    }

    // 右クリックした行が未選択なら、その行だけを選択してからメニューを出す（VS Code と同じ挙動）。
    private void ChangeList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindContainer(e.OriginalSource) is { } container && !container.IsSelected)
        {
            if (sender is ListBox lb) lb.SelectedItems.Clear();
            container.IsSelected = true;
        }
    }

    // 行のどこをクリックしても、ディレクトリ／セクションなら開閉をトグルする（矢印だけに頼らない）。
    // チェックボックスのクリックは選択操作に任せ、トグルしない。
    private void ChangeTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d is not null and not TreeViewItem)
        {
            if (d is CheckBox) return;
            d = VisualTreeHelper.GetParent(d);
        }
        if (e.ClickCount == 1 && d is TreeViewItem { HasItems: true } item)
            item.IsExpanded = !item.IsExpanded;
    }

    // TreeView は右クリックだけでは SelectedItem が切り替わらないため、コンテキストメニューが
    // 以前の選択ファイルへ作用しないよう、クリックされたノードを先に選択する。
    private void ChangeTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d is not null and not TreeViewItem)
            d = VisualTreeHelper.GetParent(d);
        if (d is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    /// <summary>クリックされた一覧／ツリー行から変更ファイルを取り出す。</summary>
    private static GitChangeItem? FindItem(object source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is FrameworkElement { DataContext: GitChangeItem item }) return item;
            if (d is FrameworkElement { DataContext: GitChangeTreeNode { Change: { } change } }) return change;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private static ListBoxItem? FindContainer(object source)
    {
        var d = source as DependencyObject;
        while (d is not null and not ListBoxItem)
            d = VisualTreeHelper.GetParent(d);
        return d as ListBoxItem;
    }
}
