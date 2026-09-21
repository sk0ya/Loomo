using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.Input;
using sk0ya.Loomo.App.Services.Infrastructure;

namespace sk0ya.Loomo.App.Services;

/// <summary>WPF要素からペインを見つけ、保存したフォーカス先を復元する。</summary>
internal static class PaneFocusElementResolver
{
    public static PaneKind? FindPaneOf(
        DependencyObject element, IReadOnlyDictionary<PaneKind, FrameworkElement> paneElements)
    {
        for (var current = element; current is not null; current = WpfTreeTraversal.GetParent(current))
            foreach (var (kind, paneElement) in paneElements)
                if (ReferenceEquals(paneElement, current))
                    return kind;
        return null;
    }

    public static bool FocusFirstFocusable(DependencyObject root)
    {
        if (root is UIElement { Focusable: true, IsVisible: true, IsEnabled: true } element)
        {
            element.Focus();
            return true;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FocusFirstFocusable(VisualTreeHelper.GetChild(root, index)))
                return true;
        return false;
    }

    public static bool TryGetVisibleBounds(
        FrameworkElement element, Visual relativeTo, out PaneLayoutRect bounds)
    {
        bounds = default;
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;

        var topLeft = element.TransformToVisual(relativeTo).Transform(new Point(0, 0));
        bounds = new PaneLayoutRect(topLeft.X, topLeft.Y, element.ActualWidth, element.ActualHeight);
        return true;
    }

    public static PaneFocusCandidate CreateCandidate(PaneFocusTarget target, Rect bounds)
        => new(target, new PaneLayoutRect(bounds.X, bounds.Y, bounds.Width, bounds.Height));

    public static void FocusSidebar(
        Panel sidebarContainer,
        WeakReference<IInputElement>? lastSidebarFocus,
        Action<UIElement> focusFallback,
        Action onVisible)
    {
        var view = sidebarContainer.Children.OfType<UIElement>()
            .FirstOrDefault(child => child.Visibility == Visibility.Visible);
        if (view is null)
            return;

        onVisible();
        if (TryRestoreFocus(lastSidebarFocus, sidebarContainer))
            return;
        focusFallback(view);
    }

    public static bool TryRestoreFocus(WeakReference<IInputElement>? reference, DependencyObject owner)
        => FocusReturnElement.ResolveLive(reference, owner)?.Focus() == true;
}
