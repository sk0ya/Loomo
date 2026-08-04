using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>タブ（サイドバーの TABS・各ペインのタブ列・切り離しウィンドウ）へ出すアイコン。
///
/// ファイル種別のアイコンはフォルダーツリー・検索結果とまったく同じ <see cref="FileIcons"/>（Catppuccin の
/// 線画）を引く。以前は Windows のシェルアイコン（SHGetFileInfo）だったが、同じ .cs がツリーでは線画・
/// タブではラスタの別絵になり、テーマの明暗にも追従しなかった。
///
/// ブラウザだけは「種別」ではなく「どのサイトか」が意味を持つので favicon を使い、取れないときだけ
/// 線画の地球儀へ落とす。</summary>
public sealed class TabIconService
{
    /// <summary>ターミナルのタブに出す絵は PowerShell（.ps1）のアイコンを流用する。</summary>
    private static readonly int TerminalIconIndex = FileIcons.IndexFor("terminal.ps1", isDirectory: false);

    /// <summary>favicon が取れないときの地球儀（凍結済みなので 1 個を使い回す）。</summary>
    private static readonly ImageSource FallbackBrowserIcon = CreateFallbackBrowserIcon();

    private readonly ConcurrentDictionary<string, Lazy<Task<ImageSource>>> _browserIconCache = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource GetTerminalIcon() => FileIcons.ImageFor(TerminalIconIndex);

    public ImageSource GetFileIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return FileIcons.ImageFor(FileIconData.DefaultFileIndex);

        return FileIcons.ImageFor(FileIcons.IndexFor(path, Directory.Exists(path)));
    }

    public ImageSource GetBrowserDefaultIcon() => FallbackBrowserIcon;

    public Task<ImageSource> GetBrowserIconAsync(CoreWebView2? coreWebView2, string? pageUrl)
    {
        if (coreWebView2 is null)
            return Task.FromResult(FallbackBrowserIcon);

        var cacheKey = GetBrowserCacheKey(coreWebView2.FaviconUri, pageUrl);
        if (string.IsNullOrWhiteSpace(cacheKey))
            return Task.FromResult(FallbackBrowserIcon);

        return _browserIconCache.GetOrAdd(cacheKey, _ =>
            new Lazy<Task<ImageSource>>(() => LoadBrowserIconAsync(coreWebView2), LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }

    private static string? GetBrowserCacheKey(string? faviconUri, string? pageUrl)
    {
        if (!string.IsNullOrWhiteSpace(faviconUri))
            return faviconUri.Trim();

        return string.IsNullOrWhiteSpace(pageUrl) ? null : pageUrl.Trim();
    }

    private static async Task<ImageSource> LoadBrowserIconAsync(CoreWebView2 coreWebView2)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(coreWebView2.FaviconUri))
                return FallbackBrowserIcon;

            await using var stream = await coreWebView2.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream is null)
                return FallbackBrowserIcon;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return FallbackBrowserIcon;
        }
    }

    private static ImageSource CreateFallbackBrowserIcon()
    {
        var outer = new SolidColorBrush(Color.FromRgb(0x9D, 0x9D, 0x9D));
        outer.Freeze();

        var inner = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
        inner.Freeze();

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(null, new Pen(outer, 1.1), Geometry.Parse("M8,1.5 A6.5,6.5 0 1 1 7.999,1.5 Z")));
        group.Children.Add(new GeometryDrawing(null, new Pen(inner, 1.0), Geometry.Parse("M2.5,8 H13.5 M8,2.5 C6.2,4.3 5.2,6.3 5.2,8 C5.2,9.7 6.2,11.7 8,13.5 M8,2.5 C9.8,4.3 10.8,6.3 10.8,8 C10.8,9.7 9.8,11.7 8,13.5")));

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
