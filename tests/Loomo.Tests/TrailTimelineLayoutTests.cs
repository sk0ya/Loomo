using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

/// <summary>軌跡の展開ビュー（§27.14）の時刻の軸。</summary>
public sealed class TrailTimelineLayoutTests
{
    private static readonly DateTime Day = new(2026, 10, 10);

    private static TrailEntryViewModel Entry(int id, int hour, int minute)
        => new(id, TrailEntryKind.File, $@"C:\w\{id}.cs", $"{id}.cs", Day.AddHours(hour).AddMinutes(minute));

    [Fact]
    public void 軸は最初の時ちょうどから最後の次の時ちょうどまで()
    {
        var (start, end) = TrailTimelineLayout.Range([Day.AddHours(9).AddMinutes(40), Day.AddHours(11).AddMinutes(5)]);

        Assert.Equal(Day.AddHours(9), start);
        Assert.Equal(Day.AddHours(12), end);
    }

    [Fact]
    public void 今日は今まで伸ばし記録が無ければ今の時間帯だけ()
    {
        var now = Day.AddHours(15).AddMinutes(30);

        Assert.Equal((Day.AddHours(9), Day.AddHours(16)), TrailTimelineLayout.Range([Day.AddHours(9).AddMinutes(1)], now));
        Assert.Equal((Day.AddHours(15), Day.AddHours(16)), TrailTimelineLayout.Range([], now));
    }

    [Fact]
    public void 座標と時刻は縮尺で往復する()
    {
        var start = Day.AddHours(9);
        var x = TrailTimelineLayout.X(Day.AddHours(10).AddMinutes(30), start, 3);

        Assert.Equal(270, x);
        Assert.Equal(Day.AddHours(10).AddMinutes(30), TrailTimelineLayout.TimeAt(x, start, 3));
    }

    [Fact]
    public void 拡大縮小してもカーソルの下の時刻は動かない()
    {
        var start = Day.AddHours(9);
        const double offset = 500, cursor = 120;
        var before = TrailTimelineLayout.TimeAt(offset + cursor, start, 3);

        var zoomed = TrailTimelineLayout.Zoom(3, +120);
        var newOffset = TrailTimelineLayout.ScrollOffsetKeeping(offset, cursor, 3, zoomed);

        Assert.True(zoomed > 3);
        Assert.Equal(before, TrailTimelineLayout.TimeAt(newOffset + cursor, start, zoomed));
        Assert.Equal(TrailTimelineLayout.MaxPixelsPerMinute, TrailTimelineLayout.Zoom(TrailTimelineLayout.MaxPixelsPerMinute, +120));
    }

    [Fact]
    public void コマンドの時刻にいた地点はそれ以前で最も新しい点()
    {
        var entries = new[] { Entry(1, 9, 0), Entry(2, 9, 30), Entry(3, 10, 0) };

        Assert.Equal(2, TrailTimelineLayout.EntryAt(entries, Day.AddHours(9).AddMinutes(45))!.Id);
        Assert.Equal(3, TrailTimelineLayout.EntryAt(entries, Day.AddHours(10))!.Id);
        Assert.Equal(1, TrailTimelineLayout.EntryAt(entries, Day.AddHours(8))!.Id);   // 最初の点より前なら最初の点
        Assert.Null(TrailTimelineLayout.EntryAt([], Day));
    }

    [Fact]
    public void 目盛りはラベルが詰まらない最小の間隔()
    {
        Assert.Equal(60, TrailTimelineLayout.TickMinutes(1));     // 1px/分 → 60分で60px
        Assert.Equal(5, TrailTimelineLayout.TickMinutes(20));     // 拡大すれば5分刻み
        Assert.Equal(30, TrailTimelineLayout.TickMinutes(3));     // 既定の縮尺では30分刻み
    }
}
