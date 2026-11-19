using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: ブラウザペインに見えている画面を撮り、印を付けて Claude Code へ渡す（§24.110 の 1）。
/// 渡し方は §24.3.1 と同じ「一時ファイル＋@参照」（送信はしない）。入口はページの右クリック ▸ Loomo とパレット。</summary>
public partial class ShellWindow {
    /// <summary>右クリックに「この画面を Claude Code へ渡す…」を出すか。Claude Code が動いているタブがあるときだけ。</summary>
    private Action? BrowserScreenToClaudeAction()
        => ClaudeTerminalTab() is null ? null : () => _ = HandBrowserScreenToClaudeAsync();

    private async Task HandBrowserScreenToClaudeAsync() {
        if (ClaudeTerminalTab() is null) {
            ReportBrowserScreenProblem("Claude Code が動いているターミナルがありません。ターミナルで claude を起動してください。");
            return;
        }
        if (ActiveBrowserView.TryCore() is not { } core) {
            ReportBrowserScreenProblem("画面を撮れませんでした（ブラウザのページがまだ開いていません）。");
            return;
        }
        BitmapSource screen;
        try {
            screen = await CaptureBrowserScreenAsync(core);
        }
        catch (Exception ex) {
            ReportBrowserScreenProblem($"画面を撮れませんでした: {ex.Message}");
            return;
        }
        var window = new ScreenMarkupWindow(screen,
            png => HandFileToClaudeAsync("browser-screen.png", path => File.WriteAllBytesAsync(path, png)));
        window.FitTo(this);
        window.ShowDialog();
    }

    /// <summary>撮る前に止まった理由。パレットから呼ぶとブラウザサポートの欄が見えていないことがあるので、トーストにも出す。</summary>
    private void ReportBrowserScreenProblem(string message) {
        _browserSupport.ReportStatus(message);
        ToastService.Error(message);
    }

    /// <summary>見えている範囲を PNG で撮って、画素の大きさのまま読み込む（ストリームを閉じても使えるよう OnLoad で）。</summary>
    private static async Task<BitmapSource> CaptureBrowserScreenAsync(CoreWebView2 core) {
        using var stream = new MemoryStream();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
