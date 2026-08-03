using System;
using System.Windows;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 使用箇所（Find References）・診断一覧・呼び出し階層のポップアップを**どこに出すか**を決める純ロジック。
/// <para>
/// 以前は <c>PlacementTarget=PaneHost</c> ＋ <c>Placement=Center</c>、つまり**ペイン領域全体の中央**に
/// 固定で出していた。分割していると操作していない側のエディタに被り、しかも読んでいるコードの真上を
/// 覆う。結果の出どころ（＝いま操作しているエディタビュー）と表示位置が無関係なのが原因なので、
/// 呼び出したビューの矩形を基準にする。
/// </para>
/// <para>
/// 置き方は「そのエディタの<b>下端に沿わせる</b>」。一覧はクリックしてジャンプするための一時的な
/// 結果表示なので、キャレット周辺（画面中央〜上寄りで読んでいることが多い）を覆いにくい下側へ寄せる。
/// 入りきらない場合はそのビューの上端へ、それでも足りなければウィンドウ内へクランプする
/// （ウィンドウ外・画面外に出さない）。
/// </para>
/// </summary>
internal static class ReferencesPopupPlacement
{
    /// <summary>ビュー端・ウィンドウ端から空ける余白（px）。</summary>
    internal const double Margin = 8;

    /// <summary>
    /// ポップアップに許す最大幅。**置く場所を選ぶ前に、幅そのものを基準ビューに収まるまで切り詰める。**
    ///
    /// <para>置き方だけを工夫しても足りない：実測幅は XAML の上限（760）に張り付くのが常態で
    /// （長いパス＋プレビューが1件でもあれば、省略記号が付いても <c>DesiredSize</c> は縮まない）、
    /// 左右分割の各ビューが 760 より狭ければ、どこへ置いても反対側＝操作していないビューを覆う。
    /// 幅を先に詰めれば「呼び出したビューの中に収まる」を実際に満たせる。</para>
    ///
    /// <para>ただし狭すぎると一覧として用を成さないので <paramref name="minUsable"/> を下限にする。
    /// ビューがそれより狭い場合だけは、はみ出しを許してでも読める幅を優先する。</para>
    /// </summary>
    public static double MaxWidthIn(Rect target, double preferredMax, double minUsable)
        => Math.Max(minUsable, Math.Min(preferredMax, target.Width - Margin * 2));

    /// <summary>
    /// ポップアップ左上の位置を<b>ウィンドウ座標</b>で返す。
    /// </summary>
    /// <param name="popup">ポップアップの実寸（測定済みの DesiredSize）。</param>
    /// <param name="target">基準にするビュー（呼び出し元のエディタ）の矩形。ウィンドウ座標。</param>
    /// <param name="window">はみ出しを止める外枠（ウィンドウのクライアント領域）。</param>
    public static Point Place(Size popup, Rect target, Rect window)
    {
        // 横：基準ビューの左端に揃える（どのビューから出たかが位置で判る）。
        var x = target.Left + Margin;
        // 縦：基準ビューの下端に沿わせ、背が高くて入らなければ上端から。
        var y = target.Bottom - Margin - popup.Height;
        if (y < target.Top + Margin)
            y = target.Top + Margin;

        // **基準ビューの中に収める**のが先。ウィンドウにしかクランプしないと、実寸が上限幅
        // （XAML の MaxWidth=760。長いパス＋プレビューが1件でもあれば省略記号でも DesiredSize は
        // 縮まないので常に張り付く）のとき、左右分割の反対側＝操作していないビューを覆ってしまう。
        // ポップアップが基準ビューより大きいときは Clamp が下限（＝ビューの左上）を採るので、
        // はみ出す向きは常に「外側」ではなく「そのビューの右下方向」に固定される。
        x = Clamp(x, target.Left + Margin, target.Right - Margin - popup.Width);
        y = Clamp(y, target.Top + Margin, target.Bottom - Margin - popup.Height);

        // 最後にウィンドウで止める（ビューがウィンドウ端に接している場合の保険）。
        return new Point(
            Clamp(x, window.Left + Margin, window.Right - Margin - popup.Width),
            Clamp(y, window.Top + Margin, window.Bottom - Margin - popup.Height));
    }

    /// <summary>
    /// <see cref="Place"/> の結果を、<c>PlacementMode.Relative</c>（基準ビューの左上原点）の
    /// オフセットへ直したもの。ビューはこの値を <c>HorizontalOffset</c>/<c>VerticalOffset</c> に入れる。
    /// </summary>
    public static Point OffsetFrom(Size popup, Rect target, Rect window)
    {
        var placed = Place(popup, target, window);
        return new Point(placed.X - target.Left, placed.Y - target.Top);
    }

    /// <summary>ポップアップがウィンドウより大きいときは下限（左上）を優先する（右下へ押し出さない）。</summary>
    private static double Clamp(double value, double min, double max)
        => max < min ? min : Math.Min(Math.Max(value, min), max);
}
