using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// XML/XAML の EditorSupport（<see cref="XmlEditorSupport"/> / <see cref="XmlTreeRenderer"/>）の検証。
/// JSON プレビューと同じページシェル（#json-root）・折りたたみ CSS クラスを共有するので、
/// 解決（拡張子→提供者）・タグ／属性／テキストの描画・エスケープ・壊れた入力の握り・タイトルを確認する。
/// </summary>
public class XmlEditorSupportTests
{
    private static EditorSupportRegistry CreateRegistry()
    {
        var settings = new AiSettings();
        var workspace = new FakeWorkspaceService();
        return new(new IEditorSupportProvider[]
        {
            new MarkdownEditorSupport(settings, workspace),
            new JsonEditorSupport(settings, new JsonSchemaValidator()),
            new YamlEditorSupport(settings),
            new TomlEditorSupport(settings),
            new XmlEditorSupport(settings),
        });
    }

    [Theory]
    [InlineData(@"C:\work\config.xml")]
    [InlineData(@"C:\work\App.xaml")]
    [InlineData(@"C:\work\UPPER.XML")]
    [InlineData(@"C:\work\Window.XAML")]
    public void Resolve_XmlとXamlにはXmlプロバイダを返す(string path)
    {
        var provider = CreateRegistry().Resolve(path);

        Assert.IsType<XmlEditorSupport>(provider);
    }

    [Fact]
    public void RenderHtml_ページシェルとタイトルを含む()
    {
        var support = new XmlEditorSupport(new AiSettings());

        var html = support.RenderHtml(@"C:\work\config.xml", "<root><item>x</item></root>");

        Assert.Contains("id=\"json-root\"", html);   // ページシェル
        Assert.Contains("XML: config.xml", html);    // <title>
    }

    [Fact]
    public void RenderBody_タグ属性テキストを折りたたみツリーにする()
    {
        var support = new XmlEditorSupport(new AiSettings());

        var body = support.RenderBody(@"C:\work\config.xml", "<root><item id=\"1\">x</item></root>");

        Assert.Contains("class=\"node\"", body);  // 折りたたみ用の親ノード
        Assert.Contains("root", body);            // タグ名
        Assert.Contains("item", body);            // 子タグ名
        Assert.Contains("id", body);              // 属性名
        Assert.Contains("\"1\"", body);           // 属性値
        Assert.Contains(">x<", body);             // テキスト（<span class="s">x</span>）
    }

    [Fact]
    public void RenderBody_子の無い要素は自己終了タグの1行になる()
    {
        var support = new XmlEditorSupport(new AiSettings());

        var body = support.RenderBody(@"C:\work\config.xml", "<root><item id=\"1\"/></root>");

        Assert.Contains("/&gt;", body);   // <item id="1"/>
    }

    [Fact]
    public void RenderBody_HTML特殊文字をエスケープする()
    {
        var support = new XmlEditorSupport(new AiSettings());

        // テキスト内容として文字列 "<b>&</b>" を持つ（XML ソース上はエスケープして書く）。
        var body = support.RenderBody(@"C:\work\config.xml", "<root>&lt;b&gt;&amp;&lt;/b&gt;</root>");

        // 描画時に再エスケープされ、生のタグとして出ない。
        Assert.Contains("&lt;b&gt;&amp;", body);
        Assert.DoesNotContain("<b>&</b>", body);
    }

    [Fact]
    public void RenderBody_コメントを表示する()
    {
        var support = new XmlEditorSupport(new AiSettings());

        var body = support.RenderBody(@"C:\work\config.xml", "<root><!-- メモ --></root>");

        Assert.Contains("&lt;!--", body);
        Assert.Contains("メモ", body);
    }

    [Fact]
    public void RenderBody_壊れたXMLはエラー本文を出し例外を投げない()
    {
        var support = new XmlEditorSupport(new AiSettings());

        var body = support.RenderBody(@"C:\work\bad.xml", "<a><b>");

        Assert.Contains("解析できません", body);
        Assert.Contains("class=\"raw\"", body);   // 原文を併記
        Assert.DoesNotContain("class=\"node\"", body);
    }

    [Fact]
    public void RenderBody_空テキストは空表示になる()
    {
        var support = new XmlEditorSupport(new AiSettings());

        Assert.Contains("空のファイル", support.RenderBody(@"C:\work\config.xml", ""));
        Assert.Contains("空のファイル", support.RenderBody(@"C:\work\config.xml", "   "));
    }

    [Fact]
    public void DescribeTitle_XMLプレフィックスとファイル名()
    {
        var support = new XmlEditorSupport(new AiSettings());

        Assert.Equal("XML: config.xml", support.DescribeTitle(@"C:\work\config.xml"));
        Assert.Equal("XML: App.xaml", support.DescribeTitle(@"C:\work\App.xaml"));
        Assert.IsAssignableFrom<IEditorSupportIncrementalHtmlProvider>(support);
    }
}
