using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>ファイル見出しは行のどこをクリックしても開閉。</summary>
    private void OnGroupRowClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as System.Windows.FrameworkElement)?.DataContext is ProblemFileGroup g)
            g.IsExpanded = !g.IsExpanded;
    }

    private void OnProblemRowClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as System.Windows.FrameworkElement)?.DataContext is ProblemItemViewModel item)
            Vm?.OpenCommand.Execute(item);
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
}
