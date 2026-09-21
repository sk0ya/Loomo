using System.Windows;
using System.Windows.Input;

namespace sk0ya.Loomo.App.Services;

/// <summary>ウィンドウの戻る／進む入力を、ポインター下のペイン履歴へ振り分ける。</summary>
internal sealed class MouseNavigationController(
    IReadOnlyDictionary<PaneKind, FrameworkElement> paneElements,
    Action<bool> navigateBrowser,
    Action<DependencyObject?, bool> navigateFiles,
    Action<bool> navigateEditorSupport)
{
    public bool Handle(MouseButtonEventArgs e)
    {
        var pane = e.OriginalSource is DependencyObject source
            ? PaneFocusElementResolver.FindPaneOf(source, paneElements)
            : null;
        if (MouseNavigationPolicy.Resolve(e.ChangedButton, pane) is not { } command)
            return false;

        e.Handled = true;
        switch (command.Target)
        {
            case MouseNavigationTarget.Browser:
                navigateBrowser(command.Back);
                break;
            case MouseNavigationTarget.Files:
                navigateFiles(e.OriginalSource as DependencyObject, command.Back);
                break;
            default:
                navigateEditorSupport(command.Back);
                break;
        }
        return true;
    }
}
