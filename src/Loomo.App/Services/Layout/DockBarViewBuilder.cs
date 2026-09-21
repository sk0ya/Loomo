using Ellipse = System.Windows.Shapes.Ellipse;
using Path = System.Windows.Shapes.Path;

namespace sk0ya.Loomo.App.Services;

/// <summary>ドック帯のペインボタンと領域移動メニューを組み立てる。</summary>
internal static class DockBarViewBuilder
{
    public static Button BuildPaneButton(
        PaneKind kind,
        DockRegion region,
        bool isOpen,
        string paneLabel,
        string iconResourceKey,
        string regionLabel,
        Brush iconStroke,
        IDictionary<PaneKind, Ellipse> badges,
        Func<string, Geometry?> findGeometry,
        Style buttonStyle,
        Func<PaneKind, ContextMenu> placementMenu,
        Action<PaneKind> onClick,
        Action<UIElement, PaneKind> hookDrag)
    {
        var icon = new Path
        {
            Data = findGeometry(iconResourceKey),
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.2,
            Stroke = iconStroke,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var badge = new Ellipse
        {
            Width = 6,
            Height = 6,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 11, 10, 0),
        };
        badges[kind] = badge;
        var content = new Grid();
        content.Children.Add(icon);
        content.Children.Add(badge);
        var button = new Button
        {
            Style = buttonStyle,
            Content = content,
            Tag = isOpen,
            ToolTip = $"{paneLabel}（{regionLabel}）：クリックで開閉／ドラッグまたは右クリックで場所を変更",
        };
        button.ContextMenu = placementMenu(kind);
        button.Click += (_, _) => onClick(kind);
        hookDrag(button, kind);
        return button;
    }

    public static ContextMenu BuildPlacementMenu(
        DockRegion current, bool openLeftOfCursor, Action<DockRegion> move)
    {
        var menu = new ContextMenu();
        if (openLeftOfCursor)
            menu.Opened += (_, _) => menu.HorizontalOffset = -(menu.ActualWidth + 2);
        foreach (var choice in DockLayoutPresentation.PlacementChoices(current))
        {
            var item = new MenuItem
            {
                Header = choice.Label,
                IsChecked = choice.IsCurrent,
                IsEnabled = !choice.IsCurrent,
            };
            var target = choice.Region;
            item.Click += (_, _) => move(target);
            menu.Items.Add(item);
        }
        return menu;
    }
}
