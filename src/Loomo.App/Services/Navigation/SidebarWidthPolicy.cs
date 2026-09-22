namespace sk0ya.Loomo.App.Services;

/// <summary>サイドバー列を畳む瞬間に「次に開くときの幅」として覚える値を決める（純ロジック）。
///
/// <para><b>人が決めた幅（<c>ColumnDefinition.Width</c>）が正</b>——`GridSplitter` はドラッグの結果を
/// `Width` へ書き戻すので、ドラッグ後もここが本物。実測（<c>ActualWidth</c>）は「いま画面に収まっている
/// 幅」でしかなく、この列は絶対幅なのに、窓を狭めたり袖（固定 250px）を出したりして中央列
/// （`MinWidth=160`）に押されると <c>Width</c> より細く配置される。そこで実測を覚えると、畳んで開き直した
/// 瞬間に人が広げた幅が永久に失われる（400px にしてから窓を狭め、畳んで開くと 200px のまま戻らない）。</para>
///
/// <para>実測に頼るのは <c>Width</c> がピクセルで取れないとき（Auto／星）だけで、それも無ければ既定へ。</para>
/// </summary>
internal static class SidebarWidthPolicy
{
    /// <summary>幅の記録が無い／読めないときの既定幅。</summary>
    public const double DefaultWidth = 220;

    /// <summary>畳む瞬間に覚える幅。</summary>
    /// <param name="width">列に設定されている幅（スプリッターのドラッグ結果はここに入る）。</param>
    /// <param name="actualWidth">いま配置されている実測幅。</param>
    public static GridLength Remember(GridLength width, double actualWidth)
        => width is { IsAbsolute: true, Value: > 0 }
            ? width
            : new GridLength(actualWidth > 0 ? actualWidth : DefaultWidth);

    /// <summary>覚えていた幅から、開き直すときに書き戻す幅。</summary>
    public static GridLength Restore(GridLength saved)
        => saved is { IsAbsolute: true, Value: > 0 } ? saved : new GridLength(DefaultWidth);
}
