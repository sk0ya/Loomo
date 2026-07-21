using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>診断（Problems）パネル。表示専用（DataContext は ProblemsPanelViewModel）。
/// 行クリックでのジャンプは ViewModel の OpenCommand→OpenRequested イベントを ShellWindow.Problems.cs が購読して行う。</summary>
public partial class ProblemsPanelView : UserControl
{
    public ProblemsPanelView() => InitializeComponent();
}
