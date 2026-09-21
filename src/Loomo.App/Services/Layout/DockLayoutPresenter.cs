using System.Windows.Shapes;

namespace sk0ya.Loomo.App.Services;

internal sealed record DockLayoutHosts(
    Grid CenterHost,
    Grid BottomHost,
    Grid RightHost,
    FrameworkElement DockBar,
    Panel BarRightZone,
    Panel BarCenterZone,
    Panel BarBottomZone,
    RowDefinition CenterRow,
    RowDefinition BottomRow,
    RowDefinition BottomSplitterRow,
    ColumnDefinition CenterColumn,
    ColumnDefinition RightColumn,
    ColumnDefinition RightSplitterColumn,
    ColumnDefinition BarColumn,
    UIElement BottomSplitter,
    UIElement RightSplitter);

/// <summary>ドックの割当状態をWPFのホスト・トラック表示へ反映する。</summary>
internal sealed class DockLayoutPresenter(
    DockLayoutCoordinator state,
    IReadOnlyDictionary<PaneKind, FrameworkElement> paneElements,
    DockLayoutHosts hosts,
    IDictionary<PaneKind, Ellipse> badges,
    double splitterThickness,
    double centerMinWidth,
    double centerMinHeight)
{
    public void ClearHosts()
    {
        hosts.CenterHost.Visibility = Visibility.Visible;
        hosts.CenterRow.Height = new GridLength(1, GridUnitType.Star);
        hosts.CenterRow.MinHeight = centerMinHeight;
        hosts.CenterColumn.Width = new GridLength(1, GridUnitType.Star);
        hosts.CenterColumn.MinWidth = centerMinWidth;
        hosts.BottomHost.Children.Clear();
        hosts.RightHost.Children.Clear();
        hosts.BarRightZone.Children.Clear();
        hosts.BarCenterZone.Children.Clear();
        hosts.BarBottomZone.Children.Clear();
        badges.Clear();
        hosts.BottomHost.Visibility = Visibility.Collapsed;
        hosts.RightHost.Visibility = Visibility.Collapsed;
        hosts.BottomSplitter.Visibility = Visibility.Collapsed;
        hosts.RightSplitter.Visibility = Visibility.Collapsed;
        hosts.DockBar.Visibility = Visibility.Collapsed;
        hosts.BottomRow.Height = new GridLength(0);
        hosts.BottomSplitterRow.Height = new GridLength(0);
        hosts.RightColumn.Width = new GridLength(0);
        hosts.RightSplitterColumn.Width = new GridLength(0);
        hosts.BarColumn.Width = new GridLength(0);
    }

    public void ApplyCenter(bool fullscreen, PaneKind? zoomedPane)
    {
        var kind = fullscreen ? zoomedPane ?? state.CenterPane : state.CenterPane;
        var element = kind is { } center && paneElements.TryGetValue(center, out var pane) ? pane : null;
        hosts.CenterHost.Visibility = element is null ? Visibility.Collapsed : Visibility.Visible;
        if (element is not null && hosts.CenterHost.Children.Count == 1
            && ReferenceEquals(hosts.CenterHost.Children[0], element))
        {
            element.Visibility = Visibility.Visible;
            return;
        }
        hosts.CenterHost.Children.Clear();
        hosts.CenterHost.RowDefinitions.Clear();
        hosts.CenterHost.ColumnDefinitions.Clear();
        if (element is null)
            return;
        if (element.Parent is Panel parent)
            parent.Children.Remove(element);
        element.Visibility = Visibility.Visible;
        hosts.CenterHost.Children.Add(element);
    }

    public void ApplyRegion(DockRegion region, bool fullscreen)
    {
        var host = region switch
        {
            DockRegion.Bottom => hosts.BottomHost,
            DockRegion.Right => hosts.RightHost,
            _ => throw new ArgumentOutOfRangeException(nameof(region)),
        };
        var kind = fullscreen ? null : state.OpenPaneIn(region);
        if (kind is null || !paneElements.TryGetValue(kind.Value, out var element))
        {
            host.Children.Clear();
            host.Visibility = Visibility.Collapsed;
            return;
        }
        if (!ReferenceEquals(element.Parent, host))
        {
            if (element.Parent is Panel parent)
                parent.Children.Remove(element);
            host.Children.Clear();
            element.Visibility = Visibility.Visible;
            host.Children.Add(element);
        }
        host.Visibility = Visibility.Visible;
    }

    public void ApplyTracks()
    {
        var plan = DockTrackPlan.For(
            center: hosts.CenterHost.Visibility == Visibility.Visible,
            bottom: hosts.BottomHost.Visibility == Visibility.Visible,
            right: hosts.RightHost.Visibility == Visibility.Visible);
        hosts.CenterRow.Height = DockLayoutPresentation.Track(plan.CenterRow, 0);
        hosts.CenterRow.MinHeight = plan.CenterRow == DockTrackSize.Collapsed ? 0 : centerMinHeight;
        hosts.CenterColumn.Width = DockLayoutPresentation.Track(plan.CenterColumn, 0);
        hosts.CenterColumn.MinWidth = plan.CenterColumn == DockTrackSize.Collapsed ? 0 : centerMinWidth;
        hosts.BottomRow.Height = DockLayoutPresentation.Track(plan.BottomRow, state.BottomHeight);
        hosts.RightColumn.Width = DockLayoutPresentation.Track(plan.RightColumn, state.RightWidth);
        hosts.BottomSplitterRow.Height = new GridLength(plan.BottomSplitter ? splitterThickness : 0);
        hosts.RightSplitterColumn.Width = new GridLength(plan.RightSplitter ? splitterThickness : 0);
        hosts.BottomSplitter.Visibility = plan.BottomSplitter ? Visibility.Visible : Visibility.Collapsed;
        hosts.RightSplitter.Visibility = plan.RightSplitter ? Visibility.Visible : Visibility.Collapsed;
    }
}
