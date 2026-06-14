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

    // 選択をビュー → VM へ渡す（一括コマンドは VM 保持の選択を対象にする）。
    private void StagedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb) Vm?.SetStagedSelection(lb.SelectedItems);
    }

    private void UnstagedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb) Vm?.SetUnstagedSelection(lb.SelectedItems);
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

    /// <summary>クリックされた要素を含む行（ListBoxItem）の DataContext を取り出す。</summary>
    private static GitChangeItem? FindItem(object source) =>
        FindContainer(source)?.DataContext as GitChangeItem;

    private static ListBoxItem? FindContainer(object source)
    {
        var d = source as DependencyObject;
        while (d is not null and not ListBoxItem)
            d = VisualTreeHelper.GetParent(d);
        return d as ListBoxItem;
    }
}
