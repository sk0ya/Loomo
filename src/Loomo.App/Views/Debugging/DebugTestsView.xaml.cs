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

    /// <summary>「期待値と実際を Diff で比べる」（§24.110）。テスト一覧は .NET・TS・Python と別窓にもあるので、
    /// どの一覧からでも部屋の Diff ペインへ着くよう、ホスト（ShellWindow）が 1 か所で受ける。</summary>
    internal static event Action<DiffComparison>? CompareRequested;

    private void OnCompareAssertionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TestItemViewModel { Assertion: { } values } test)
            CompareRequested?.Invoke(TestAssertionComparison.For(test.DisplayName, values));
    }

    // テスト葉のダブルクリック：スタックトレースから拾った位置へジャンプ（葉＝TreeViewItem 上のときだけ。
    // グループ行は無視）。最内の TreeViewItem を拾うので、葉のときだけ DataContext が TestItemViewModel になる。
    private void OnTestDoubleClick(object sender, MouseButtonEventArgs e) => _interaction.NavigateToTestSource(e);

    // テストグループ行のシングルクリック：開閉をトグルする（▶ ボタンのクリックは Button が処理するので来ない）。
    private void OnTestGroupClick(object sender, MouseButtonEventArgs e)
        => DebugTestsInteractionController.ToggleGroup(sender);
}
