using Ellipse = System.Windows.Shapes.Ellipse;

namespace sk0ya.Loomo.App.Services;

internal sealed record DockBarHosts(Panel RightZone, Panel CenterZone, Panel BottomZone);

/// <summary>ドック帯の3領域、ペインボタン、配置メニュー、活動バッジをまとめて表示する。</summary>
internal sealed class DockBarPresenter(
    DockLayoutCoordinator state,
    DockBarHosts hosts,
    FrameworkElement resources,
    Func<PaneKind, bool> isApplicable,
    Action<PaneKind> togglePane,
    Action<PaneKind, DockRegion> placePane,
    Action<UIElement, PaneKind> hookDrag,
    Brush failedBadgeBrush,
    Brush succeededBadgeBrush)
{
    private readonly Dictionary<PaneKind, Ellipse> _badges = new();

    public IDictionary<PaneKind, Ellipse> Badges => _badges;

    public IReadOnlyList<PaneKind> Rebuild()
    {
        _badges.Clear();
        FillZone(hosts.RightZone, DockRegion.Right);
        FillZone(hosts.CenterZone, DockRegion.Center);
        FillZone(hosts.BottomZone, DockRegion.Bottom);
        return _badges.Keys.ToArray();
    }

    public ContextMenu BuildPlacementMenu(PaneKind kind, bool openLeftOfCursor = false)
        => DockBarViewBuilder.BuildPlacementMenu(
            state.RegionOf(kind), openLeftOfCursor, region => placePane(kind, region));

    public void UpdateBadge(PaneKind kind, PaneActivityKind activity)
    {
        if (!_badges.TryGetValue(kind, out var badge))
            return;
        var tone = PaneActivityPresentation.BadgeTone(activity);
        badge.Visibility = tone == PaneActivityBadgeTone.Hidden ? Visibility.Collapsed : Visibility.Visible;
        if (tone == PaneActivityBadgeTone.Hidden)
            return;
        badge.Fill = tone switch
        {
            PaneActivityBadgeTone.Accent => (Brush)resources.FindResource("Accent"),
            PaneActivityBadgeTone.Failed => failedBadgeBrush,
            PaneActivityBadgeTone.Succeeded => succeededBadgeBrush,
            _ => badge.Fill,
        };
    }

    private void FillZone(Panel zone, DockRegion region)
    {
        zone.Children.Clear();
        foreach (var kind in state.PanesIn(region).Where(isApplicable))
            zone.Children.Add(BuildPaneButton(kind));
    }

    private Button BuildPaneButton(PaneKind kind)
    {
        var region = state.RegionOf(kind);
        var open = state.IsOpen(kind);
        return DockBarViewBuilder.BuildPaneButton(
            kind, region, open, PaneVisibilityPresentation.Label(kind), $"PaneIcon.{kind}",
            DockLayoutPresentation.RegionName(region),
            (Brush)resources.FindResource(open ? "Fg" : "FgDim"), _badges,
            key => resources.TryFindResource(key) as Geometry,
            (Style)resources.FindResource("ActivityIconButtonRight"),
            pane => BuildPlacementMenu(pane, openLeftOfCursor: true), togglePane, hookDrag);
    }
}
