using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>IDE ペインの「問題」タブ。表示専用（DataContext は ProblemsViewModel）。
/// 診断行のクリック／Enter でのジャンプは ViewModel の OpenCommand→OpenRequested イベントを
/// ShellWindow.Problems.cs が購読して行う。矢印キーの選択移動だけでは飛ばない
/// （ジャンプのたびにエディタへフォーカスが移ると一覧を辿れなくなるため）。</summary>
public partial class DebugProblemsView : UserControl
{
    public DebugProblemsView() => InitializeComponent();

    private ProblemsViewModel? Vm => DataContext as ProblemsViewModel;

    /// <summary>TreeViewItem のクラスハンドラーに握られる前に、行クリックを処理する。
    /// 行内の Grid に MouseLeftButtonUp を直接置くと、実機入力では TreeViewItem の
    /// 選択処理に負けて問題行のジャンプが発火しないことがある。</summary>
    private void OnTreeMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || e.OriginalSource is not DependencyObject source)
            return;

        // 開閉矢印は ToggleButton 自身が IsExpanded を更新する。
        if (FindAncestor<ToggleButton>(source) is not null)
            return;

        // Quick Fix はそのボタンの Command だけを実行し、行ジャンプを重ねない。
        if (FindAncestor<ButtonBase>(source) is not null)
            return;

        if (FindAncestor<TreeViewItem>(source) is not { } treeItem)
            return;

        switch (treeItem.DataContext)
        {
            case ProblemFileGroup group:
                group.IsExpanded = !group.IsExpanded;
                break;
            case ProblemItemViewModel item:
                Vm?.OpenCommand.Execute(item);
                break;
        }
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        switch (Tree.SelectedItem)
        {
            case ProblemItemViewModel item:
                Vm?.OpenCommand.Execute(item);
                e.Handled = true;
                break;
            case ProblemFileGroup g:
                g.IsExpanded = !g.IsExpanded;
                e.Handled = true;
                break;
        }
    }

    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = current switch
        {
            Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(current),
            _ => LogicalTreeHelper.GetParent(current),
        })
        {
            if (current is T match)
                return match;
        }

        return null;
    }
}
