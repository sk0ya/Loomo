namespace sk0ya.Loomo.App.Layout;

/// <summary>袖（右端のミニチュア一覧）へのドロップ受け皿の当たり判定。舞台に出ているペインを
/// タイトルバーで掴み、袖の上で離すと「しまう」＝有効なまま非表示にする——その判定だけを持つ
/// 純粋関数。座標はすべて <c>PaneHost</c> 基準（ドラッグ中のプレビューと同じ座標系）。</summary>
public static class WingDropZone
{
    /// <summary>袖の左右の隙間（スプリッター・余白）も受け皿に含める幅。袖のカードそのものへ
    /// 1〜2px の狙い澄ましを要求しないための遊び。</summary>
    public const double Slack = 8;

    /// <summary>袖の受け皿。<paramref name="wingBounds"/> はホスト座標での袖の矩形。
    /// 上下は <paramref name="hostHeight"/> いっぱいまで広げる——袖の下の空きへ落としても
    /// 「袖へしまう」意図は変わらないため。袖が出ていなければ <see cref="Rect.Empty"/>
    /// ＝ドロップ先なし（受け皿の無い場所へ落として黙って消えることはない）。</summary>
    public static Rect Compute(Rect wingBounds, double hostHeight)
    {
        if (wingBounds.IsEmpty || wingBounds.Width <= 0 || wingBounds.Height <= 0)
            return Rect.Empty;
        var height = Math.Max(hostHeight, wingBounds.Bottom);
        return new Rect(
            wingBounds.X - Slack,
            0,
            wingBounds.Width + Slack * 2,
            Math.Max(height, 1));
    }

    public static bool Hits(Rect zone, Point position)
        => !zone.IsEmpty && zone.Width > 0 && zone.Contains(position);
}
