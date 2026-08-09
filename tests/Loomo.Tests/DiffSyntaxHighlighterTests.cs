using Editor.Core.Syntax;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

/// <summary>差分本体の構文色付け（エディタと同じ字句解析器を差分行へ配る）。</summary>
public class DiffSyntaxHighlighterTests
{
    private static DiffRowVm Row(string kind, string text) => new(kind, text);

    private static string TextOf(string line, SyntaxToken token)
        => line.Substring(token.StartColumn, token.Length);

    [Fact]
    public void 統合表示のトークンはパッチのプレフィックス1文字分ずれる()
    {
        var rows = new[] { Row("Context", " var a = 1;"), Row("Added", "+const int b = 2;") };

        var syntax = DiffSyntaxHighlighter.ForUnified(@"C:\work\a.cs", hasPatchPrefix: true, rows);

        var added = Assert.IsType<SyntaxToken[]>(syntax[1]);
        var keyword = added.First(t => t.Kind == TokenKind.Keyword);
        // 表示行（"+" 込み）の桁でそのまま切り出せる＝行を組み立てるときに桁を数え直さなくてよい
        Assert.Equal("const", TextOf(rows[1].Text, keyword));
    }

    [Fact]
    public void プレフィックスの無い差分ではずらさない()
    {
        // AI変更・アドホック比較は全文2つから組み立てるので、行は本文そのもの
        var rows = new[] { Row("Added", "const int b = 2;") };

        var syntax = DiffSyntaxHighlighter.ForUnified(@"C:\work\a.cs", hasPatchPrefix: false, rows);

        var keyword = Assert.IsType<SyntaxToken[]>(syntax[0]).First(t => t.Kind == TokenKind.Keyword);
        Assert.Equal(0, keyword.StartColumn);
    }

    [Fact]
    public void 削除行が開いたブロックコメントは追加行を巻き込まない()
    {
        // 旧側と新側を1本の流れで解析すると、消した "/*" のせいで以降の行がまるごとコメント色になる
        var rows = new[]
        {
            Row("Removed", "-/* 消したコメントの始まり"),
            Row("Added", "+const int b = 2;"),
        };

        var syntax = DiffSyntaxHighlighter.ForUnified(@"C:\work\a.cs", hasPatchPrefix: true, rows);

        var added = Assert.IsType<SyntaxToken[]>(syntax[1]);
        Assert.Contains(added, t => t.Kind == TokenKind.Keyword);
        Assert.DoesNotContain(added, t => t.Kind == TokenKind.Comment);
    }

    [Fact]
    public void ヘッダと省略マーカーは色付けしない()
    {
        var rows = new[]
        {
            Row("Header", "diff --git a/a.cs b/a.cs"),
            Row("Gap", "@@ -1,2 +1,2 @@"),
            Row("Context", " var a = 1;"),
        };

        var syntax = DiffSyntaxHighlighter.ForUnified(@"C:\work\a.cs", hasPatchPrefix: true, rows);

        Assert.Null(syntax[0]);
        Assert.Null(syntax[1]);
        Assert.NotNull(syntax[2]);
    }

    [Fact]
    public void 言語が決まらないファイルとファイル無しの比較は色付けしない()
    {
        var rows = new[] { Row("Added", "+const int b = 2;") };

        Assert.Empty(DiffSyntaxHighlighter.ForUnified(@"C:\work\memo.unknown", true, rows));
        Assert.Empty(DiffSyntaxHighlighter.ForUnified("", true, rows));
    }

    [Fact]
    public void 行数が上限を超える差分は色付けしない()
    {
        var rows = Enumerable.Range(0, DiffSyntaxHighlighter.MaxLines + 1)
            .Select(_ => Row("Context", " var a = 1;")).ToArray();

        Assert.Empty(DiffSyntaxHighlighter.ForUnified(@"C:\work\a.cs", true, rows));
    }

    [Fact]
    public void 左右並びは各側の本文だけを解析し詰め物には色を付けない()
    {
        var rows = new[]
        {
            new DiffSideRowVm("Removed", "const int a = 1;", "Added", "var b = \"x\";", "1", "1"),
            new DiffSideRowVm("Empty", "", "Added", "var c = 2;", "", "2"),
        };

        var left = DiffSyntaxHighlighter.ForSide(@"C:\work\a.cs", rows, left: true);
        var right = DiffSyntaxHighlighter.ForSide(@"C:\work\a.cs", rows, left: false);

        Assert.Contains(Assert.IsType<SyntaxToken[]>(left[0]), t => t.Kind == TokenKind.Keyword);
        Assert.Null(left[1]);   // 片側だけ行がある箇所の詰め物
        Assert.Contains(Assert.IsType<SyntaxToken[]>(right[0]), t => t.Kind == TokenKind.String);
        Assert.NotNull(right[1]);
    }
}
