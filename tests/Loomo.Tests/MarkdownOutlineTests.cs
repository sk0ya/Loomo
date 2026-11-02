using System.Linq;
using Editor.Core.Lsp;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>Editor ペインのアウトライン（<see cref="EditorOutline"/>）の中身の組み立て。</summary>
public class MarkdownOutlineTests
{
    private static IReadOnlyList<EditorOutlineEntry> Md(string text)
        => EditorOutline.FromMarkdown(CodeEditorSupportAnalysis.SplitLines(text));

    [Fact]
    public void Lists_atx_headings_with_depth_relative_to_the_shallowest()
    {
        var entries = Md("## A\ntext\n### A1\n## B");

        Assert.Equal(new[] { ("A", 0, 0), ("A1", 1, 2), ("B", 0, 3) },
            entries.Select(e => (e.Text, e.Depth, e.Line0)));
        Assert.Equal(3, entries[0].Column0);   // 「## 」の後ろの文字の頭へ飛ぶ
    }

    [Fact]
    public void Ignores_code_fences_and_front_matter()
    {
        var entries = Md("---\ntitle: x\n---\n# Real\n```sh\n# not a heading\n```\n~~~\n## nope\n~~~\n## Sub");

        Assert.Equal(new[] { "Real", "Sub" }, entries.Select(e => e.Text));
    }

    [Fact]
    public void Reads_setext_headings_but_not_horizontal_rules()
    {
        var entries = Md("Title\n=====\n\n---\n- item\n---\nSection\n-------");

        Assert.Equal(new[] { ("Title", 0, 0), ("Section", 1, 6) },
            entries.Select(e => (e.Text, e.Depth, e.Line0)));
    }

    [Fact]
    public void Cleans_inline_markup_and_closing_hashes()
        => Assert.Equal("Bold link code",
            Assert.Single(Md("## **Bold** [link](http://x) `code` ##\n#NoSpace\n#")).Text);

    [Fact]
    public void Flattens_symbols_in_declaration_order_and_jumps_to_the_name()
    {
        var method = new DocumentSymbol("Run", SymbolKind.Method,
            new LspRange(new LspPosition(4, 4), new LspPosition(9, 5)),
            new LspRange(new LspPosition(5, 16), new LspPosition(5, 19)),   // 名前の位置
            System.Array.Empty<DocumentSymbol>());
        var type = new DocumentSymbol("Foo", SymbolKind.Class,
            new LspRange(new LspPosition(2, 0), new LspPosition(10, 1)),
            new LspRange(new LspPosition(2, 13), new LspPosition(2, 16)),
            new[] { method });

        Assert.Equal(new[] { ("Foo", 0, 2, 13), ("Run", 1, 5, 16) },
            EditorOutline.FromSymbols(new[] { type }).Select(e => (e.Text, e.Depth, e.Line0, e.Column0)));
    }

    [Theory]
    [InlineData(@"C:\a\readme.md", true)]
    [InlineData(@"C:\a\README.MARKDOWN", true)]
    [InlineData(@"C:\a\x.cs", false)]
    [InlineData(null, false)]
    public void Detects_markdown_by_extension(string? path, bool expected)
        => Assert.Equal(expected, EditorOutline.IsMarkdown(path));
}
