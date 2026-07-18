using sk0ya.Loomo.App.Layout;

namespace sk0ya.Loomo.Tests;

public class SpanLayoutPlannerTests
{
    [Fact]
    public void SideBySide_excludes_vertically_disjoint_monitors_and_sorts_left_to_right()
    {
        var current = new ScreenRect(0, 0, 1920, 1040);
        var result = SpanLayoutPlanner.SideBySide(current, new[]
        {
            new ScreenRect(1920, 0, 3840, 1040),
            new ScreenRect(0, 1040, 1920, 2120),
            current,
            new ScreenRect(-1280, 100, 0, 1024)
        });

        Assert.Equal(new[] { -1280, 0, 1920 }, result.Select(area => area.Left));
    }

    [Fact]
    public void MaximizeRect_uses_union_width_and_common_vertical_band()
    {
        var current = new ScreenRect(0, 0, 1920, 1040);
        var result = SpanLayoutPlanner.MaximizeRect(current, new[]
        {
            new ScreenRect(-1280, 40, 0, 1024),
            current,
            new ScreenRect(1920, 20, 3840, 1080)
        });

        Assert.Equal(new ScreenRect(-1280, 40, 3840, 1024), result);
    }

    [Fact]
    public void Empty_side_by_side_set_keeps_current_monitor()
    {
        var current = new ScreenRect(0, 0, 1920, 1040);
        Assert.Equal(new[] { current }, SpanLayoutPlanner.SideBySide(current, Array.Empty<ScreenRect>()));
        Assert.Equal(current, SpanLayoutPlanner.MaximizeRect(current, Array.Empty<ScreenRect>()));
    }
}
