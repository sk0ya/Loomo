namespace sk0ya.Loomo.App.Services;

internal readonly record struct SpanPanePosition(PaneKind Kind, double CenterX, double CenterY, double Height);
internal readonly record struct SpanPaneArea(double Left, double Right);
internal readonly record struct SpanPanePlacement(PaneKind Kind, double Height);
internal readonly record struct SpanPaneColumn(double Width, IReadOnlyList<SpanPanePlacement> Panes);
internal readonly record struct SpanRestorePlacement(int Left, int Top, int Width, int Height);

/// <summary>各モニター領域へペインを割り当て、列幅と行順を算出する。</summary>
internal static class SpanPaneLayoutPolicy
{
    public static SpanRestorePlacement RestorePlacement(
        int currentLeft, int currentTop, int currentWidth, int cursorX,
        int restoreLeft, int restoreTop, int restoreRight, int restoreBottom)
    {
        var width = restoreRight - restoreLeft;
        var height = restoreBottom - restoreTop;
        var ratio = Math.Clamp((cursorX - currentLeft) / (double)Math.Max(currentWidth, 1), 0.0, 1.0);
        return new(cursorX - (int)Math.Round(width * ratio), currentTop, width, height);
    }

    public static PaneNode BuildTree(
        IReadOnlyList<SpanPaneColumn> columns,
        IReadOnlyDictionary<PaneKind, PaneLeaf> leaves)
    {
        var root = new PaneSplit { Orientation = SplitKind.Columns };
        foreach (var column in columns)
            root.Children.Add(BuildColumn(column, leaves));
        return root;
    }

    private static PaneNode BuildColumn(
        SpanPaneColumn column, IReadOnlyDictionary<PaneKind, PaneLeaf> leaves)
    {
        if (column.Panes.Count == 1)
        {
            var leaf = leaves[column.Panes[0].Kind];
            leaf.Weight = column.Width;
            return leaf;
        }
        var rows = new PaneSplit { Orientation = SplitKind.Rows, Weight = column.Width };
        foreach (var item in column.Panes)
        {
            var leaf = leaves[item.Kind];
            leaf.Weight = item.Height;
            rows.Children.Add(leaf);
        }
        return rows;
    }

    public static IReadOnlyList<SpanPaneColumn> Plan(
        IReadOnlyList<SpanPanePosition> visiblePanes,
        IReadOnlyList<PaneKind> hiddenPanes,
        IReadOnlyList<SpanPaneArea> areas,
        double spanLeft,
        double spanWidth,
        double hostLeft,
        double hostRightGap)
    {
        if (areas.Count == 0)
            return [];

        var groups = areas.Select(_ => new List<SpanPanePosition>()).ToList();
        foreach (var pane in visiblePanes)
        {
            var index = -1;
            for (var i = 0; i < areas.Count; i++)
            {
                if (pane.CenterX < (areas[i].Right - spanLeft) / spanWidth)
                {
                    index = i;
                    break;
                }
            }
            groups[index < 0 ? areas.Count - 1 : index].Add(pane);
        }

        if (groups.Any(group => group.Count == 0) && visiblePanes.Count >= areas.Count)
        {
            var ordered = visiblePanes.OrderBy(pane => pane.CenterX).ThenBy(pane => pane.CenterY).ToList();
            groups = areas.Select(_ => new List<SpanPanePosition>()).ToList();
            for (var i = 0; i < ordered.Count; i++)
                groups[i * areas.Count / ordered.Count].Add(ordered[i]);
        }

        foreach (var kind in hiddenPanes)
        {
            var group = groups.FirstOrDefault(candidate => candidate.Count > 0) ?? groups[0];
            var height = group.Count > 0 ? group.Average(pane => pane.Height) : 1.0;
            group.Add(new SpanPanePosition(kind, 0.0, double.MaxValue, height));
        }

        var columns = new List<SpanPaneColumn>();
        double pendingWidth = 0;
        for (var i = 0; i < areas.Count; i++)
        {
            var width = areas[i].Right - areas[i].Left;
            if (i == 0)
                width -= hostLeft;
            if (i == areas.Count - 1)
                width -= hostRightGap;
            width += pendingWidth;
            if (groups[i].Count == 0)
            {
                pendingWidth = width;
                continue;
            }

            pendingWidth = 0;
            var panes = groups[i]
                .OrderBy(pane => pane.CenterY)
                .ThenBy(pane => pane.CenterX)
                .Select(pane => new SpanPanePlacement(pane.Kind, pane.Height))
                .ToArray();
            columns.Add(new SpanPaneColumn(Math.Max(width, 1), panes));
        }

        if (pendingWidth > 0 && columns.Count > 0)
        {
            var last = columns[^1];
            columns[^1] = last with { Width = last.Width + pendingWidth };
        }
        return columns;
    }
}
