using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>タブを切り替えずに覗く窓の中身（§24.108）。開く・閉じるは置いている側（動いているもの面）が持つ。</summary>
public partial class TerminalPeekView : UserControl
{
    public TerminalPeekView() => InitializeComponent();

    /// <summary>窓を閉じてほしい（Esc）。</summary>
    public event EventHandler? CloseRequested;

    public void FocusInput()
    {
        InputBox.Focus();
        Keyboard.Focus(InputBox);
    }

    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (e.Key == Key.Enter && DataContext is TerminalPeekViewModel vm)
        {
            e.Handled = true;
            vm.SendCommand.Execute(null);
        }
    }

    /// <summary>取り直すたびに末尾を見せる（覗くのは最後の行が要るから）。</summary>
    private void OnScreenTextChanged(object sender, TextChangedEventArgs e) => ScreenBox.ScrollToEnd();
}
