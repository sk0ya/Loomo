using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// EditorSupport ペインの拡張子→提供者解決（EditorSupportRegistry / MarkdownEditorSupport）と、
/// プレビューの相対パス画像解決（MarkdownPreviewPaths）の検証。
/// ペインの自動開閉そのもの（ShellWindow）は UI 依存のためここでは扱わない。
/// </summary>
public class EditorSupportTests
{
    private static MarkdownEditorSupport CreateSupport(string? workspaceRoot = null)
    {
        var workspace = new FakeWorkspaceService();
        if (workspaceRoot is not null)
            workspace.OpenFolder(workspaceRoot);
        return new MarkdownEditorSupport(new AiSettings(), workspace);
    }

    private static EditorSupportRegistry CreateRegistry()
    {
        return new(new IEditorSupportProvider[]
        {
            CreateSupport(),
            new ImageEditorSupport(),
            new VGridEditorSupport(new AiSettings()),
            new BrowserEditorSupport()
        });
    }

    [Theory]
    [InlineData(@"C:\work\README.md")]
    [InlineData(@"C:\work\note.markdown")]
    [InlineData(@"C:\work\UPPER.MD")]
    public void Resolve_Markdownファイルには対応プロバイダを返す(string path)
    {
        var provider = CreateRegistry().Resolve(path);

        Assert.IsType<MarkdownEditorSupport>(provider);
    }

    [Theory]
    [InlineData(@"C:\work\data.csv")]
    [InlineData(@"C:\work\data.tsv")]
    [InlineData(@"C:\work\UPPER.CSV")]
    public void Resolve_CsvTsvファイルにはVGridプロバイダを返す(string path)
    {
        var provider = CreateRegistry().Resolve(path);

        Assert.IsType<VGridEditorSupport>(provider);
    }

    [Theory]
    [InlineData(@"C:\work\image.png")]
    [InlineData(@"C:\work\favicon.ico")]
    [InlineData(@"C:\work\photo.JPG")]
    [InlineData(@"C:\work\scan.tiff")]
    public void Resolve_画像ファイルには画像プロバイダを返す(string path)
    {
        var provider = CreateRegistry().Resolve(path);

        Assert.IsType<ImageEditorSupport>(provider);
    }

    [Theory]
    [InlineData(@"C:\work\manual.pdf")]
    [InlineData(@"C:\work\diagram.svg")]
    [InlineData(@"C:\work\page.html")]
    [InlineData(@"C:\work\page.htm")]
    [InlineData(@"C:\work\REPORT.PDF")]
    public void Resolve_ブラウザで開けるファイルにはBrowserプロバイダを返す(string path)
    {
        var provider = CreateRegistry().Resolve(path);

        Assert.IsType<BrowserEditorSupport>(provider);
    }

    [Fact]
    public void BrowserSupport_URIプロバイダとしてファイルのfileURIを返す()
    {
        var support = new BrowserEditorSupport();

        Assert.IsAssignableFrom<IEditorSupportUriProvider>(support);
        Assert.Equal("PDF: manual.pdf", support.DescribeTitle(@"C:\work\manual.pdf"));

        var uri = support.ResolveNavigationUri(@"C:\work\manual.pdf");
        Assert.StartsWith("file:///", uri);
        Assert.EndsWith("manual.pdf", uri);
    }

    [Fact]
    public void VGridSupport_タイトルはGridプレフィックスとファイル名()
    {
        var support = new VGridEditorSupport(new AiSettings());

        Assert.Equal("Grid: data.csv", support.DescribeTitle(@"C:\work\data.csv"));
    }

    [Theory]
    [InlineData(@"C:\work\Program.cs")]
    [InlineData(@"C:\work\拡張子なし")]
    [InlineData("")]
    [InlineData(null)]
    public void Resolve_未対応や無効なパスにはnullを返す(string? path)
    {
        Assert.Null(CreateRegistry().Resolve(path));
    }

    [Fact]
    public void Registry_同じ拡張子の重複登録は例外にする()
    {
        var workspace = new FakeWorkspaceService();
        var settings = new AiSettings();

        var ex = Assert.Throws<InvalidOperationException>(() => new EditorSupportRegistry(
            new IEditorSupportProvider[]
            {
                new MarkdownEditorSupport(settings, workspace),
                new DuplicateEditorSupport()
            }));

        Assert.Contains(".md", ex.Message);
    }

    [Fact]
    public void MarkdownSupport_本文を含む完全なHTMLを生成しタイトルにファイル名を出す()
    {
        var support = CreateSupport();
        const string path = @"C:\work\README.md";

        Assert.Equal("Preview: README.md", support.DescribeTitle(path));

        var html = support.RenderHtml(path, "# 見出し\n\n本文です。");
        Assert.Contains("<h1", html);
        Assert.Contains("本文です。", html);
        Assert.Contains("Preview: README.md", html);   // <title> へ反映される
    }

    [Fact]
    public void MarkdownSupport_相対パス画像を仮想ホストのbase経由で解決できるHTMLにする()
    {
        var support = CreateSupport(workspaceRoot: @"C:\work");

        var html = support.RenderHtml(@"C:\work\docs\README.md", "![図](images/arch.png)");

        // ShellWindow が preview.loomo をワークスペースルートへマップする前提で、base はファイルの
        // フォルダ位置を指し、相対 src はそのまま残す（../ でルート内を遡る画像も解決できる）。
        Assert.Contains("<base href=\"https://preview.loomo/docs/\">", html);
        Assert.Contains("<img src=\"images/arch.png\" alt=\"図\">", html);
    }

    [Fact]
    public void MarkdownSupport_mermaidフェンスは図用ブロックとして出力する()
    {
        var support = CreateSupport();

        var html = support.RenderHtml(@"C:\work\README.md", "```mermaid\ngraph TD\n  A-->B\n```");

        Assert.Contains("<pre class=\"mermaid\">", html);
        Assert.Contains("A--&gt;B", html);          // textContent として読まれるので HTML エンコードでよい
        // ページには mermaid ブートストラップが常駐し、.mermaid 要素があるときだけ遅延ロードする。
        Assert.Contains("https://assets.loomo/mermaid.min.js", html); // 同梱スクリプト（オフライン可）
        Assert.Contains("mermaid.initialize", html);
        Assert.DoesNotContain("language-mermaid", html); // 通常のコードブロックにはしない
    }

    [Fact]
    public void MarkdownSupport_mermaidが無ければ図ブロックにしない()
    {
        var support = CreateSupport();

        var html = support.RenderHtml(@"C:\work\README.md", "```csharp\nvar x = 1;\n```");

        // mermaid ランタイムは .mermaid 要素があるときだけ遅延ロードされる（ブートストラップは常駐するが
        // ここでは図が無いので run()/load は走らない）。通常のコードブロックとして描く。
        Assert.Contains("language-csharp", html);
        Assert.DoesNotContain("<pre class=\"mermaid\">", html);
    }

    [Fact]
    public void ImageSupport_WPFビジュアルプロバイダとして画像を扱う()
    {
        var support = new ImageEditorSupport();

        Assert.IsAssignableFrom<IEditorSupportVisualProvider>(support);
        Assert.Equal("Image: app icon.ico", support.DescribeTitle(@"C:\work\assets\app icon.ico"));
    }

    [Theory]
    [InlineData(800, 600, 1600, 1200)]
    [InlineData(320, 240, 1920, 1080)]
    [InlineData(1024, 256, 512, 512)]
    [InlineData(256, 1024, 512, 512)]
    public void ImageFitMath_計算した倍率では画像が表示領域からはみ出さない(
        double viewportWidth,
        double viewportHeight,
        double imageWidth,
        double imageHeight)
    {
        var zoom = ImageFitMath.CalculateFitZoom(viewportWidth, viewportHeight, imageWidth, imageHeight);

        Assert.True(imageWidth * zoom <= viewportWidth - ImageFitMath.SafetyInset + 0.001);
        Assert.True(imageHeight * zoom <= viewportHeight - ImageFitMath.SafetyInset + 0.001);
    }

    [Fact]
    public void ImageFitMath_画像Dpiで変わるDip寸法をそのまま使ってフィット倍率を出す()
    {
        // 3840px wide at 192 DPI is 1920 DIP wide in WPF; pixel widthで計算すると過剰に縮む。
        var zoom = ImageFitMath.CalculateFitZoom(
            viewportWidth: 960,
            viewportHeight: 540,
            imageWidth: 1920,
            imageHeight: 1080);

        Assert.Equal((540 - ImageFitMath.SafetyInset) / 1080, zoom, precision: 6);
    }

    [Fact]
    public void ImageSupport_実WPFレイアウトでも初期フィット表示は見切れない()
    {
        WpfLayoutTestHost.RunSta(() =>
        {
            var imagePath = Path.Combine(Path.GetTempPath(), $"loomo-fit-test-{Guid.NewGuid():N}.png");
            WpfLayoutTestHost.WriteTestPng(imagePath, pixelWidth: 1600, pixelHeight: 1200);

            try
            {
                var support = new ImageEditorSupport();
                var view = support.GetOrCreateView();
                var window = new Window
                {
                    Width = 360,
                    Height = 260,
                    Content = view,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None
                };

                window.Show();
                WpfLayoutTestHost.PumpDispatcher();
                support.UpdateAsync(imagePath, "").GetAwaiter().GetResult();
                WpfLayoutTestHost.PumpDispatcher();
                WpfLayoutTestHost.PumpDispatcher();

                var scroll = WpfLayoutTestHost.FindVisualChild<ScrollViewer>(view);
                var image = WpfLayoutTestHost.FindVisualChild<Image>(view);
                Assert.NotNull(scroll);
                Assert.NotNull(image);

                var maxWidth = scroll!.ViewportWidth - ImageFitMath.SafetyInset;
                var maxHeight = scroll.ViewportHeight - ImageFitMath.SafetyInset;
                Assert.True(image!.ActualWidth <= maxWidth + 0.001,
                    $"Image actual width {image.ActualWidth}, width property {image.Width}, source {image.Source?.Width} must be <= viewport {scroll.ViewportWidth} - inset. Extent={scroll.ExtentWidth}.");
                Assert.True(image.ActualHeight <= maxHeight + 0.001,
                    $"Image actual height {image.ActualHeight}, height property {image.Height}, source {image.Source?.Height} must be <= viewport {scroll.ViewportHeight} - inset. Extent={scroll.ExtentHeight}.");

                window.Close();
            }
            finally
            {
                File.Delete(imagePath);
            }
        });
    }
}

file static class WpfLayoutTestHost
{
    public static T? FindVisualChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
                return descendant;
        }

        return null;
    }

    public static void RunSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
            throw exception;
    }

    public static void PumpDispatcher()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    public static void WriteTestPng(string path, int pixelWidth, int pixelHeight)
    {
        var stride = pixelWidth * 4;
        var pixels = new byte[stride * pixelHeight];
        for (var y = 0; y < pixelHeight; y++)
        {
            for (var x = 0; x < pixelWidth; x++)
            {
                var offset = y * stride + x * 4;
                pixels[offset + 0] = 0x40;
                pixels[offset + 1] = (byte)(x % 256);
                pixels[offset + 2] = (byte)(y % 256);
                pixels[offset + 3] = 0xFF;
            }
        }

        var bitmap = BitmapSource.Create(
            pixelWidth,
            pixelHeight,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

file sealed class DuplicateEditorSupport : IEditorSupportHtmlProvider
{
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".md"];

    public string DescribeTitle(string filePath) => Path.GetFileName(filePath);

    public string RenderHtml(string filePath, string text) => "";
}

/// <summary>
/// MarkdownPreviewPaths.Resolve：仮想ホストのマップ先フォルダと base href の決定規則。
/// </summary>
public class MarkdownPreviewPathsTests
{
    [Fact]
    public void ルート配下のファイルはルートをマップしbaseは相対フォルダを指す()
    {
        var (folder, baseHref) = MarkdownPreviewPaths.Resolve(@"C:\work", @"C:\work\docs\api\README.md");

        Assert.Equal(@"C:\work", folder);
        Assert.Equal("https://preview.loomo/docs/api/", baseHref);
    }

    [Fact]
    public void ルート直下のファイルはbaseがホストルートになる()
    {
        var (folder, baseHref) = MarkdownPreviewPaths.Resolve(@"C:\work", @"C:\work\README.md");

        Assert.Equal(@"C:\work", folder);
        Assert.Equal("https://preview.loomo/", baseHref);
    }

    [Fact]
    public void 日本語や空白を含むフォルダ名はURLエスケープされる()
    {
        var (_, baseHref) = MarkdownPreviewPaths.Resolve(@"C:\work", @"C:\work\設計 資料\README.md");

        Assert.Equal($"https://preview.loomo/{Uri.EscapeDataString("設計 資料")}/", baseHref);
    }

    [Theory]
    [InlineData(@"C:\other\docs\README.md")] // ルート外
    [InlineData(@"D:\work\README.md")]       // 別ドライブ（GetRelativePath が絶対パスを返すケース）
    public void ルート外のファイルは自フォルダをマップする(string path)
    {
        var (folder, baseHref) = MarkdownPreviewPaths.Resolve(@"C:\work", path);

        Assert.Equal(Path.GetDirectoryName(path), folder);
        Assert.Equal("https://preview.loomo/", baseHref);
    }

    [Fact]
    public void ルート未設定なら自フォルダをマップする()
    {
        var (folder, baseHref) = MarkdownPreviewPaths.Resolve(null, @"C:\work\docs\README.md");

        Assert.Equal(@"C:\work\docs", folder);
        Assert.Equal("https://preview.loomo/", baseHref);
    }
}

/// <summary>
/// VGridTextSync：エディタ本文 ⇔ TsvDocument の往復変換（CSV/TSV 双方向同期の純ロジック部分）。
/// グリッド余白（EnsureSize の空行・空列）が本文へ漏れないこと、エコー検出の正規化比較を確認する。
/// </summary>
public class VGridTextSyncTests
{
    [Fact]
    public void Tsvの往復_本文が保たれグリッド余白は出力されない()
    {
        var doc = VGridTextSync.BuildDocument(@"C:\work\data.tsv", "a\tb\nc\td");

        // EnsureSize で実データより大きなグリッドになっている
        Assert.True(doc.RowCount > 2);

        Assert.Equal("a\tb\nc\td", VGridTextSync.Serialize(doc, "\n", trailingNewline: false));
    }

    [Fact]
    public void Csvの往復_カンマや引用符はDelimiterStrategyのエスケープ規則に従う()
    {
        var doc = VGridTextSync.BuildDocument(@"C:\work\data.csv", "name,note\n\"a,b\",plain");

        var text = VGridTextSync.Serialize(doc, "\n", trailingNewline: false);

        Assert.Equal("name,note\n\"a,b\",plain", text);
    }

    [Fact]
    public void セル編集が出力へ反映される()
    {
        var doc = VGridTextSync.BuildDocument(@"C:\work\data.csv", "a,b\nc,d");

        doc.Rows[1].Cells[1].Value = "edited";

        Assert.Equal("a,b\nc,edited", VGridTextSync.Serialize(doc, "\n", trailingNewline: false));
    }

    [Fact]
    public void 改行コードと末尾改行を指定どおり踏襲する()
    {
        var doc = VGridTextSync.BuildDocument(@"C:\work\data.csv", "a,b\r\nc,d\r\n");

        Assert.Equal("a,b\r\nc,d\r\n", VGridTextSync.Serialize(doc, "\r\n", trailingNewline: true));
    }

    [Theory]
    [InlineData("a,b\nc,d", "a,b\r\nc,d")]      // 改行コードの違いは同内容
    [InlineData("a,b\nc,d", "a,b\nc,d\n\n")]    // 末尾の空行も同内容
    public void NormalizeForCompare_改行差と末尾空行を無視して一致する(string left, string right)
    {
        Assert.Equal(VGridTextSync.NormalizeForCompare(left), VGridTextSync.NormalizeForCompare(right));
    }

    [Fact]
    public void NormalizeForCompare_内容が違えば一致しない()
    {
        Assert.NotEqual(
            VGridTextSync.NormalizeForCompare("a,b"),
            VGridTextSync.NormalizeForCompare("a,c"));
    }
}
