using System.IO;
using sk0ya.Loomo.App.Layout;

namespace sk0ya.Loomo.Tests;

public class ShellWindowPlacementTests
{
    private static readonly ScreenRect[] TwoMonitors =
    {
        new(0, 0, 1920, 1040),
        new(1920, 0, 3840, 1040),
    };

    [Fact]
    public void Window_on_a_present_monitor_is_reachable()
        => Assert.True(ShellWindowPlacementPolicy.IsReachable(new ScreenRect(2000, 100, 3200, 900), TwoMonitors));

    [Fact]
    public void Window_left_on_a_removed_monitor_is_not_reachable()
        => Assert.False(ShellWindowPlacementPolicy.IsReachable(new ScreenRect(4000, 100, 5200, 900), TwoMonitors));

    [Fact]
    public void Window_whose_title_band_is_above_every_work_area_is_not_reachable()
        => Assert.False(ShellWindowPlacementPolicy.IsReachable(new ScreenRect(100, -900, 1300, 10), TwoMonitors));

    [Fact]
    public void Window_hanging_off_the_edge_is_reachable_while_enough_title_band_shows()
    {
        Assert.True(ShellWindowPlacementPolicy.IsReachable(new ScreenRect(-1000, 100, 200, 900), TwoMonitors));
        Assert.False(ShellWindowPlacementPolicy.IsReachable(new ScreenRect(-1200, 100, 50, 900), TwoMonitors));
    }

    [Fact]
    public void Store_round_trips_bounds_and_shape()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-window-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ShellWindowPlacementStore(path);
            Assert.Null(store.Load());
            var placement = new ShellWindowPlacement(new ScreenRect(-1280, 40, 0, 1000), ShellWindowShape.Span);
            store.Save(placement);
            Assert.Equal(placement, store.Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Store_ignores_broken_or_degenerate_files()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-window-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Null(new ShellWindowPlacementStore(path).Load());
            File.WriteAllText(path, """{ "left": 100, "top": 100, "right": 100, "bottom": 500, "shape": "Normal" }""");
            Assert.Null(new ShellWindowPlacementStore(path).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
