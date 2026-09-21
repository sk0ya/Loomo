namespace sk0ya.Loomo.App.Services;

/// <summary>ペイン、ビューポート、またはサイドバーを指すフォーカス位置。</summary>
internal readonly record struct PaneFocusTarget(PaneKind? Pane, Guid ViewportId = default)
{
    public bool IsSidebar => Pane is null;
    public static PaneFocusTarget Sidebar => new((PaneKind?)null);
    public static PaneFocusTarget Of(PaneKind kind) => new(kind);
    public static PaneFocusTarget Viewport(PaneKind kind, Guid viewportId) => new(kind, viewportId);
}

/// <summary>WPF 矩形を使わずに方向移動を評価するための矩形値。</summary>
internal readonly record struct PaneFocusCandidate(PaneFocusTarget Target, PaneLayoutRect Bounds);

/// <summary>方向キーで移る隣接領域を、表示矩形だけから決める。</summary>
internal static class PaneFocusNavigationPolicy
{
    public static bool ShouldRememberPaneFocus(PaneKind pane, bool isButton)
        => !isButton && pane is PaneKind.Debug or PaneKind.TsIde or PaneKind.Ai
            or PaneKind.Git or PaneKind.Diff or PaneKind.Trace;

    public static PaneKind? ResolveRestorePane(
        bool stageActive,
        PaneKind stagePane,
        PaneKind? workspacePane,
        Func<PaneKind, bool> isAvailable)
    {
        var candidate = stageActive ? stagePane : workspacePane;
        return candidate is { } pane && isAvailable(pane) ? pane : null;
    }

    public static PaneFocusTarget? FindNeighbor(
        IReadOnlyList<PaneFocusCandidate> candidates,
        PaneFocusTarget? focused,
        DropZone direction)
    {
        if (candidates.Count == 0)
            return null;

        var originIndex = focused is { } current
            ? IndexOf(candidates, current)
            : -1;
        if (originIndex < 0)
            originIndex = 0;

        var origin = candidates[originIndex];
        var fromCenterX = origin.Bounds.X + origin.Bounds.Width / 2;
        var fromCenterY = origin.Bounds.Y + origin.Bounds.Height / 2;
        PaneFocusTarget? best = null;
        var bestScore = double.MaxValue;

        foreach (var candidate in candidates)
        {
            if (candidate.Target == origin.Target)
                continue;

            var bounds = candidate.Bounds;
            const double tolerance = 1.0;
            var inDirection = direction switch
            {
                DropZone.Left => bounds.X + bounds.Width <= origin.Bounds.X + tolerance,
                DropZone.Right => bounds.X >= origin.Bounds.X + origin.Bounds.Width - tolerance,
                DropZone.Above => bounds.Y + bounds.Height <= origin.Bounds.Y + tolerance,
                _ => bounds.Y >= origin.Bounds.Y + origin.Bounds.Height - tolerance,
            };
            if (!inDirection)
                continue;

            var centerX = bounds.X + bounds.Width / 2;
            var centerY = bounds.Y + bounds.Height / 2;
            var (axis, perpendicular) = direction is DropZone.Left or DropZone.Right
                ? (Math.Abs(centerX - fromCenterX), Math.Abs(centerY - fromCenterY))
                : (Math.Abs(centerY - fromCenterY), Math.Abs(centerX - fromCenterX));
            var score = axis + perpendicular * 2;
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate.Target;
            }
        }

        return best;
    }

    public static int StageCycleDirection(DropZone direction)
        => direction is DropZone.Below or DropZone.Right ? 1 : -1;

    private static int IndexOf(IReadOnlyList<PaneFocusCandidate> candidates, PaneFocusTarget target)
    {
        for (var i = 0; i < candidates.Count; i++)
            if (candidates[i].Target == target)
                return i;
        return -1;
    }
}
