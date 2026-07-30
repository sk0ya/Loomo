namespace sk0ya.Loomo.App.Views;

/// <summary>
/// 0 サイズで配置されても落ちない WebView2。<b>アプリ内の WebView2 はすべてこれを使う</b>
/// （ブラウザペインのタブ・EditorSupport のプレビュー・切り離しウィンドウの複製）。
///
/// <para>素の <see cref="WebView2CompositionControl"/> は幅か高さ 0 で配置されるとその場で落ちる。
/// 理由と「例外を握りつぶしても直らない」ことは <see cref="WebViewArrangePolicy"/> に記録した。
/// ここでは配置サイズを 1px 以上に保ち、0 の <c>SizeChanged</c> を内部へ届かせない。
/// 実際の表示領域が 0 の場面（隠したペイン・幅 0 の列）では見えないままなので見た目への影響は無い。</para>
/// </summary>
public sealed class LoomoWebView2 : WebView2CompositionControl
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        var clamped = WebViewArrangePolicy.Clamp(finalSize);
        if (clamped != finalSize)
            LogClampedArrange(finalSize);
        return base.ArrangeOverride(clamped);
    }

    /// <summary>0 サイズで配置された経路を後から特定できるように、祖先のサイズを控えておく
    /// （<c>LOOMO_PANE_DEBUG=1</c> のときだけ。既定では何もしない）。クランプで落ちなくなった代わりに
    /// 「どのコンテナが 0 を渡したか」は見えなくなるので、その手掛かりだけ残す。
    /// 祖先の <c>ActualWidth/Height</c> は<b>ひとつ前のパスの値</b>（親の <c>RenderSize</c> は子の配置後に入る）
    /// なので、今まさに 0 になっている親を指すわけではない——経路の特定用と割り切る。</summary>
    private void LogClampedArrange(Size finalSize)
    {
        if (!PaneLayoutDebugLog.Enabled)
            return;
        var chain = new List<string>();
        for (DependencyObject? node = this; node is not null && chain.Count < 12; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement element)
                chain.Add($"{element.GetType().Name}{(string.IsNullOrEmpty(element.Name) ? "" : $"({element.Name})")}"
                    + $" {element.ActualWidth:0.#}x{element.ActualHeight:0.#}");
        PaneLayoutDebugLog.Log(
            $"WebView2 の配置を {finalSize.Width:0.##}x{finalSize.Height:0.##} からクランプ: {string.Join(" <- ", chain)}");
    }
}
