using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// EditorSupport ペインの拡張子→提供者解決（EditorSupportRegistry / MarkdownEditorSupport）の検証。
/// ペインの自動開閉そのもの（ShellWindow）は UI 依存のためここでは扱わない。
/// </summary>
public class EditorSupportTests
{
    private static EditorSupportRegistry CreateRegistry()
        => new(new IEditorSupportProvider[] { new MarkdownEditorSupport(new AiSettings()) });

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
        var support = new MarkdownEditorSupport(new AiSettings());
        const string path = @"C:\work\README.md";

        Assert.Equal("Preview: README.md", support.DescribeTitle(path));

        var html = support.RenderHtml(path, "# 見出し\n\n本文です。");
        Assert.Contains("<h1", html);
        Assert.Contains("本文です。", html);
        Assert.Contains("Preview: README.md", html);   // <title> へ反映される
    }
}
