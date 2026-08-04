using sk0ya.Loomo.Core.Markdown;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// インラインリンクの宛先解析（<see cref="MarkdownLinkParser"/>）。
/// 「最初の <c>)</c> まで」で切ると壊れる形（釣り合った括弧・タイトル・入れ子の <c>[]</c>）を固定する。
/// </summary>
public class MarkdownLinkParserTests
{
    private static MarkdownInlineLink One(string text)
    {
        var links = MarkdownLinkParser.FindAll(text);
        return Assert.Single(links);
    }

    [Theory]
    [InlineData("[aa](aa(bb)_cc.md)", "aa(bb)_cc.md")]
    [InlineData("[aa](a(b(c))d.md)", "a(b(c))d.md")]
    [InlineData("[aa](plain.md)", "plain.md")]
    [InlineData(@"[aa](a\(b.md)", "a(b.md")]
    [InlineData("[aa](<a (b .md>)", "a (b .md")]
    [InlineData("[aa](dir/f.md \"title\")", "dir/f.md")]
    [InlineData("[aa](a(b)_c.md 'title')", "a(b)_c.md")]
    public void 釣り合った括弧やタイトルを含む宛先を最後まで読む(string markdown, string expected)
    {
        var link = One(markdown);

        Assert.Equal(expected, link.Destination);
        Assert.Equal(0, link.Start);
        Assert.Equal(markdown.Length, link.Length);
    }

    [Fact]
    public void リンク全体の長さが閉じ括弧まで届く()
    {
        const string src = "前 [aa](aa(bb)_cc.md) 後";

        var link = One(src);

        Assert.Equal("aa", link.Text);
        Assert.Equal("[aa](aa(bb)_cc.md)", src.Substring(link.Start, link.Length));
    }

    [Fact]
    public void 画像は先頭の感嘆符を含めて1件として返す()
    {
        var link = One("![alt](a(b).png)");

        Assert.True(link.IsImage);
        Assert.Equal(0, link.Start);
        Assert.Equal("alt", link.Text);
        Assert.Equal("a(b).png", link.Destination);
    }

    [Fact]
    public void リンクテキストの入れ子の角括弧を許す()
    {
        var link = One("[see [1] here](a(b).md)");

        Assert.Equal("see [1] here", link.Text);
        Assert.Equal("a(b).md", link.Destination);
    }

    [Fact]
    public void 釣り合わない括弧は宛先として認めない()
    {
        Assert.Empty(MarkdownLinkParser.FindAll("[aa](a(b.md"));
        Assert.Empty(MarkdownLinkParser.FindAll("[aa](a(b.md 'title'"));
    }

    [Fact]
    public void 複数のリンクを順に返す()
    {
        var links = MarkdownLinkParser.FindAll("[a](x(1).md) と [b](y(2).md)");

        Assert.Equal(2, links.Count);
        Assert.Equal("x(1).md", links[0].Destination);
        Assert.Equal("y(2).md", links[1].Destination);
    }

    [Fact]
    public void 脚注参照はリンクとして拾わない()
    {
        Assert.Empty(MarkdownLinkParser.FindAll("本文[^1]と続き"));
    }

    [Theory]
    [InlineData("a(b)_c.md", "a(b)_c.md")]      // 釣り合っていればそのまま
    [InlineData("plain.md", "plain.md")]
    [InlineData("a (b).md", "<a (b).md>")]      // 空白があれば <> で囲む
    [InlineData("a(b.md", "<a(b.md>")]          // 釣り合わない括弧も <> で囲む
    [InlineData("a<b>.md", @"<a\<b\>.md>")]
    public void 生成側は安全な宛先表記へ整形する(string raw, string expected)
    {
        Assert.Equal(expected, MarkdownLinkParser.EncodeDestination(raw));
    }

    [Fact]
    public void 整形した宛先は読み直して元へ戻る()
    {
        foreach (var raw in new[] { "a(b)_c.md", "a (b).md", "a(b.md", "a<b>.md", @"a\b.md" })
        {
            var markdown = $"[t]({MarkdownLinkParser.EncodeDestination(raw)})";

            Assert.Equal(raw, One(markdown).Destination);
        }
    }
}
