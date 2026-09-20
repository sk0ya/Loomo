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

    /// <summary>その WebView2 がいま見ている URL（まだ生成前・落ちた後・空なら null）。
    /// <para><b>正本は <c>CoreWebView2.Source</c>——WPF ラッパーの <c>Source</c> ではない</b>。ラッパー側は
    /// <see cref="Uri"/> 型なので、<c>data:</c> のように Uri に載せ替えられない遷移では<b>前の値のまま
    /// 取り残される</b>。アドレス欄なら「前のページの URL が居座る」で済むが、切り離しブラウザは
    /// この URL で器の中身を<b>作り直す</b>（窓またぎの載せ替え・メインへ戻す・プロセス落ち・復元）ので、
    /// 古い値を読むと見ていたページが黙って1つ前へ戻る。だから読み口をここに集める。</para>
    /// <para>空文字も「無い」として null にする——遷移の種類によっては <c>Source</c> が空で返るので、
    /// 呼び手が次の手掛かり（引き出したときの URL・<c>PendingUrl</c>）へ落とせるようにするため。</para></summary>
    public static string? TryUrl(this WebView2CompositionControl? view)
    {
        if (view is null)
            return null;
        return Empty(view.TryCore()?.Source) ?? Empty(SafeWrapperSource(view));
        static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;
        static string? SafeWrapperSource(WebView2CompositionControl view)
        {
            try { return view.Source?.ToString(); }
            catch (InvalidOperationException) { return null; }
        }
    }
}
