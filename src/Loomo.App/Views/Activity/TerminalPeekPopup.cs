using System.Windows.Controls.Primitives;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// 覗き窓（§24.108）の出し入れ。動いているもの面と TABS のターミナル行が同じものを使う。押した行の右に出し、
/// 外を押すか Esc で閉じる。閉じたら、開く前にフォーカスを持っていたもの（読んでいたエディタ等）へ返す。
/// </summary>
internal sealed class TerminalPeekPopup
{
    private readonly Popup _popup;
    private readonly TerminalPeekView _view = new();
    private IInputElement? _focusBefore;
    private Action? _onClosed;

    public TerminalPeekPopup()
    {
        _popup = new Popup
        {
            Child = _view,
            Placement = PlacementMode.Right,
            HorizontalOffset = 6,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
        };
        _popup.Closed += (_, _) => OnClosed();
        _view.CloseRequested += (_, _) => _popup.IsOpen = false;
    }

    /// <summary>開く前に呼ぶ：いまのフォーカスを覚える（窓の入力欄に移る前に）。</summary>
    public void RememberFocus() => _focusBefore = Keyboard.FocusedElement;

    /// <summary><paramref name="anchor"/> の右に、<paramref name="vm"/> の窓を開く。閉じたら <paramref name="onClosed"/>。</summary>
    public void Show(UIElement anchor, TerminalPeekViewModel vm, Action onClosed)
    {
        _onClosed = onClosed;
        _view.DataContext = vm;
        _popup.PlacementTarget = anchor;
        _popup.IsOpen = true;
        _ = vm.StartAsync();
        _view.Dispatcher.BeginInvoke(_view.FocusInput, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Close() => _popup.IsOpen = false;

    private void OnClosed()
    {
        var onClosed = _onClosed;
        _onClosed = null;
        _view.DataContext = null;
        onClosed?.Invoke();
        var restore = _focusBefore;
        _focusBefore = null;
        if (restore is UIElement { IsVisible: true } element)
            element.Focus();
    }
}
