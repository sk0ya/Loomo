using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>AI入力欄のコマンド補完、履歴操作、キャレットとフォーカスを管理する。</summary>
internal sealed class AiBarInputController
{
    private readonly TextBox _inputBox;
    private readonly Func<AiBarViewModel?> _viewModel;

    internal AiBarInputController(TextBox inputBox, Func<AiBarViewModel?> viewModel)
    {
        _inputBox = inputBox;
        _viewModel = viewModel;
    }

    internal void FocusInput()
        => _inputBox.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(_inputBox), _inputBox);
            _inputBox.Focus();
            Keyboard.Focus(_inputBox);
        }));

    internal void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_viewModel() is not { } viewModel)
            return;

        if (e.Key == Key.J && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            var caret = _inputBox.SelectionStart;
            _inputBox.SelectedText = "\n";
            _inputBox.CaretIndex = caret + 1;
            e.Handled = true;
            return;
        }

        if (viewModel.IsCommandPopupOpen)
        {
            switch (e.Key)
            {
                case Key.Down: viewModel.MoveCommandSelection(1); e.Handled = true; break;
                case Key.Up: viewModel.MoveCommandSelection(-1); e.Handled = true; break;
                // 未確定なら Tab / Enter は通常のフォーカス移動・送信へ渡す。
                case Key.Tab: e.Handled = viewModel.CompleteSelectedCommand(); break;
                case Key.Enter: e.Handled = viewModel.AcceptAndRunSelectedCommand(); break;
                case Key.Escape: viewModel.CloseCommandPopup(); e.Handled = true; break;
            }
            return;
        }

        switch (e.Key)
        {
            case Key.Up:
                if (viewModel.RecallPreviousHistory())
                {
                    MoveCaretToEnd();
                    e.Handled = true;
                }
                break;
            case Key.Down:
                if (viewModel.RecallNextHistory())
                {
                    MoveCaretToEnd();
                    e.Handled = true;
                }
                break;
        }
    }

    internal void AcceptSelectedCommand()
        => _viewModel()?.AcceptAndRunSelectedCommand();

    private void MoveCaretToEnd()
        => _inputBox.Dispatcher.BeginInvoke(
            () => _inputBox.CaretIndex = _inputBox.Text.Length,
            DispatcherPriority.Input);
}
