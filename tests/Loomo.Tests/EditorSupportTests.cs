using System.IO;
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
        => new(new IEditorSupportProvider[] { CreateSupport() });

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
    [InlineData(@"C:\work\Program.cs")]
    [InlineData(@"C:\work\拡張子なし")]
    [InlineData("")]
    [InlineData(null)]
    public void Resolve_未対応や無効なパスにはnullを返す(string? path)
    {
        Assert.Null(CreateRegistry().Resolve(path));
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
    public void MarkdownSupport_mermaidフェンスは図用ブロックとスクリプトを出力する()
    {
        var support = CreateSupport();

        var html = support.RenderHtml(@"C:\work\README.md", "```mermaid\ngraph TD\n  A-->B\n```");

        Assert.Contains("<pre class=\"mermaid\">", html);
        Assert.Contains("A--&gt;B", html);          // textContent として読まれるので HTML エンコードでよい
        Assert.Contains("https://assets.loomo/mermaid.min.js", html); // 同梱スクリプト（オフライン可）
        Assert.Contains("mermaid?.initialize", html);
        Assert.DoesNotContain("language-mermaid", html); // 通常のコードブロックにはしない
    }

    [Fact]
    public void MarkdownSupport_mermaidが無ければスクリプトを埋め込まない()
    {
        var support = CreateSupport();

        var html = support.RenderHtml(@"C:\work\README.md", "```csharp\nvar x = 1;\n```");

        Assert.DoesNotContain("mermaid.min.js", html);
        Assert.Contains("language-csharp", html);
    }
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
