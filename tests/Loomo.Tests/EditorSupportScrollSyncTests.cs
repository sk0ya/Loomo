using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// エディタとプレビューをソース行で合わせるスクロール同期の検証。比率で合わせていた頃は、
/// 画像やコードブロックのように描画の高さがソース行数と比例しない箇所でずれていった。
/// </summary>
public class EditorSupportScrollSyncTests
{
    [Fact]
    public void RenderToBody_SourceLines_StampsEachTopLevelBlockWithItsStartLine()
    {
        var body = MarkdownRenderer.RenderToBody(
            "# Title\n\npara one\npara two\n\n```cs\nx\n```\n\n- a\n- b\n\n> quote", sourceLines: true);

        Assert.Contains("<h1 data-source-line=\"0\"", body);
        Assert.Contains("<p data-source-line=\"2\">", body);
        Assert.Contains("<div data-source-line=\"5\" class=\"code-block\">", body);
        Assert.Contains("<ul data-source-line=\"9\">", body);
        Assert.Contains("<blockquote data-source-line=\"12\" data-source-end=\"13\">", body);   // 最後のブロックは文書の終わりも持つ
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, "data-source-end"));
    }

    [Fact]
    public void RenderToBody_SourceLines_CountsFrontmatterLines()
    {
        var body = MarkdownRenderer.RenderToBody("---\ntitle: x\n---\n# H", sourceLines: true);

        Assert.Contains("<h1 data-source-line=\"3\"", body);
    }

    [Fact]
    public void RenderToBody_WithoutSourceLines_EmitsNoMarkers()
    {
        Assert.DoesNotContain("data-source-line", MarkdownRenderer.RenderToBody("# H\n\ntext"));
    }

    // バッファ行 0..3 のうち 1 行目が3表示行に折り返している：表示行 = 0, 1, 4, 5（計6）
    private static int Wrapped(int line) => line switch { 0 => 0, 1 => 1, 2 => 4, 3 => 5, _ => -1 };

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2.5, 1.5)]   // 折り返した行の中ほど
    [InlineData(4, 2)]
    [InlineData(5.5, 3.5)]
    public void SourceLineAt_AccountsForWrappedLines(double visual, double expected)
    {
        Assert.Equal(expected, EditorSupportScrollSync.SourceLineAt(visual, 4, 6, Wrapped), 6);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1.5, 2.5)]
    [InlineData(2, 4)]
    [InlineData(3.5, 5.5)]
    public void VisualLineAt_IsTheInverse(double source, double expected)
    {
        Assert.Equal(expected, EditorSupportScrollSync.VisualLineAt(source, 4, 6, Wrapped), 6);
    }

    [Fact]
    public void FoldedLines_MapToTheFoldHeader()
    {
        // 1〜2 行目が 0 行目の下に畳まれている：表示行 = 0, -, -, 1
        static int Folded(int line) => line switch { 0 => 0, 3 => 1, _ => -1 };

        // 見出しの表示行がソースの [0, 3) を受け持つ：見出しが上端なら見出し、表示行の中ほどなら畳んだ範囲の中ほど
        Assert.Equal(0, EditorSupportScrollSync.SourceLineAt(0.001, 4, 2, Folded), 2);
        Assert.Equal(1.5, EditorSupportScrollSync.SourceLineAt(0.5, 4, 2, Folded), 6);
        Assert.Equal(3, EditorSupportScrollSync.SourceLineAt(1, 4, 2, Folded), 6);
        Assert.Equal(0, EditorSupportScrollSync.VisualLineAt(0, 4, 2, Folded), 6);
        Assert.Equal(2.0 / 3, EditorSupportScrollSync.VisualLineAt(2, 4, 2, Folded), 6);
        Assert.Equal(1, EditorSupportScrollSync.VisualLineAt(3, 4, 2, Folded), 6);
    }

    [Fact]
    public void WrappedFoldHeader_UsesTheNextVisibleLineAsItsEnd()
    {
        // 0 行目が3表示行に折り返し、その下の 1〜2 行目が畳まれている：表示行 = 0, -, -, 3
        static int Folded(int line) => line switch { 0 => 0, 3 => 3, _ => -1 };

        Assert.Equal(1.5, EditorSupportScrollSync.SourceLineAt(1.5, 4, 4, Folded), 6);
        Assert.Equal(1.5, EditorSupportScrollSync.VisualLineAt(1.5, 4, 4, Folded), 6);
    }

    [Fact]
    public void LargeFold_DoesNotWalkLineByLine()
    {
        // 10 万行のうち 100〜89999 行目が 99 行目の下に畳まれている
        var calls = 0;
        int Folded(int line)
        {
            calls++;
            return line < 100 ? line : line < 90000 ? -1 : line - 89900;
        }

        Assert.Equal(99, EditorSupportScrollSync.SourceLineAt(99, 100000, 10100, Folded), 6);
        Assert.InRange(EditorSupportScrollSync.VisualLineAt(50000, 100000, 10100, Folded), 99, 100);   // 見出しの表示行の中
        Assert.True(calls < 2000, $"visualLineOf を {calls} 回呼んだ");
    }

    [Theory]
    [InlineData(0, 1000, 100, 0)]
    [InlineData(900, 1000, 100, 0)]
    [InlineData(950, 1000, 100, 0.5)]
    [InlineData(1000, 1000, 100, 1)]
    [InlineData(0, 0, 100, 0)]
    public void TailBlend_RampsOverTheLastScreen(double offset, double max, double viewport, double expected)
    {
        Assert.Equal(expected, EditorSupportScrollSync.TailBlend(offset, max, viewport), 6);
    }
}
