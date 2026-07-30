namespace sk0ya.Loomo.App.Layout;

/// <summary>
/// WebView2 を配置してよい最小サイズの決定（UI 非依存）。
///
/// <para><see cref="Microsoft.Web.WebView2.Wpf.WebView2CompositionControl"/> は <c>SizeChanged</c> ごとに
/// 内部の <c>Direct3D11CaptureFramePool</c> を新しいサイズで作り直す。幅か高さが 0 になると
/// <c>Recreate</c> が E_INVALIDARG を返し、<see cref="System.ArgumentException"/>
/// （「パラメーターが間違っています。」）が Dispatcher の未処理例外として飛んでアプリが即落ちする。</para>
///
/// <para>実際に踏んだ経路は<b>袖（ミニチュア）の描画元を組むとき</b>。
/// <c>ArrangeThumbnailSource</c> は <c>host.UpdateLayout()</c> でウィンドウ全体のレイアウトパスを走らせるので、
/// 組み替え途中のペインが一瞬 0 高さ/0 幅で配置される（実測ログ：袖構築中に Browser ペインの WebView2 が
/// <c>360x0</c> で配置された）。ペインを袖へ移した瞬間に落ちていたのがこれ。他にも列/行幅 0、
/// ヘッダーより低いペインで <c>LastChildFill</c> の中身が 0、等いくらでもある。</para>
///
/// <para>しかも<b>例外を握りつぶしても直らない</b>——フレームプールが壊れたまま次のフレーム到着で
/// <c>Direct3D11CaptureFrame.Dispose</c> がアクセス違反を起こしプロセスが死ぬ（実測）。
/// つまり「0 を渡さない」以外に手が無いので、配置サイズをここで下限クランプする。</para>
///
/// <para><see cref="Visibility.Collapsed"/> は安全（WPF は RenderSize を 0 にするが
/// SizeChanged を発火しないので <c>Recreate</c> まで届かない）。親から外すのも安全（サイズは保持される）。
/// 危ないのは「可視のまま 0 サイズで配置される」経路だけ。</para>
/// </summary>
public static class WebViewArrangePolicy
{
    /// <summary>配置サイズの下限（DIP）。1 未満はピクセル換算で 0 になり得るので許さない。</summary>
    public const double MinLength = 1;

    /// <summary>配置サイズを下限クランプする。</summary>
    public static Size Clamp(Size size) => new(ClampLength(size.Width), ClampLength(size.Height));

    private static double ClampLength(double length)
        => double.IsNaN(length) || length < MinLength ? MinLength : length;
}
