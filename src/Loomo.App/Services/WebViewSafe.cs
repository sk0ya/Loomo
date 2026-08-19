namespace sk0ya.Loomo.App.Services;

/// <summary>WebView2 コントロールの安全な参照。</summary>
internal static class WebViewSafe
{
    /// <summary>いまの <see cref="CoreWebView2"/>。無ければ（まだ作っていない・<b>ブラウザプロセスが落ちた</b>）null。
    /// <para>落ちた後の WebView2 は <c>CoreWebView2</c> を<b>読むだけで</b>
    /// <see cref="InvalidOperationException"/>（"The WebView control is no longer valid because the browser
    /// process crashed"）を投げる——null にはならない。だから <c>view?.CoreWebView2 is not { } core</c> という
    /// 素直な確認は通り抜けられず、参照した側（フレーム適用など）が丸ごと落ちて、ペインは空のまま二度と
    /// 描かれない。プロファイルを共有している以上ブラウザプロセスは全インスタンスで1つなので、
    /// <b>他の Loomo の巻き添えでも落ちる</b>＝ここを均しておかないと複数起動で表に出る（§21.5.3）。</para></summary>
    public static CoreWebView2? TryCore(this WebView2CompositionControl? view)
    {
        try { return view?.CoreWebView2; }
        catch (InvalidOperationException) { return null; }
    }
}
