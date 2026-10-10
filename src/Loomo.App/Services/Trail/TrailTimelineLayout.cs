using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 軌跡の展開ビュー（§27.14）の座標計算。バーのドット列は「並び順」の軸（1点＝1スロット）だが、展開ビューは
/// <b>時刻の軸</b>——どれだけ間が空いたか、コマンドがどの移動の合間に走ったかが距離で読める。
/// WPF に触れない純粋な計算だけを置く（テスト用）。
/// </summary>
public static class TrailTimelineLayout
{
    /// <summary>既定の縮尺（1分あたりのピクセル）。9時間の作業日で 1600px 前後＝数画面ぶん。</summary>
    public const double DefaultPixelsPerMinute = 3;
    public const double MinPixelsPerMinute = 0.25;
    public const double MaxPixelsPerMinute = 40;

    /// <summary>軸の範囲。記録のある最初の時刻の時ちょうど〜最後の時刻の次の時ちょうど（<paramref name="now"/> を
    /// 渡せばそれも含める＝今日は「今」まで伸ばす）。何も無ければ <paramref name="now"/>（無ければ 0 時）の時間帯1つ。</summary>
    public static (DateTime Start, DateTime End) Range(IEnumerable<DateTime> times, DateTime? now = null)
    {
        DateTime? min = null, max = null;
        foreach (var t in now is { } n ? times.Append(n) : times)
        {
            if (min is null || t < min) min = t;
            if (max is null || t > max) max = t;
        }
        if (min is null || max is null)
        {
            var hour = FloorHour(now ?? DateTime.Today);
            return (hour, hour.AddHours(1));
        }
        var start = FloorHour(min.Value);
        var end = FloorHour(max.Value).AddHours(1);
        return (start, end);
    }

    public static double X(DateTime time, DateTime start, double pixelsPerMinute)
        => (time - start).TotalMinutes * pixelsPerMinute;

    public static DateTime TimeAt(double x, DateTime start, double pixelsPerMinute)
        => start.AddMinutes(x / pixelsPerMinute);

    public static double Width(DateTime start, DateTime end, double pixelsPerMinute)
        => Math.Max(0, (end - start).TotalMinutes * pixelsPerMinute);

    /// <summary>ホイール1刻みぶんの拡大縮小（上＝拡大）。範囲に収める。</summary>
    public static double Zoom(double pixelsPerMinute, int wheelDelta)
    {
        var factor = wheelDelta > 0 ? 1.25 : 1 / 1.25;
        return Math.Clamp(pixelsPerMinute * factor, MinPixelsPerMinute, MaxPixelsPerMinute);
    }

    /// <summary>カーソルの下の時刻を動かさずに縮尺を変えたときの、新しい水平スクロール位置。</summary>
    public static double ScrollOffsetKeeping(double offset, double cursorX, double oldPixelsPerMinute, double newPixelsPerMinute)
        => Math.Max(0, (offset + cursorX) * newPixelsPerMinute / oldPixelsPerMinute - cursorX);

    /// <summary>その時刻にいた地点：その時刻以前で最も新しい点。無ければ最も古い点。点が無ければ null。</summary>
    public static TrailEntryViewModel? EntryAt(IEnumerable<TrailEntryViewModel> entries, DateTime time)
    {
        TrailEntryViewModel? before = null, earliest = null;
        foreach (var entry in entries)
        {
            if (earliest is null || entry.Timestamp < earliest.Timestamp)
                earliest = entry;
            if (entry.Timestamp <= time && (before is null || entry.Timestamp >= before.Timestamp))
                before = entry;
        }
        return before ?? earliest;
    }

    /// <summary>軸に目盛りを打つ間隔（分）。ラベルどうしが <paramref name="minGapPixels"/> 未満に詰まらない最小の間隔。</summary>
    public static int TickMinutes(double pixelsPerMinute, double minGapPixels = 56)
    {
        foreach (var minutes in new[] { 5, 10, 15, 30, 60, 120, 180, 360 })
            if (minutes * pixelsPerMinute >= minGapPixels)
                return minutes;
        return 720;
    }

    private static DateTime FloorHour(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind);
}
