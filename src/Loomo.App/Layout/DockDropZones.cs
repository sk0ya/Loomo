namespace sk0ya.Loomo.App.Layout;

/// <summary>ドック中にペインを運んでいる間、画面の<b>実際の領域</b>（中央・下・右）のどこへ落とすかを決める。
/// 右帯の3区画だけが受け皿だと、行き先の領域から目を離して帯の小さな区画を狙うことになる。
/// 並びはドックの組み方そのまま——下の領域は横幅いっぱい、右の領域はその上で止まる——なので、
/// 下の帯を先に判定し、残った高さの右端を右の帯にする。</summary>
public static class DockDropZones
{
    /// <summary>閉じている領域の受け皿の厚み（器に対する比）。開いている領域はその大きさがそのまま受け皿になる。</summary>
    public const double ClosedBandRatio = 0.25;
    /// <summary>中央へ戻す受け皿として必ず残す厚み（器に対する比）。中央を畳んで下や右が器いっぱいに
    /// 広がっていても中央へ落とせるように。開いている領域を削るのはこのときだけ——一律の上限で削ると、
    /// 塗った枠が実際の領域とズレる（下の領域が器の半分あると、枠が中央側へ食い込んでいた）。</summary>
    public const double MinCenterRatio = 0.2;

    /// <param name="area">中央・右・下をまとめた器の大きさ。</param>
    /// <param name="rightOpenWidth">右の領域の実際の幅（閉じていれば 0）。</param>
    /// <param name="bottomOpenHeight">下の領域の実際の高さ（閉じていれば 0）。</param>
    /// <param name="point">器の左上を原点にしたマウスの位置。</param>
    /// <returns>落とし先と、その落とし先として塗る矩形（器の座標）。器が測れていなければ null。</returns>
    public static (DockRegion Region, Rect Preview)? Resolve(Size area, double rightOpenWidth, double bottomOpenHeight, Point point)
    {
        if (area.Width <= 0 || area.Height <= 0)
            return null;
        var bottomBand = Band(area.Height, bottomOpenHeight);
        var rightBand = Band(area.Width, rightOpenWidth);
        var upperHeight = area.Height - bottomBand;
        if (point.Y >= upperHeight)
            return (DockRegion.Bottom, new Rect(0, upperHeight, area.Width, bottomBand));
        if (point.X >= area.Width - rightBand)
            return (DockRegion.Right, new Rect(area.Width - rightBand, 0, rightBand, upperHeight));
        return (DockRegion.Center, new Rect(0, 0, area.Width - rightBand, upperHeight));
    }

    private static double Band(double extent, double open)
    {
        var band = open > 0 ? open : extent * ClosedBandRatio;
        return Math.Min(band, extent * (1 - MinCenterRatio));
    }
}
