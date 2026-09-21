namespace sk0ya.Loomo.App.Services;

internal readonly record struct DockPlacementChoice(DockRegion Region, string Label, bool IsCurrent);

/// <summary>ドックの領域名、配置候補、ドラッグ入力の非Visualな変換。</summary>
internal static class DockLayoutPresentation
{
    public static GridLength Track(DockTrackSize size, double fixedSize) => size switch
    {
        DockTrackSize.Fill => new GridLength(1, GridUnitType.Star),
        DockTrackSize.Fixed => new GridLength(fixedSize),
        _ => new GridLength(0),
    };

    public static IReadOnlyList<DockPlacementChoice> PlacementChoices(DockRegion current)
        =>
        [
            new(DockRegion.Center, "中央（タイル配置）へ", current == DockRegion.Center),
            new(DockRegion.Bottom, "下の領域へ", current == DockRegion.Bottom),
            new(DockRegion.Right, "右の領域へ", current == DockRegion.Right),
        ];

    public static string RegionName(DockRegion region) => region switch
    {
        DockRegion.Right => "右",
        DockRegion.Bottom => "下",
        _ => "中央",
    };

    public static DockRegion? ParseRegion(string? value)
        => Enum.TryParse<DockRegion>(value, out var region) ? region : null;

    public static PaneKind? ParsePaneKind(string? value)
        => Enum.TryParse<PaneKind>(value, out var kind) ? kind : null;

    public static bool CanDrop(
        DockLayoutCoordinator dock,
        PaneKind? kind,
        DockRegion? target)
        => kind is { } pane && target is { } region && dock.RegionOf(pane) != region;
}
