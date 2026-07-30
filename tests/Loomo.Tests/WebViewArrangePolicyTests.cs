using System.Windows;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// WebView2 を 0 サイズで配置しないための下限クランプの回帰テスト。
/// 0 を通すと WebView2CompositionControl が <c>Direct3D11CaptureFramePool.Recreate</c> の
/// ArgumentException でアプリごと落ちる（EditorSupport を袖へ移した時のクラッシュがこれ）。
/// </summary>
public class WebViewArrangePolicyTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 361)]
    [InlineData(342, 0)]
    [InlineData(0.4, 0.9)]
    public void Degenerate_sizes_are_lifted_to_at_least_one_pixel(double width, double height)
    {
        var clamped = WebViewArrangePolicy.Clamp(new Size(width, height));
        Assert.True(clamped.Width >= WebViewArrangePolicy.MinLength);
        Assert.True(clamped.Height >= WebViewArrangePolicy.MinLength);
    }

    [Fact]
    public void Normal_sizes_pass_through_unchanged()
        => Assert.Equal(new Size(342, 361), WebViewArrangePolicy.Clamp(new Size(342, 361)));

    [Fact]
    public void Nan_is_treated_as_degenerate()
        => Assert.Equal(
            new Size(WebViewArrangePolicy.MinLength, 100),
            WebViewArrangePolicy.Clamp(new Size(double.NaN, 100)));

    /// <summary>アプリ内で作る WebView2 はすべてクランプ付きであること
    /// （素の WebView2CompositionControl を直に new すると落ちる経路が復活する）。UI 型なので STA。</summary>
    [Fact]
    public void Editor_support_factory_creates_the_clamped_control()
        => RunSta(() => Assert.IsType<LoomoWebView2>(new EditorSupportViewFactory().Create()));

    private static void RunSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { exception = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception is not null) throw exception;
    }
}
