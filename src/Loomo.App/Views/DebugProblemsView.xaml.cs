using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>IDE ペインの「問題」タブ。表示専用（DataContext は ProblemsViewModel）。
/// 行クリックでのジャンプは ViewModel の OpenCommand→OpenRequested イベントを ShellWindow.Problems.cs が購読して行う。</summary>
public partial class DebugProblemsView : UserControl
{
    public DebugProblemsView() => InitializeComponent();
}
