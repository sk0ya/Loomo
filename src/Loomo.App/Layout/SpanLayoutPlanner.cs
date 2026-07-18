namespace sk0ya.Loomo.App.Layout;

/// <summary>物理ピクセル単位のモニタ作業領域。</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom);

/// <summary>マルチモニタ跨ぎ配置のUI非依存な矩形計算。</summary>
public static class SpanLayoutPlanner
{
    public static IReadOnlyList<ScreenRect> SideBySide(
        ScreenRect current, IEnumerable<ScreenRect> workAreas)
    {
        var result = workAreas
            .Where(area => area.Bottom > current.Top && area.Top < current.Bottom)
            .OrderBy(area => area.Left)
            .ToList();
        return result.Count > 0 ? result : new[] { current };
    }

    public static ScreenRect MaximizeRect(ScreenRect current, IEnumerable<ScreenRect> sideBySide)
    {
        var left = current.Left;
        var right = current.Right;
        var top = current.Top;
        var bottom = current.Bottom;
        foreach (var area in sideBySide)
        {
            left = Math.Min(left, area.Left);
            right = Math.Max(right, area.Right);
            top = Math.Max(top, area.Top);
            bottom = Math.Min(bottom, area.Bottom);
        }
        return bottom <= top ? current : new ScreenRect(left, top, right, bottom);
    }
}
