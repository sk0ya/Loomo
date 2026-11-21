using Editor.Core.Syntax;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

public sealed class EditorTodoHighlighterTests
{
    private static IReadOnlyDictionary<int, IReadOnlyList<LineHighlightSpan>> Highlight(string file, params string[] lines)
    {
        var engine = new SyntaxEngine();
        engine.DetectLanguage(file);
        var settings = new TodoTreeSettings();
        return new EditorTodoHighlighter(() => settings)
            .Highlight(file, lines, engine.Tokenize(lines), 0, lines.Length - 1);
    }

    [Fact]
    public void コードはコメント先頭のタグと担当者とコロンだけを塗る()
    {
        var result = Highlight("a.cs", "var s = \"// TODO: 文字列\";", "int x; // FIXME(koya): 直す", "// 文中の TODO: は塗らない");
        var span = Assert.Single(Assert.Single(result).Value);
        Assert.Equal(1, result.Keys.Single());
        Assert.Equal("FIXME(koya):", "int x; // FIXME(koya): 直す".Substring(span.Start, span.Length));
    }

    [Fact]
    public void 文書はコードフェンスの中を塗らない()
    {
        var result = Highlight("a.md", "- TODO: 書く", "```", "// TODO: 見本", "```", "NOTE: 残す");
        Assert.Equal([0, 4], result.Keys.OrderBy(k => k));
    }

    [Fact]
    public void 対象外の拡張子は塗らない()
        => Assert.Empty(Highlight("a.txt", "TODO: 書く"));
}
