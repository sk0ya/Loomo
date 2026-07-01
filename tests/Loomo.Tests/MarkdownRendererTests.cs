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
    public void BareUrl_InParagraph_IsAutolinked()
    {
        var html = Render("see https://example.com/path for details");

        Assert.Contains("<a href=\"https://example.com/path\">https://example.com/path</a>", html);
    }

    [Fact]
    public void BareUrl_TrailingPunctuation_IsExcludedFromLink()
    {
        var html = Render("visit https://example.com/a, then https://example.com/b.");

        Assert.Contains("<a href=\"https://example.com/a\">https://example.com/a</a>,", html);
        Assert.Contains("<a href=\"https://example.com/b\">https://example.com/b</a>.", html);
    }

    [Fact]
    public void BareUrl_WithBalancedParens_KeepsParensInLink()
    {
        var html = Render("see https://en.wikipedia.org/wiki/Foo_(bar) here");

        Assert.Contains("<a href=\"https://en.wikipedia.org/wiki/Foo_(bar)\">https://en.wikipedia.org/wiki/Foo_(bar)</a>", html);
    }

    [Fact]
    public void BareUrl_InsideCodeSpan_IsNotAutolinked()
    {
        var body = MarkdownRenderer.RenderToBody("`https://example.com`");

        Assert.DoesNotContain("<a href", body);
        Assert.Contains("<code>https://example.com</code>", body);
    }

    [Fact]
    public void MarkdownLink_IsNotDoubleAutolinked()
    {
        var body = MarkdownRenderer.RenderToBody("[text](https://example.com)");

        Assert.Equal(1, Count(body, "<a href"));
        Assert.Contains("<a href=\"https://example.com\">text</a>", body);
    }

    [Fact]
    public void LinkText_ContainingCodeSpan_RendersWithoutStrayControlChars()
    {
        // コードスパンはリンクより先にスタッシュされるため、番兵の入れ子が正しく復元されないと
        // 生の \x01 がそのまま残ってしまう（[`docs/設計書.md`](docs/設計書.md) のような形）。
        var body = MarkdownRenderer.RenderToBody("[`docs/design.md`](docs/design.md)");

        Assert.Equal($"<p><a href=\"docs/design.md\"><code>docs/design.md</code></a></p>{Environment.NewLine}", body);
        Assert.DoesNotContain('\x01', body);
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

    [Fact]
    public void TaskList_UncheckedAndChecked_RenderAsClickableCheckboxesWithSourceLine()
    {
        var html = Render("- [ ] todo\n- [x] done\n- [X] also done\n- not a task");

        // disabled は付けない：プレビュー上でクリックしてチェック状態を切り替えられる。
        Assert.Contains("<li class=\"task-list-item\"><input type=\"checkbox\" data-line=\"0\">todo</li>", Collapse(html));
        Assert.Contains("<li class=\"task-list-item\"><input type=\"checkbox\" data-line=\"1\" checked>done</li>", Collapse(html));
        Assert.Contains("<li class=\"task-list-item\"><input type=\"checkbox\" data-line=\"2\" checked>also done</li>", Collapse(html));
        Assert.Contains("<li>not a task</li>", html);
    }

    [Fact]
    public void TaskList_DataLine_AccountsForFrontmatterOffset()
    {
        // フロントマター分だけソースの行番号がずれるので、data-line はそのオフセットを反映する必要がある
        // （クリック時にホストが正しいソース行を書き換えられるように）。
        var html = Render("---\ntitle: hi\n---\n\n- [ ] todo");

        Assert.Contains("data-line=\"4\"", html);
    }

    [Theory]
    [InlineData("- [ ] todo", "- [x] todo")]
    [InlineData("- [x] done", "- [ ] done")]
    [InlineData("- [X] done", "- [ ] done")]
    [InlineData("  - [ ] nested", "  - [x] nested")]
    [InlineData("1. [ ] numbered", "1. [x] numbered")]
    public void ToggleTaskListLine_FlipsCheckState(string before, string after)
    {
        Assert.Equal(after, MarkdownRenderer.ToggleTaskListLine(before));
    }

    [Fact]
    public void ToggleTaskListLine_NonTaskLine_ReturnsNull()
    {
        Assert.Null(MarkdownRenderer.ToggleTaskListLine("- not a task"));
        Assert.Null(MarkdownRenderer.ToggleTaskListLine("plain paragraph"));
    }

    [Fact]
    public void Table_ColumnAlignment_AppliesTextAlignStyle()
    {
        var html = Render("|L|C|R|D|\n|:--|:--:|--:|--|\n|a|b|c|d|");

        Assert.Contains("<th style=\"text-align:left\">L</th>", html);
        Assert.Contains("<th style=\"text-align:center\">C</th>", html);
        Assert.Contains("<th style=\"text-align:right\">R</th>", html);
        Assert.Contains("<th>D</th>", html);
        Assert.Contains("<td style=\"text-align:left\">a</td>", html);
        Assert.Contains("<td style=\"text-align:center\">b</td>", html);
        Assert.Contains("<td style=\"text-align:right\">c</td>", html);
        Assert.Contains("<td>d</td>", html);
    }

    [Fact]
    public void CodeFence_KnownLanguage_EmitsSyntaxHighlightSpans()
    {
        var body = MarkdownRenderer.RenderToBody("```csharp\nclass Foo { }\n```");

        Assert.Contains("<span class=\"tok-kw\">class</span>", body);
        Assert.Contains("class=\"language-csharp\"", body);
    }

    [Fact]
    public void CodeFence_UnknownLanguage_FallsBackToPlainEncodedText()
    {
        var body = MarkdownRenderer.RenderToBody("```notalanguage\nclass Foo { }\n```");

        Assert.DoesNotContain("<span class=\"tok-", body);
        Assert.Contains("class Foo { }", body);
    }

    [Fact]
    public void CodeFence_Mermaid_IsNotSyntaxHighlighted()
    {
        var body = MarkdownRenderer.RenderToBody("```mermaid\ngraph TD;\nA-->B;\n```");

        Assert.Contains("<pre class=\"mermaid\">", body);
        Assert.DoesNotContain("<span class=\"tok-", body);
    }

    [Fact]
    public void Heading_GetsAnchorLinkForPermalink()
    {
        var html = Render("## My Heading");

        Assert.Contains("<h2 id=\"my-heading\">", html);
        Assert.Contains("<a class=\"heading-anchor\" href=\"#my-heading\"", html);
        Assert.Contains("My Heading</h2>", html);
    }

    [Fact]
    public void CodeFence_WrapsWithCopyButton()
    {
        var body = MarkdownRenderer.RenderToBody("```csharp\nclass Foo { }\n```");

        Assert.Contains("<div class=\"code-block\">", body);
        Assert.Contains("class=\"code-copy-btn\"", body);
    }

    [Fact]
    public void CodeFence_Mermaid_IsNotWrappedWithCopyButton()
    {
        var body = MarkdownRenderer.RenderToBody("```mermaid\ngraph TD;\nA-->B;\n```");

        Assert.DoesNotContain("code-copy-btn", body);
    }

    [Fact]
    public void CodeFence_Diff_HighlightsAddedAndRemovedLines()
    {
        var body = MarkdownRenderer.RenderToBody("```diff\n+++ b/file\n--- a/file\n@@ -1 +1 @@\n+added\n-removed\n unchanged\n```");

        Assert.Contains("<span class=\"diff-meta\">+++ b/file</span>", body);
        Assert.Contains("<span class=\"diff-meta\">--- a/file</span>", body);
        Assert.Contains("<span class=\"diff-hunk\">@@ -1 +1 @@</span>", body);
        Assert.Contains("<span class=\"diff-add\">+added</span>", body);
        Assert.Contains("<span class=\"diff-del\">-removed</span>", body);
        Assert.Contains(" unchanged", body);
    }

    [Fact]
    public void Footnote_ReferenceAndDefinition_RenderWithBackref()
    {
        var body = MarkdownRenderer.RenderToBody("Some text.[^note]\n\n[^note]: The footnote body.");

        Assert.Contains("<sup id=\"fnref-1\"><a href=\"#fn-1\" class=\"footnote-ref\">1</a></sup>", body);
        Assert.Contains("<section class=\"footnotes\">", body);
        Assert.Contains("<li id=\"fn-1\">The footnote body.", body);
        Assert.Contains("<a href=\"#fnref-1\" class=\"footnote-backref\"", body);
    }

    [Fact]
    public void Footnote_UndefinedReference_IsLeftAsPlainText()
    {
        var body = MarkdownRenderer.RenderToBody("Some text.[^missing]");

        Assert.Contains("[^missing]", body);
        Assert.DoesNotContain("footnote-ref", body);
        Assert.DoesNotContain("class=\"footnotes\"", body);
    }

    [Fact]
    public void Toc_Marker_GeneratesNavFromHeadings()
    {
        var body = MarkdownRenderer.RenderToBody("[[toc]]\n\n# Intro\n\n## Details");

        Assert.Contains("<nav class=\"toc\">", body);
        Assert.Contains("<a href=\"#intro\">Intro</a>", body);
        Assert.Contains("<a href=\"#details\">Details</a>", body);
    }

    [Fact]
    public void Toc_Marker_IgnoresHeadingsInsideCodeFences()
    {
        var body = MarkdownRenderer.RenderToBody("[[toc]]\n\n```\n# Not a heading\n```\n\n# Real Heading");

        Assert.DoesNotContain("Not a heading</a>", body);
        Assert.Contains("<a href=\"#real-heading\">Real Heading</a>", body);
    }

}
