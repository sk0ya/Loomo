using System.Windows;
using sk0ya.Loomo.App.Layout;

namespace sk0ya.Loomo.Tests;

public class WingDropZoneTests
{
    /// <summary>袖（右端の一覧）は PaneHost の右外にある。ドラッグ中の座標は PaneHost 基準なので、
    /// 受け皿もそのまま右外の座標で当たること——ここを PaneHost 内に丸めると「袖へしまう」が
    /// 一切当たらなくなる。</summary>
    [Fact]
    public void Wing_outside_the_pane_host_still_hits()
    {
        var zone = WingDropZone.Compute(new Rect(1000, 8, 250, 700), hostHeight: 720);
        Assert.True(WingDropZone.Hits(zone, new Point(1100, 300)));
    }

    /// <summary>袖の上下の余白（ツールバーの上・カードの下の空き）へ落としても意図は同じ。</summary>
    [Fact]
    public void Wing_padding_above_and_below_the_cards_is_part_of_the_target()
    {
        var zone = WingDropZone.Compute(new Rect(1000, 8, 250, 300), hostHeight: 720);
        Assert.True(WingDropZone.Hits(zone, new Point(1100, 2)));
        Assert.True(WingDropZone.Hits(zone, new Point(1100, 700)));
    }

    /// <summary>袖の左の隙間（スプリッター）にも遊びを持たせる。1〜2px の狙い澄ましは要求しない。</summary>
    [Fact]
    public void Splitter_gap_next_to_the_wing_is_forgiving()
    {
        var zone = WingDropZone.Compute(new Rect(1000, 8, 250, 700), hostHeight: 720);
        Assert.True(WingDropZone.Hits(zone, new Point(995, 300)));
        Assert.False(WingDropZone.Hits(zone, new Point(980, 300)));
    }

    /// <summary>袖が出ていなければ受け皿は無い＝どこで離してもレイアウトは変わらない
    /// （受け皿の無い場所へ落としてペインが黙って消えることはない）。</summary>
    [Fact]
    public void No_wing_means_no_drop_target()
    {
        var zone = WingDropZone.Compute(Rect.Empty, hostHeight: 720);
        Assert.False(WingDropZone.Hits(zone, new Point(1100, 300)));
        Assert.False(WingDropZone.Hits(WingDropZone.Compute(new Rect(1000, 8, 0, 0), 720), new Point(1000, 8)));
    }
}
