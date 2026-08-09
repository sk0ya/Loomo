using System.Windows.Documents;
using System.Windows.Media;
using Editor.Core.Syntax;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

public class DiffSessionViewTests
{
    /// <summary>色引きの差し替え：キーワードだけ色を持ち、他は既定色（＝アプリのテーマ色に委ねる）。
    /// 共有のエディタ配色を触らないので、どのスレッドで走らせても安全。</summary>
    private static Brush? Foreground(TokenKind kind)
        => kind == TokenKind.Keyword ? Brushes.Red : null;

    /// <summary>構文色の Run 分割は、行の文字列を1文字も足さず落とさずに再現しなければならない
    /// （差分本体は選択・コピーできる普通のテキストなので、割り方を誤ると読めるものと貼れるものが食い違う）。</summary>
    [Theory]
    // 通常（キーワード＋残り）／トークンの重なり／行末をはみ出す長さ／範囲外の桁
    [InlineData("const int b = 2;", 0, 5)]
    [InlineData("const int b = 2;", 2, 5)]
    [InlineData("const int b = 2;", 12, 99)]
    [InlineData("const int b = 2;", 99, 3)]
    public void 構文色のRun分割は行の文字列を保つ(string text, int start, int length)
    {
        var tokens = new[]
        {
            new SyntaxToken(0, 5, TokenKind.Keyword),
            new SyntaxToken(start, length, TokenKind.String),
        };

        var runs = DiffSessionView.SyntaxRuns(text, tokens, Foreground);

        Assert.Equal(text, string.Concat(runs.Select(r => r.Text)));
    }

    [Fact]
    public void 色を持たない種別は前景を固定せずアプリのテーマ色に委ねる()
    {
        var runs = DiffSessionView.SyntaxRuns(
            "const x",
            [new SyntaxToken(0, 5, TokenKind.Keyword), new SyntaxToken(6, 1, TokenKind.Identifier)],
            Foreground);

        // 先頭＝キーワード色（実ブラシ）、識別子側はリソース参照のまま＝アプリのテーマ追従
        Assert.Equal("const", runs[0].Text);
        Assert.Same(Brushes.Red, runs[0].ReadLocalValue(TextElement.ForegroundProperty));
        Assert.Equal(" x", runs[1].Text);   // 隙間と識別子は同じ色なので1つの Run にまとまる
        Assert.Null(runs[1].ReadLocalValue(TextElement.ForegroundProperty) as Brush);
    }

    [Theory]
    [InlineData(300, 500, 400, 300)]
    [InlineData(450, 500, 400, 400)]
    [InlineData(450, 400, 500, 400)]
    [InlineData(-10, 500, 400, 0)]
    public void 横スクロール位置を左右共通の到達可能範囲に収める(
        double requested,
        double leftMaximum,
        double rightMaximum,
        double expected)
    {
        var actual = DiffSessionView.ClampToSharedHorizontalRange(
            requested, leftMaximum, rightMaximum);

        Assert.Equal(expected, actual);
    }
}
