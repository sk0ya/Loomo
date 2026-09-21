using System.Windows;
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
    private readonly DebugProblemsInteractionController _interaction;

    public DebugProblemsView()
    {
        InitializeComponent();
        _interaction = new DebugProblemsInteractionController(Tree, () => DataContext as ProblemsViewModel);
    }

    /// <summary>TreeViewItem のクラスハンドラーに握られる前に、行クリックを処理する。
    /// 行内の Grid に MouseLeftButtonUp を直接置くと、実機入力では TreeViewItem の
    /// 選択処理に負けて問題行のジャンプが発火しないことがある。</summary>
    private void OnTreeMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _interaction.HandleMouseUp(e);

    private void OnTreeKeyDown(object sender, KeyEventArgs e) => _interaction.HandleKeyDown(e);

}
