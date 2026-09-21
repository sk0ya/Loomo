namespace sk0ya.Loomo.App.Services;

/// <summary>分割木に現在の WPF トラック寸法を保存する。</summary>
internal static class PaneLayoutSizeCapture
{
    public static void Capture(PaneNode? root) => CaptureNode(root);

    private static void CaptureNode(PaneNode? node)
    {
        if (node is not PaneSplit split)
            return;
        if (split.Host is { } grid)
        {
            var columns = split.Orientation == SplitKind.Columns;
            foreach (var child in split.Children)
            {
                var index = child.TrackIndex;
                if (index < 0)
                    continue;
                var oldWeight = child.Weight;
                if (columns)
                {
                    if (index < grid.ColumnDefinitions.Count)
                    {
                        var definition = grid.ColumnDefinitions[index];
                        child.Weight = definition.ActualWidth > 0
                            ? definition.ActualWidth
                            : PositiveGridLengthValue(definition.Width, child.Weight);
                    }
                }
                else if (index < grid.RowDefinitions.Count)
                {
                    var definition = grid.RowDefinitions[index];
                    child.Weight = definition.ActualHeight > 0
                        ? definition.ActualHeight
                        : PositiveGridLengthValue(definition.Height, child.Weight);
                }
                if (PaneLayoutDebugLog.Enabled && Math.Abs(oldWeight - child.Weight) > 0.5)
                {
                    var label = child is PaneLeaf leaf ? leaf.Kind.ToString() : "split";
                    PaneLayoutDebugLog.Log($"    CaptureNode: {label}[{index}] weight {oldWeight:0.#} -> {child.Weight:0.#}");
                }
            }
        }
        foreach (var child in split.Children)
            CaptureNode(child);
    }

    private static double PositiveGridLengthValue(GridLength length, double fallback)
        => length.Value > 0 ? length.Value : fallback > 0 ? fallback : 1;
}
