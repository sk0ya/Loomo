namespace sk0ya.Loomo.App.Services;

internal readonly record struct PanePlacementStep(
    PaneKind Pane, PaneKind? RelativeTo, bool Center, DropZone? Zone)
{
    public bool MakesVisible => RelativeTo is null;
}

/// <summary>PaneOpenBehavior に応じて、表示またはペイン交換・追加の手順を決める。</summary>
internal static class PanePlacementPolicy
{
    public static IReadOnlyList<PanePlacementStep> Resolve(
        PaneOpenBehavior behavior,
        PaneKind target,
        bool targetVisible,
        PaneKind? topLeft,
        PaneKind? main,
        PaneKind? sub,
        PaneKind? focusedPane,
        SplitKind subAxis)
    {
        if ((behavior is PaneOpenBehavior.Sub or PaneOpenBehavior.Loop) && targetVisible)
            return [];

        if (behavior == PaneOpenBehavior.Sub)
            return IntoSub(target, main, sub, subAxis);

        if (behavior == PaneOpenBehavior.Loop)
        {
            if (focusedPane is { } origin && sub == origin
                && main is { } mainPane && sub is { } subPane && subPane != target)
                return [Place(subPane, mainPane, center: true),
                    Place(target, subPane, center: false, zone: SubZone(subAxis))];

            return IntoSub(target, main, sub, subAxis);
        }

        return topLeft is { } mainPaneAtTopLeft && mainPaneAtTopLeft != target
            ? [Place(target, mainPaneAtTopLeft, center: true)]
            : [Show(target)];
    }

    private static IReadOnlyList<PanePlacementStep> IntoSub(
        PaneKind target, PaneKind? main, PaneKind? sub, SplitKind subAxis)
    {
        if (sub is { } subPane && subPane != target)
            return [Place(target, subPane, center: true)];
        if (main is { } mainPane && mainPane != target)
            return [Place(target, mainPane, center: false, zone: SubZone(subAxis))];
        return [Show(target)];
    }

    private static PanePlacementStep Show(PaneKind pane) => new(pane, null, Center: false, Zone: null);

    private static PanePlacementStep Place(
        PaneKind pane, PaneKind relativeTo, bool center, DropZone? zone = null)
        => new(pane, relativeTo, center, zone);

    private static DropZone SubZone(SplitKind axis)
        => axis == SplitKind.Rows ? DropZone.Below : DropZone.Right;
}
