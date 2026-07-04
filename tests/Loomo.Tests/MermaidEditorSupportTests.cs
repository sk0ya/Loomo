using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Mermaid 単体ファイル（.mmd / .mermaid）のプレビュー提供者（MermaidEditorSupport）と、
/// EditorSupportRegistry での拡張子解決の検証。
/// </summary>
public class MermaidEditorSupportTests
{
    private static EditorSupportRegistry CreateRegistry()
    {
        var workspace = new FakeWorkspaceService();
        return new(new IEditorSupportProvider[]
        {
            new MarkdownEditorSupport(new AiSettings(), workspace),
            new MermaidEditorSupport(new AiSettings())
        });
    }

    [Theory]
    [InlineData(@"C:\work\flow.mmd")]
    [InlineData(@"C:\work\diagram.mermaid")]
    [InlineData(@"C:\work\UPPER.MMD")]
    public void Resolve_MermaidファイルにはMermaidプロバイダを返す(string path)
    {
        var provider = CreateRegistry().Resolve(path);

        Assert.IsType<MermaidEditorSupport>(provider);
    }

    [Fact]
    public void DescribeTitle_Mermaidプレフィックスとファイル名()
    {
        var support = new MermaidEditorSupport(new AiSettings());

        Assert.Equal("Mermaid: flow.mmd", support.DescribeTitle(@"C:\work\flow.mmd"));
    }

    [Fact]
    public void RenderHtml_図ブロックとブートストラップを含む完全なHTMLを生成する()
    {
        var support = new MermaidEditorSupport(new AiSettings());
        const string path = @"C:\work\flow.mmd";

        Assert.IsAssignableFrom<IEditorSupportIncrementalHtmlProvider>(support);

        var html = support.RenderHtml(path, "graph TD\n  A-->B");

        Assert.Contains("<pre class=\"mermaid\">", html);
        Assert.Contains("A--&gt;B", html);                            // textContent として読まれるので HTML エンコード
        Assert.Contains("Mermaid: flow.mmd", html);                  // <title> へ反映される
        // ページには mermaid ブートストラップが常駐し、.mermaid 要素があるときだけ遅延ロードする。
        Assert.Contains("https://assets.loomo/mermaid.min.js", html); // 同梱スクリプト（オフライン可）
        Assert.Contains("mermaid.initialize", html);
    }

    [Fact]
    public void RenderBody_生テキストを図ブロックに包みHTMLエスケープする()
    {
        var support = new MermaidEditorSupport(new AiSettings());

        var body = support.RenderBody(@"C:\work\flow.mmd", "graph LR\n  A-->B");

        Assert.Contains("<pre class=\"mermaid\">", body);
        Assert.Contains("A--&gt;B", body);
        Assert.DoesNotContain("A-->B", body);   // 生の --> は残らない
    }

    [Fact]
    public void RenderBody_空テキストは空の図ブロックにする()
    {
        var support = new MermaidEditorSupport(new AiSettings());

        var body = support.RenderBody(@"C:\work\flow.mmd", "");

        Assert.Equal("<pre class=\"mermaid\"></pre>", body);
    }
}
