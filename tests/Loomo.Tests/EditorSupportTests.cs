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
        return new MarkdownEditorSupport(new LoomoSettings(), workspace);
    }

    private static EditorSupportRegistry CreateRegistry()
    {
        return new(new IEditorSupportProvider[]
        {
            CreateSupport(),
            new JsonEditorSupport(new LoomoSettings(), new JsonSchemaValidator()),
            new ImageEditorSupport(),
            new VGridEditorSupport(new LoomoSettings()),
            new ExcelEditorSupport(new LoomoSettings()),
            new WordEditorSupport(new LoomoSettings()),
            new BrowserEditorSupport(),
            new PochiEditorSupport()
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

    [Theory]
    [InlineData(@"C:\work\package.json")]
    [InlineData(@"C:\work\tsconfig.JSON")]
    [InlineData(@"C:\work\.vscode\settings.jsonc")]
    public void Resolve_JsonファイルにはJsonプロバイダを返す(string path)
    {
        var provider = CreateRegistry().Resolve(path);

        Assert.IsType<JsonEditorSupport>(provider);
    }

    [Theory]
    [InlineData(@"C:\work\diagram.pochi.json")]
    [InlineData(@"C:\work\図.POCHI.JSON")]
    public void Resolve_複合拡張子は汎用の拡張子より優先する(string path)
    {
        // .pochi.json は Path.GetExtension だと ".json" になるので、素朴な解決だと JSON 側が勝つ。
        var provider = CreateRegistry().Resolve(path);

        Assert.IsType<PochiEditorSupport>(provider);
    }

    [Fact]
    public void PochiSupport_公開ビルドへ対象ファイル付きでナビゲートする()
    {
        var support = new PochiEditorSupport();

        Assert.IsAssignableFrom<IEditorSupportUriProvider>(support);
        Assert.False(support.UsesEditorText);   // 図面はブリッジ（hostDoc）で渡す
        Assert.Equal("Pochi: diagram.pochi.json", support.DescribeTitle(@"C:\work\diagram.pochi.json"));

        var uri = support.ResolveNavigationUri(@"C:\work\diagram.pochi.json");
        Assert.StartsWith(PochiEditorSupport.AppUrl, uri);
        // ファイルごとに URI が変わること＝別の図面へ切り替えたときに再ナビゲートが起きる条件。
        Assert.NotEqual(uri, support.ResolveNavigationUri(@"C:\work\other.pochi.json"));
    }

    [Fact]
    public void JsonSupport_オブジェクトを折りたたみツリーのHTMLにする()
    {
        var support = new JsonEditorSupport(new LoomoSettings(), new JsonSchemaValidator());
        const string path = @"C:\work\data.json";

        Assert.Equal("JSON: data.json", support.DescribeTitle(path));
        Assert.IsAssignableFrom<IEditorSupportIncrementalHtmlProvider>(support);

        var html = support.RenderHtml(path, """{ "name": "loomo", "count": 3, "ok": true }""");
        Assert.Contains("JSON: data.json", html);       // <title>
        Assert.Contains("id=\"json-root\"", html);
        Assert.Contains("\"name\"", html);
        Assert.Contains("loomo", html);
        Assert.Contains("class=\"node\"", html);          // 折りたたみ可能なルートオブジェクト
    }

    [Fact]
    public void JsonSupport_配列とネストの件数を表示する()
    {
        var support = new JsonEditorSupport(new LoomoSettings(), new JsonSchemaValidator());

        var body = support.RenderBody(@"C:\work\data.json", """{ "items": [1, 2, 3] }""");

        Assert.Contains("3 要素", body);   // 配列の件数
        Assert.Contains("1 項目", body);   // ルートオブジェクトの件数
    }

    [Fact]
    public void JsonSupport_コメントと末尾カンマを許容する()
    {
        var support = new JsonEditorSupport(new LoomoSettings(), new JsonSchemaValidator());

        var body = support.RenderBody(@"C:\work\data.jsonc", "{ // 設定\n  \"a\": 1, }");

        Assert.Contains("\"a\"", body);
        Assert.DoesNotContain("解析できません", body);
    }

    [Fact]
    public void JsonSupport_壊れたJSONはエラーと原文を出す()
    {
        var support = new JsonEditorSupport(new LoomoSettings(), new JsonSchemaValidator());

        var body = support.RenderBody(@"C:\work\data.json", "{ \"a\": ");

        Assert.Contains("解析できません", body);
        Assert.Contains("class=\"raw\"", body);   // 原文を併記
    }

    [Fact]
    public void JsonSupport_HTML特殊文字をエスケープする()
    {
        var support = new JsonEditorSupport(new LoomoSettings(), new JsonSchemaValidator());

        var body = support.RenderBody(@"C:\work\data.json", """{ "html": "<b>&</b>" }""");

        Assert.Contains("&lt;b&gt;&amp;&lt;/b&gt;", body);
        Assert.DoesNotContain("<b>&</b>", body);
    }

    [Fact]
    public void JsonSupport_各ノードにJSONパスとコピー導線を埋め込む()
    {
        var support = new JsonEditorSupport(new LoomoSettings(), new JsonSchemaValidator());

        var body = support.RenderBody(@"C:\work\data.json", """{ "items": [ { "name": "x" } ] }""");

        // ネスト：識別子キーは .key、配列は [i] で連結する
        Assert.Contains("data-path=\"$.items[0].name\"", body);
        // 値はクリックでコピーできるよう data-val を持つ
        Assert.Contains("data-val=\"x\"", body);
        // 行末にパスコピーのアイコン
        Assert.Contains("class=\"copy\"", body);
    }

    [Fact]
    public void JsonSupport_識別子でないキーはブラケット表記のパスにする()
    {
        var support = new JsonEditorSupport(new LoomoSettings(), new JsonSchemaValidator());

        var body = support.RenderBody(@"C:\work\data.json", """{ "a b": 1 }""");

        Assert.Contains("data-path=\"$[&quot;a b&quot;]\"", body);   // 属性内なので " はエンコードされる
    }

    [Fact]
    public void JsonSupport_絞り込み用の検索ボックスをページに出す()
    {
        var support = new JsonEditorSupport(new LoomoSettings(), new JsonSchemaValidator());

        var html = support.RenderHtml(@"C:\work\data.json", """{ "a": 1 }""");

        Assert.Contains("id=\"json-filter\"", html);
    }

    [Fact]
    public void JsonSupport_各ノードにソース行番号を埋めエディタへ飛べる()
    {
        var support = new JsonEditorSupport(new LoomoSettings(), new JsonSchemaValidator());
        // 1行目:{ 2行目:name 3行目:nested{ 4行目:deep 5行目:} 6行目:}
        var json = "{\n  \"name\": \"x\",\n  \"nested\": {\n    \"deep\": 1\n  }\n}";

        var body = support.RenderBody(@"C:\work\data.json", json);

        Assert.Contains("data-path=\"$.name\" data-line=\"2\"", body);
        Assert.Contains("data-path=\"$.nested\" data-line=\"3\"", body);
        Assert.Contains("data-path=\"$.nested.deep\" data-line=\"4\"", body);
        Assert.Contains("class=\"goto\"", body);   // 「エディタで開く」導線
    }

    [Fact]
    public void VGridSupport_タイトルはGridプレフィックスとファイル名()
    {
        var support = new VGridEditorSupport(new LoomoSettings());

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
        var settings = new LoomoSettings();

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
                var support = new ImageVisual();
                var view = support.View;
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
                support.PrepareAsync(imagePath, "", CancellationToken.None)
                       .GetAwaiter().GetResult()
                       .Invoke();
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

    /// <summary>マルチルート：基準はプライマリではなく<b>そのファイルを担当するフォルダー</b>。
    /// プライマリ固定だと追加フォルダーのファイルは常に「基準外」に落ち、
    /// <c>../assets/img.png</c> のような上への相対画像が解決できなくなる（実際にそうなっていた）。</summary>
    [Fact]
    public void 追加フォルダーのファイルはそのフォルダーを基準にする()
    {
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(@"C:\work");
        workspace.AddFolder(@"D:\shared\lib");
        var support = new MarkdownEditorSupport(new LoomoSettings(), workspace);
        var file = @"D:\shared\lib\docs\README.md";

        // PageContextKey は base href をそのまま含むので、基準の選び方がここに出る。
        var key = support.PageContextKey(file, "# hello");

        Assert.Contains("https://preview.loomo/docs/", key);
    }

    // ── アウトライン（見出し一覧）の表示切替 ────────────────────────────────
    //
    // 一覧そのものはページ側 JS が本文の見出しから組む（本文差し替えのたびに組み直せる）ので、
    // C# 側の責務は「ページへ ON/OFF を焼き込む」と「切替でページを組み直させる」の2つだけ。

    [Fact]
    public void MarkdownSupport_アウトライン設定はページへ焼き込まれる()
    {
        var settings = new LoomoSettings();
        var support = new MarkdownEditorSupport(settings, new FakeWorkspaceService());
        const string path = @"C:\work\README.md";

        Assert.Contains("outlineEnabled = false", support.RenderHtml(path, "# 見出し"));

        settings.Appearance.MarkdownOutlineVisible = true;
        var html = support.RenderHtml(path, "# 見出し");
        Assert.Contains("outlineEnabled = true", html);
        Assert.Contains(".loomo-outline-panel", html);   // 幅・配色はページの CSS が持つ
    }

    /// <summary>アウトライン表示は<b>ページ構造</b>を変えるので鍵に含める。含めないと切替が
    /// 本文差し替え（setBody）で流れてしまい、ボタンを押しても一覧が出ない／消えない。</summary>
    [Fact]
    public void MarkdownSupport_アウトラインの切替でページの鍵が変わる()
    {
        var settings = new LoomoSettings();
        var support = new MarkdownEditorSupport(settings, new FakeWorkspaceService());
        const string path = @"C:\work\README.md";

        var off = support.PageContextKey(path, "# 見出し");
        settings.Appearance.MarkdownOutlineVisible = true;

        Assert.NotEqual(off, support.PageContextKey(path, "# 見出し"));
    }

    /// <summary>効かないモードの設定を鍵に入れると、ページの中身が1バイトも変わらないのに
    /// フル再構築が走る（marp を発表中にアウトラインを押すとスライドが1枚目へ戻っていた）。</summary>
    [Fact]
    public void MarkdownSupport_効かないモードの設定はページの鍵を変えない()
    {
        var settings = new LoomoSettings();
        var support = new MarkdownEditorSupport(settings, new FakeWorkspaceService());
        const string path = @"C:\work\deck.md";
        const string marp = "---\nmarp: true\n---\n\n# スライド";
        const string plain = "# 見出し";

        var marpBefore = support.PageContextKey(path, marp);
        var plainBefore = support.PageContextKey(path, plain);

        // アウトラインは通常ドキュメントだけに効く＝marp の鍵は動かない。
        settings.Appearance.MarkdownOutlineVisible = true;
        Assert.Equal(marpBefore, support.PageContextKey(path, marp));
        Assert.NotEqual(plainBefore, support.PageContextKey(path, plain));

        // 発表モードは marp だけに効く＝通常ドキュメントの鍵は動かない。
        plainBefore = support.PageContextKey(path, plain);
        settings.Appearance.MarkdownSlideMode = true;
        Assert.NotEqual(marpBefore, support.PageContextKey(path, marp));
        Assert.Equal(plainBefore, support.PageContextKey(path, plain));
    }

    [Fact]
    public async Task Pipeline_アウトライントグルはMarkdownプレビューにだけ出す()
    {
        var settings = new LoomoSettings();
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(@"C:\work");
        var pipeline = new EditorSupportPipeline();

        var markdown = await pipeline.PrepareAsync(
            new MarkdownEditorSupport(settings, workspace),
            EditorSupportContext.For(workspace, @"C:\work\README.md", "# 見出し", null, "Dracula"));
        var json = await pipeline.PrepareAsync(
            new JsonEditorSupport(settings, new JsonSchemaValidator()),
            EditorSupportContext.For(workspace, @"C:\work\package.json", "{}", null, "Dracula"));
        // marp スライドではアウトラインを組まないので、押せるのに何も起きないボタンを出さない。
        var deck = await pipeline.PrepareAsync(
            new MarkdownEditorSupport(settings, workspace),
            EditorSupportContext.For(
                workspace, @"C:\work\deck.md", "---\nmarp: true\n---\n\n# スライド", null, "Dracula"));

        Assert.True(markdown.ShowOutline);
        Assert.False(json.ShowOutline);
        Assert.False(deck.ShowOutline);
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
