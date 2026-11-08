using System.Windows;
using sk0ya.Loomo.App.Layout;

namespace sk0ya.Loomo.Tests;

/// <summary>ドック中にペインのヘッダーを運んだとき、画面の実際の領域のどこへ落ちるか。</summary>
public class DockDropZonesTests
{
    private static readonly Size Area = new(1000, 800);

    /// <summary>右・下とも閉じていれば、器の端の帯（1/4）がその領域の受け皿。</summary>
    [Theory]
    [InlineData(500, 700, DockRegion.Bottom)]
    [InlineData(900, 300, DockRegion.Right)]
    [InlineData(400, 300, DockRegion.Center)]
    public void Closed_regions_use_edge_bands(double x, double y, DockRegion expected)
        => Assert.Equal(expected, DockDropZones.Resolve(Area, 0, 0, new Point(x, y))!.Value.Region);

    /// <summary>下の領域は横幅いっぱい——右の領域の真下も下の領域（組み方と同じ）。</summary>
    [Fact]
    public void Bottom_spans_under_the_right_region()
    {
        var zone = DockDropZones.Resolve(Area, 300, 200, new Point(950, 700))!.Value;
        Assert.Equal(DockRegion.Bottom, zone.Region);
        Assert.Equal(new Rect(0, 600, 1000, 200), zone.Preview);
    }

    /// <summary>開いている領域は、その大きさがそのまま受け皿になる。</summary>
    [Fact]
    public void Open_right_region_uses_its_width_above_the_bottom()
    {
        var zone = DockDropZones.Resolve(Area, 300, 200, new Point(750, 100))!.Value;
        Assert.Equal(DockRegion.Right, zone.Region);
        Assert.Equal(new Rect(700, 0, 300, 600), zone.Preview);
    }

    /// <summary>器の半分を占める下・右の領域でも、塗る枠は実際の大きさのまま（一律の上限で削ると枠が領域とズレた）。</summary>
    [Fact]
    public void Large_open_regions_are_not_shrunk()
    {
        var right = DockDropZones.Resolve(Area, 450, 400, new Point(600, 100))!.Value;
        Assert.Equal(DockRegion.Right, right.Region);
        Assert.Equal(new Rect(550, 0, 450, 400), right.Preview);
        var bottom = DockDropZones.Resolve(Area, 450, 400, new Point(100, 450))!.Value;
        Assert.Equal(new Rect(0, 400, 1000, 400), bottom.Preview);
    }

    /// <summary>中央を畳んで下が器いっぱいに広がっていても、中央へ戻す受け皿は残る。</summary>
    [Fact]
    public void Center_stays_reachable_when_a_region_fills_the_area()
        => Assert.Equal(DockRegion.Center, DockDropZones.Resolve(Area, 0, 800, new Point(300, 100))!.Value.Region);

    /// <summary>まだ測れていない器では判断しない。</summary>
    [Fact]
    public void Unmeasured_area_resolves_nothing()
        => Assert.Null(DockDropZones.Resolve(new Size(0, 0), 0, 0, new Point(0, 0)));
}
