using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// <see cref="HtmlToMarkdownConverter"/> の検証。対象は Mammoth が実機で出す意味的 HTML
/// （自己終了タグの整形済み XHTML 断片。<see cref="OfficeEditorSupportTests"/> で確認済みの形）で、
/// 任意の HTML 全般の網羅ではない。
/// </summary>
public class HtmlToMarkdownConverterTests
{
    [Fact]
    public void 見出しと段落を変換する()
    {
        var md = HtmlToMarkdownConverter.Convert("<h1>見出し1</h1><p>本文です。</p>");
        Assert.Equal("# 見出し1\n\n本文です。\n", md);
    }

    [Fact]
    public void 強調と斜体とリンクを変換する()
    {
        var md = HtmlToMarkdownConverter.Convert(
            "<p><strong>太字</strong> と <em>斜体</em> と <a href=\"https://example.com\">リンク</a></p>");
        Assert.Equal("**太字** と *斜体* と [リンク](https://example.com)\n", md);
    }

    [Fact]
    public void 箇条書きを変換する()
    {
        var md = HtmlToMarkdownConverter.Convert("<ul><li>項目1</li><li>項目2</li></ul>");
        Assert.Equal("- 項目1\n- 項目2\n", md);
    }

    [Fact]
    public void 表をパイプ区切りへ変換する_セルの段落は展開する()
    {
        var html = "<table><tr><td><p>A1</p></td><td><p>B1</p></td></tr>"
                  + "<tr><td><p>A2</p></td><td><p>B2</p></td></tr></table>";
        var md = HtmlToMarkdownConverter.Convert(html);
        Assert.Equal("| A1 | B1 |\n| --- | --- |\n| A2 | B2 |\n", md);
    }

    [Fact]
    public void 画像をdataURIのまま埋め込む()
    {
        var md = HtmlToMarkdownConverter.Convert("<p><img src=\"data:image/png;base64,AAA=\" /></p>");
        Assert.Equal("![](data:image/png;base64,AAA=)\n", md);
    }

    [Fact]
    public void colspanのセルは空セルで埋めて列数を揃える()
    {
        var html = "<table><tr><td colspan=\"3\"><p>タイトル</p></td></tr>"
                  + "<tr><td><p>A</p></td><td><p>B</p></td><td><p>C</p></td></tr></table>";
        var md = HtmlToMarkdownConverter.Convert(html);
        Assert.Equal("| タイトル |  |  |\n| --- | --- | --- |\n| A | B | C |\n", md);
    }

    [Fact]
    public void ネストした表の行は外側の表と混ざらない()
    {
        var html = "<table><tr><td><table><tr><td><p>内側1</p></td></tr>"
                  + "<tr><td><p>内側2</p></td></tr></table></td><td><p>外側の隣のセル</p></td></tr></table>";
        var md = HtmlToMarkdownConverter.Convert(html);
        // 外側の表は1行だけ（ネストした表の行が混ざって増えない）。
        Assert.Equal(1, md.Split('\n').Count(l => l.StartsWith("| ") && !l.Contains("---")));
    }

    [Fact]
    public void EMF画像は描画できないため死んだbase64を埋め込まずプレースホルダにする()
    {
        var md = HtmlToMarkdownConverter.Convert("<p><img alt=\"fig1\" src=\"data:image/x-emf;base64,AAA=\" /></p>");
        Assert.Equal("[画像: fig1]\n", md);
        Assert.DoesNotContain("base64", md);
    }

    [Fact]
    public void 段落先頭のMarkdown記号は誤認防止のためエスケープする()
    {
        var md = HtmlToMarkdownConverter.Convert("<p>#タグではない見出し風テキスト</p>");
        Assert.StartsWith("\\#タグ", md);
    }

    [Fact]
    public void 壊れたHTMLはタグを剥がしたテキストへフォールバックする()
    {
        var md = HtmlToMarkdownConverter.Convert("<p>閉じ忘れ<br>テキスト");
        Assert.Contains("閉じ忘れ", md);
        Assert.Contains("テキスト", md);
        Assert.DoesNotContain("<", md);
    }
}
