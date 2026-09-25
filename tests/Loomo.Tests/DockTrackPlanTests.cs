using sk0ya.Loomo.App.Layout;

namespace sk0ya.Loomo.Tests;

/// <summary>ドックの枠の取り分。<b>畳んだ領域は場所を残さない</b>——空の枠に案内文を置いて
/// お茶を濁していたのをやめ、閉じたぶんは残った面が取るようにした分の回帰テスト。</summary>
public class DockTrackPlanTests
{
    /// <summary>3領域とも出ている＝中央が残り全部、下と右は決めた高さ／幅。</summary>
    [Fact]
    public void All_three_open()
    {
        var plan = DockTrackPlan.For(center: true, bottom: true, right: true);

        Assert.Equal(DockTrackSize.Fill, plan.CenterRow);
        Assert.Equal(DockTrackSize.Fill, plan.CenterColumn);
        Assert.Equal(DockTrackSize.Fixed, plan.BottomRow);
        Assert.Equal(DockTrackSize.Fixed, plan.RightColumn);
        Assert.True(plan.BottomSplitter);
        Assert.True(plan.RightSplitter);
    }

    /// <summary>中央を畳んだら、上の行は右の領域が取る（空の枠は残さない）。
    /// 下の領域は中央と右の両方の下に敷いてあるので、決めた高さのまま残る。</summary>
    [Fact]
    public void Closing_the_center_gives_its_place_to_the_right()
    {
        var plan = DockTrackPlan.For(center: false, bottom: true, right: true);

        Assert.Equal(DockTrackSize.Fill, plan.CenterRow);          // 右は中央と同じ行に住む
        Assert.Equal(DockTrackSize.Collapsed, plan.CenterColumn);
        Assert.Equal(DockTrackSize.Fixed, plan.BottomRow);
        Assert.Equal(DockTrackSize.Fill, plan.RightColumn);
        Assert.True(plan.BottomSplitter);    // 上に右の領域がある
        Assert.False(plan.RightSplitter);    // 左に何も無い＝掴んでも動かす先が無い
    }

    /// <summary>中央も右も無ければ<b>行ごと</b>畳んで、下の領域が全部を取る。
    /// 畳むのは行だけで、列は残す（列は下の領域・帯がまたいでいる）。</summary>
    [Fact]
    public void Only_the_bottom_region_fills_everything()
    {
        var plan = DockTrackPlan.For(center: false, bottom: true, right: false);

        Assert.Equal(DockTrackSize.Collapsed, plan.CenterRow);
        Assert.Equal(DockTrackSize.Fill, plan.CenterColumn);       // 列を 0 にすると帯が迷い出る
        Assert.Equal(DockTrackSize.Fill, plan.BottomRow);
        Assert.Equal(DockTrackSize.Collapsed, plan.RightColumn);
        Assert.False(plan.BottomSplitter);
        Assert.False(plan.RightSplitter);
    }

    /// <summary>右だけ＝上の行を右が全部取る（下は無いので行は中央のまま）。</summary>
    [Fact]
    public void Only_the_right_region_fills_everything()
    {
        var plan = DockTrackPlan.For(center: false, bottom: false, right: true);

        Assert.Equal(DockTrackSize.Fill, plan.CenterRow);
        Assert.Equal(DockTrackSize.Collapsed, plan.CenterColumn);
        Assert.Equal(DockTrackSize.Collapsed, plan.BottomRow);
        Assert.Equal(DockTrackSize.Fill, plan.RightColumn);
        Assert.False(plan.BottomSplitter);
        Assert.False(plan.RightSplitter);
    }

    /// <summary>中央の行を畳むのは「下が全部を取るとき」だけ。中央の行は右の領域・右帯・サイドバーが
    /// またいでいる行でもあるので、下が引き取らないのに 0 にすると<b>右も帯も消える</b>。</summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]     // 右があれば、下があっても行は右のもの
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]   // 何も出ていなくても残す
    public void The_center_row_only_collapses_when_the_bottom_takes_over(bool center, bool bottom, bool right)
    {
        var plan = DockTrackPlan.For(center, bottom, right);

        Assert.Equal(DockTrackSize.Fill, plan.CenterRow);
        Assert.Equal(DockTrackSize.Collapsed, DockTrackPlan.For(false, true, false).CenterRow);
    }

    /// <summary>中央だけ＝ほかの枠は 0（1枚を邪魔するものを残さない）。</summary>
    [Fact]
    public void Only_the_center()
    {
        var plan = DockTrackPlan.For(center: true, bottom: false, right: false);

        Assert.Equal(DockTrackSize.Fill, plan.CenterRow);
        Assert.Equal(DockTrackSize.Fill, plan.CenterColumn);
        Assert.Equal(DockTrackSize.Collapsed, plan.BottomRow);
        Assert.Equal(DockTrackSize.Collapsed, plan.RightColumn);
        Assert.False(plan.BottomSplitter);
        Assert.False(plan.RightSplitter);
    }

    /// <summary>1枚も出ていなければ部屋は空になる。ただし<b>中央の枠は行も列も残る</b>——帯
    /// （とサイドバー）はこの枠をまたいでいるので、0 にすると高さを失って消えるか、
    /// 全体が左詰めになって帯が部屋の真ん中へ迷い出る。</summary>
    [Fact]
    public void Nothing_open_leaves_an_empty_room_but_keeps_the_bar()
    {
        var plan = DockTrackPlan.For(center: false, bottom: false, right: false);

        Assert.Equal(DockTrackSize.Fill, plan.CenterRow);
        Assert.Equal(DockTrackSize.Fill, plan.CenterColumn);
        Assert.Equal(DockTrackSize.Collapsed, plan.BottomRow);
        Assert.Equal(DockTrackSize.Collapsed, plan.RightColumn);
        Assert.False(plan.BottomSplitter);
        Assert.False(plan.RightSplitter);
    }

    /// <summary>どの組み合わせでも「残り全部」は<b>縦も横もちょうど1つ</b>。
    /// 0 なら枠が潰れて帯ごと消え、2つなら畳んだはずの領域が場所を取り返す。</summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public void Exactly_one_track_fills_each_axis(bool center, bool bottom, bool right)
    {
        var plan = DockTrackPlan.For(center, bottom, right);

        Assert.Equal(1, new[] { plan.CenterRow, plan.BottomRow }.Count(t => t == DockTrackSize.Fill));
        Assert.Equal(1, new[] { plan.CenterColumn, plan.RightColumn }.Count(t => t == DockTrackSize.Fill));
    }

    /// <summary>スプリッターは両隣に中身があるときだけ。</summary>
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, true, false)]
    public void Splitters_need_both_sides(bool center, bool bottom, bool right, bool bottomSplitter)
    {
        var plan = DockTrackPlan.For(center, bottom, right);

        Assert.Equal(bottomSplitter, plan.BottomSplitter);
        Assert.Equal(right && center, plan.RightSplitter);
    }
}
