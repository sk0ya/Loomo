namespace sk0ya.Loomo.App.Services;

internal readonly record struct PaneStagePosition(PaneKind Kind, double X, double Y);

/// <summary>袖候補、矩形位置からの左上ペイン選択、WPFツリー走査エラーの識別。</summary>
internal static class StageCardPolicy
{
    public static IReadOnlyList<PaneKind> WingCandidates(
        IReadOnlyList<PaneKind> order,
        Func<PaneKind, bool> sessionEnabled,
        Func<PaneKind, bool> shownInMain)
        => order.Where(kind => sessionEnabled(kind) && !shownInMain(kind)).ToList();

    public static PaneKind? TopLeftPane(
        IEnumerable<PaneStagePosition> positionedVisiblePanes,
        PaneKind? structuralFallback)
    {
        PaneKind? best = null;
        var bestX = 0d;
        var bestY = 0d;
        foreach (var pane in positionedVisiblePanes)
        {
            if (best is null
                || pane.Y < bestY - 0.5
                || (Math.Abs(pane.Y - bestY) <= 0.5 && pane.X < bestX))
            {
                best = pane.Kind;
                bestX = pane.X;
                bestY = pane.Y;
            }
        }
        return best ?? structuralFallback;
    }

    public static bool IsTreeWalkMutation(string message)
        => message.Contains("論理子を変更できません", StringComparison.Ordinal)
            || (message.Contains("logical child", StringComparison.OrdinalIgnoreCase)
                && message.Contains("tree walk", StringComparison.OrdinalIgnoreCase));
}
