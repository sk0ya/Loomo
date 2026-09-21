using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>テストエクスプローラタブ。DataContext は DebugTestsViewModel。
/// 失敗テストのダブルクリックでソースへジャンプ、グループ行クリックで開閉する。</summary>
public partial class DebugTestsView : UserControl
{
    private readonly DebugTestsInteractionController _interaction;

    public DebugTestsView()
    {
        InitializeComponent();
        _interaction = new DebugTestsInteractionController(() => DataContext as ITestExplorer);
    }

    private void OnCopyItemClick(object sender, RoutedEventArgs e) => DebugItemClipboard.Copy(sender);

    // テスト葉のダブルクリック：スタックトレースから拾った位置へジャンプ（葉＝TreeViewItem 上のときだけ。
    // グループ行は無視）。最内の TreeViewItem を拾うので、葉のときだけ DataContext が TestItemViewModel になる。
    private void OnTestDoubleClick(object sender, MouseButtonEventArgs e) => _interaction.NavigateToTestSource(e);

    // テストグループ行のシングルクリック：開閉をトグルする（▶ ボタンのクリックは Button が処理するので来ない）。
    private void OnTestGroupClick(object sender, MouseButtonEventArgs e)
        => DebugTestsInteractionController.ToggleGroup(sender);
}
