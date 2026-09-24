namespace sk0ya.Loomo.App.Services;

/// <summary>デバッグのプロジェクト／スクリプト領域を折り畳むときの幅状態。</summary>
internal readonly record struct DebugPaneLayoutState(bool IsExpanded, double ExpandedWidth)
{
    internal double ColumnWidth => IsExpanded ? Math.Max(170, ExpandedWidth) : 28;
    internal double SplitterWidth => IsExpanded ? 6 : 0;
}

/// <summary>同じ形のデバッグ一覧ペインで使う幅の保存・復元規則。</summary>
internal static class DebugPaneLayoutPolicy
{
    internal static DebugPaneLayoutState Initial => new(true, 220);

    internal static DebugPaneLayoutState Toggle(DebugPaneLayoutState current, double actualWidth)
        => current.IsExpanded
            ? new DebugPaneLayoutState(false, actualWidth > 40 ? actualWidth : current.ExpandedWidth)
            : current with { IsExpanded = true };
}
