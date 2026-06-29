using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Markdown プレビューの HTML 生成（<see cref="MarkdownRenderer"/>）の検証。
/// 番号付きリストの開始番号保持と、文末スペース2つによるハード改行（&lt;br&gt;）を中心に確認する。
/// </summary>
public class MarkdownRendererTests
{
    private static string Render(string markdown) => MarkdownRenderer.RenderToHtml(markdown);

    [Fact]
    public void OrderedList_StartingAtOne_EmitsPlainOl()
    {
        var html = Render("1. one\n2. two\n3. three");

        Assert.Contains("<ol>", html);
        Assert.DoesNotContain("<ol start", html);
        Assert.Contains("<li>one</li>", html);
        Assert.Contains("<li>two</li>", html);
        Assert.Contains("<li>three</li>", html);
    }

    [Fact]
    public void OrderedList_NotStartingAtOne_PreservesStartNumber()
    {
        var html = Render("5. five\n6. six");

        Assert.Contains("<ol start=\"5\">", html);
        Assert.Contains("<li>five</li>", html);
        Assert.Contains("<li>six</li>", html);
    }

    [Fact]
    public void OrderedList_LooseWithBlankLines_StaysSingleListAndKeepsNumbering()
    {
        // 空行を挟んだルーズリストは単一の <ol> にまとまり、ブラウザが 1. 2. と採番する
        // （別々の <ol> に割れて番号が 1 に戻ることはない）。
        var html = Render("1. first\n\n2. second");

        Assert.Equal(1, Count(html, "<ol"));
        Assert.Contains("<li>first</li>", html);
        Assert.Contains("<li>second</li>", html);
    }

    [Fact]
    public void NestedUnorderedList_IndentBecomesChildList()
    {
        var html = Render("- parent\n  - child\n- parent2");

        // 子リストは親 <li> を閉じる前に入る。
        Assert.Contains("<li>parent<ul>", Collapse(html));
        Assert.Contains("<li>child</li>", html);
        Assert.Contains("<li>parent2</li>", html);
        // ネストした子 <ul> は親 <ul> の中で閉じる（トップレベル <ul> は1つだけ開く）。
        Assert.Equal(2, Count(html, "<ul>"));
        Assert.Equal(2, Count(html, "</ul>"));
    }

    [Fact]
    public void NestedOrderedUnderOrdered_IsIndentedNotFlattened()
    {
        // インデントされた番号付き項目が段落に転落せず、入れ子の <ol> になる。
        var html = Render("1. parent\n   1. child\n2. parent2");

        Assert.Contains("<li>parent<ol>", Collapse(html));
        Assert.Contains("<li>child</li>", html);
        Assert.Contains("<li>parent2</li>", html);
        Assert.DoesNotContain("<p>", html);
    }

    [Fact]
    public void IndentedOrderedList_DoesNotFallBackToParagraph()
    {
        var html = Render("  1. only");

        Assert.Contains("<ol>", html);
        Assert.Contains("<li>only</li>", html);
        Assert.DoesNotContain("<p>", html);
    }

    private static string Collapse(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, @">\s+<", "><");

    private static int Count(string haystack, string needle)
    {
        int n = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) >= 0) { n++; idx += needle.Length; }
        return n;
    }

    [Fact]
    public void Paragraph_TrailingTwoSpaces_ProducesHardBreak()
    {
        var html = Render("line one  \nline two");

        Assert.Contains("line one<br>line two", html);
    }

    [Fact]
    public void Paragraph_NoTrailingSpaces_JoinsWithSpace()
    {
        var html = Render("line one\nline two");

        Assert.Contains("line one line two", html);
        Assert.DoesNotContain("<br>", html);
    }

    [Fact]
    public void Paragraph_SingleTrailingSpace_DoesNotHardBreak()
    {
        // ハード改行は「スペース2つ以上」が条件。1 つだけのときは通常の連結。
        var html = Render("line one \nline two");

        Assert.DoesNotContain("<br>", html);
        Assert.Contains("line one line two", html);
    }

    [Fact]
    public void Frontmatter_IsStrippedFromDocumentBody()
    {
        // 先頭の YAML フロントマターは本文として描かない（表や hr に化けない）。
        var html = Render("---\ntitle: hi\nmarp: true\n---\n\n# Heading");

        Assert.Contains("<h1", html);
        Assert.Contains("Heading", html);
        Assert.DoesNotContain("title: hi", html);
        Assert.DoesNotContain("<table", html);
    }

    [Fact]
    public void Link_WithUnderscoresInTextAndHref_NotParsedAsEmphasis()
    {
        // [aa_01_cc.md](aa_01_cc.md) の _01_ が斜体（<em>）に化けず、リンクのまま出る。
        var html = Render("[aa_01_cc.md](aa_01_cc.md)");

        Assert.Contains("<a href=\"aa_01_cc.md\">aa_01_cc.md</a>", html);
        Assert.DoesNotContain("<em>", html);
    }

    [Fact]
    public void Image_WithUnderscoresInAltAndSrc_NotParsedAsEmphasis()
    {
        var html = Render("![my_pic_01](img_01_x.png)");

        Assert.Contains("<img src=\"img_01_x.png\" alt=\"my_pic_01\">", html);
        Assert.DoesNotContain("<em>", html);
    }

    [Fact]
    public void Link_WithAsterisksInText_NotParsedAsEmphasis()
    {
        // * も同様にリンク内では強調にしない。
        var html = Render("[a*b*c](a*b*c.md)");

        Assert.Contains("<a href=\"a*b*c.md\">a*b*c</a>", html);
        Assert.DoesNotContain("<em>", html);
    }

    [Fact]
    public void Emphasis_OutsideLink_StillWorks()
    {
        // リンク退避が通常の強調処理を壊していないこと。
        var html = Render("text _italic_ and [aa_01](aa_01.md)");

        Assert.Contains("<em>italic</em>", html);
        Assert.Contains("<a href=\"aa_01.md\">aa_01</a>", html);
    }

    [Fact]
    public void IsMarpDocument_DetectsFrontmatterFlag()
    {
        Assert.True(MarkdownRenderer.IsMarpDocument("---\nmarp: true\n---\n# x"));
        Assert.False(MarkdownRenderer.IsMarpDocument("---\nmarp: false\n---\n# x"));
        Assert.False(MarkdownRenderer.IsMarpDocument("# no frontmatter"));
        // marp ディレクティブが本文中にあるだけ（フロントマター外）は marp 扱いにしない。
        Assert.False(MarkdownRenderer.IsMarpDocument("# x\n\nmarp: true"));
    }

}
