using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>テストエクスプローラタブ。DataContext は DebugTestsViewModel。
/// 失敗テストのダブルクリックでソースへジャンプ、グループ行クリックで開閉する。</summary>
public partial class DebugTestsView : UserControl
{
    public DebugTestsView() => InitializeComponent();

    private void OnCopyItemClick(object sender, RoutedEventArgs e) => DebugItemClipboard.Copy(sender);

    // テスト葉のダブルクリック：スタックトレースから拾った位置へジャンプ（葉＝TreeViewItem 上のときだけ。
    // グループ行は無視）。最内の TreeViewItem を拾うので、葉のときだけ DataContext が TestItemViewModel になる。
    private void OnTestDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is TreeViewItem item)
            {
                if (DataContext is DebugTestsViewModel vm && item.DataContext is TestItemViewModel t)
                    vm.NavigateToTestSource(t);
                return;
            }
        }
    }

    // テストグループ行のシングルクリック：開閉をトグルする（▶ ボタンのクリックは Button が処理するので来ない）。
    private void OnTestGroupClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TestGroupViewModel g })
            g.IsExpanded = !g.IsExpanded;
    }
}
