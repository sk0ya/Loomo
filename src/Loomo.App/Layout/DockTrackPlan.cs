namespace sk0ya.Loomo.App.Layout;

/// <summary>ドックの枠（行・列）の取り分。</summary>
public enum DockTrackSize
{
    /// <summary>畳む＝枠ごと 0。中身は <c>Visibility.Collapsed</c> にする
    /// ——0 サイズで配置された WebView2 はフレームプールごと壊れる（設計書 §26.3）。</summary>
    Collapsed,

    /// <summary>決めた高さ／幅（下の領域・右の領域の既定サイズ、またはスプリッターで変えた値）。</summary>
    Fixed,

    /// <summary>残り全部（<c>*</c>）。</summary>
    Fill
}

/// <summary>「いまどの領域に面が出ているか」から「どの枠がどれだけ取るか」への翻訳。
/// <para><b>形：</b>上の行に中央と右の領域が左右に並び、下の領域はその両方の下へ横幅いっぱいに敷く
/// （右の領域は下の領域の上で止まる）。</para>
/// <para><b>畳んだ領域は場所を残さない。</b>中央を畳めば右の領域が上の行を全部取り、上の行（中央も右も）
/// が空なら行ごと畳んで下の領域が全部を取る——空の枠を残して「ここには何もありません」と書くのは、
/// 閉じた人にとっては閉じ切れていないのと同じ。1枚も出ていなければ部屋は空になる（帯から戻せる）。</para>
/// <para><b>「残り全部」（<c>*</c>）は縦にも横にも必ずどこか1つが持つ。</b>中央の行と列はドック
/// だけのものではなく、サイドバー・下の領域・右帯がまたいでいる器でもある。引き取り手が居ないのに
/// 0 にすると、行なら高さを失って<b>帯ごと消え</b>、列なら全体が左詰めになって<b>帯が部屋の真ん中へ
/// 迷い出る</b>。だから中央が畳むのは<b>引き取り手が居るときだけ</b>——行は下の領域が全部を取るとき、
/// 列は右の領域がその場所を取るとき。それ以外は中身が無くても枠は残す（中身は <c>Collapsed</c> なので、
/// 見えるのは空の部屋と右端の帯）。</para>
/// <para>スプリッターは<b>両隣に中身があるときだけ</b>置く（片側が畳んであれば、掴んでも動かす先が無い）。</para></summary>
public readonly record struct DockTrackPlan(
    DockTrackSize CenterRow,
    DockTrackSize CenterColumn,
    DockTrackSize BottomRow,
    DockTrackSize RightColumn,
    bool BottomSplitter,
    bool RightSplitter)
{
    /// <param name="center">中央に面が立っているか。</param>
    /// <param name="bottom">下の領域に面が出ているか。</param>
    /// <param name="right">右の領域に面が出ているか。</param>
    public static DockTrackPlan For(bool center, bool bottom, bool right)
    {
        // 右の領域は中央と同じ行に住む（行は中央か右のどちらかが居れば要る）。
        var row = center || right;
        // 引き取り手：右が中央の列を取るのは中央が畳んであるとき、下が中央の行を取るのは
        // その行（中央も右も）が空のとき。取り手が居ないなら、空でも枠は残す。
        var rightTakesOver = right && !center;
        var bottomTakesOver = bottom && !row;
        return new DockTrackPlan(
            CenterRow: bottomTakesOver ? DockTrackSize.Collapsed : DockTrackSize.Fill,
            CenterColumn: rightTakesOver ? DockTrackSize.Collapsed : DockTrackSize.Fill,
            BottomRow: !bottom ? DockTrackSize.Collapsed
                : bottomTakesOver ? DockTrackSize.Fill
                : DockTrackSize.Fixed,
            RightColumn: !right ? DockTrackSize.Collapsed
                : rightTakesOver ? DockTrackSize.Fill
                : DockTrackSize.Fixed,
            BottomSplitter: bottom && row,
            RightSplitter: right && center);
    }
}
