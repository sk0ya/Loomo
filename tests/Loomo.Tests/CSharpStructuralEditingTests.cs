using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Editor;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>構文木を単位にした移動・削除（設計書 §35.2）。テキスト上の位置ではなく
/// 「どの要素を掴んだか」「区切り・コメント・空行がどうなるか」を見る。</summary>
public sealed class CSharpStructuralEditingTests
{
    /// <summary><paramref name="marker"/> が最初に現れる位置をキャレットにする（<paramref name="delta"/> で桁をずらす）。</summary>
    private static LspPosition Caret(string text, string marker, int delta = 0)
    {
        var offset = text.IndexOf(marker, System.StringComparison.Ordinal);
        Assert.True(offset >= 0, $"'{marker}' が見つかりません。");
        var line = SourceText.From(text).Lines.GetLineFromPosition(offset + delta);
        return new LspPosition(line.LineNumber, offset + delta - line.Start);
    }

    private static string Apply(string text, StructuralEditResult result)
    {
        Assert.Null(result.Error);
        var source = SourceText.From(text);
        int Offset(LspPosition p) => source.Lines[p.Line].Start + p.Character;
        return source.WithChanges(result.Edits!.Select(edit => new TextChange(
            TextSpan.FromBounds(Offset(edit.Range.Start), Offset(edit.Range.End)), edit.NewText))).ToString();
    }

    private static string Selected(string text, LspRange range)
    {
        var source = SourceText.From(text);
        int Offset(LspPosition p) => source.Lines[p.Line].Start + p.Character;
        return text[Offset(range.Start)..Offset(range.End)];
    }

    private static StructuralEditResult MoveLines(string text, LspPosition caret, bool forward, LspRange? selection = null)
        => CSharpStructuralEditing.Move(text, selection, caret, StructuralListKind.Lines, forward);

    private static StructuralEditResult MoveElement(string text, LspPosition caret, bool forward, LspRange? selection = null)
        => CSharpStructuralEditing.Move(text, selection, caret, StructuralListKind.Separated, forward);

    [Fact]
    public void 引数を右へ動かすと区切りはそのままで中身だけが入れ替わる()
    {
        var text = "class C { void M() { Foo(alpha, beta, gamma); } }";
        var result = MoveElement(text, Caret(text, "alpha"), forward: true);
        var after = Apply(text, result);

        Assert.Equal("class C { void M() { Foo(beta, alpha, gamma); } }", after);
        Assert.Equal("alpha", Selected(after, result.Selection!));
        Assert.Equal("引数を右へ移動しました。", result.Summary);
    }

    [Fact]
    public void 動かした要素は選択されたままなので続けて押せばさらに動く()
    {
        var text = "class C { void M(int a, string b, bool c) { } }";
        var first = MoveElement(text, Caret(text, "int a"), forward: true);
        var once = Apply(text, first);
        var second = MoveElement(once, first.Selection!.Start, forward: true, first.Selection);
        var twice = Apply(once, second);

        Assert.Equal("class C { void M(string b, bool c, int a) { } }", twice);
        Assert.Equal("int a", Selected(twice, second.Selection!));
    }

    /// <summary><c>Foo(a|, b)</c> のようにキャレットが要素の直後（カンマの手前）にあっても、その要素を掴む。</summary>
    [Fact]
    public void 要素の直後にあるキャレットはその要素を掴む()
    {
        var text = "class C { void M() { Foo(alpha, beta); } }";
        var after = Apply(text, MoveElement(text, Caret(text, "alpha", delta: 5), forward: true));

        Assert.Equal("class C { void M() { Foo(beta, alpha); } }", after);
    }

    /// <summary>要素が1つだけの並び（<c>Foo(x)</c>）は動かす相手がいないので、外側の並びの要素を動かす。</summary>
    [Fact]
    public void 要素が一つだけの並びでは外側の並びの要素を動かす()
    {
        var text = "class C { void M() { Bar(Foo(x), y); } }";
        var after = Apply(text, MoveElement(text, Caret(text, "x)"), forward: true));

        Assert.Equal("class C { void M() { Bar(y, Foo(x)); } }", after);
    }

    [Fact]
    public void 端の要素はそれ以上動かせないと理由を返す()
    {
        var text = "class C { void M() { Foo(alpha, beta); } }";
        var result = MoveElement(text, Caret(text, "alpha"), forward: false);

        Assert.Null(result.Edits);
        Assert.Equal("引数はこれ以上左へ動かせません。", result.Error);
    }

    [Fact]
    public void 文を下へ動かすと直前のコメントも連れていく()
    {
        var text = string.Join("\n",
            "class C",
            "{",
            "    void M()",
            "    {",
            "        // 最初",
            "        First();",
            "        Second(); // 二番目",
            "    }",
            "}");
        var result = MoveLines(text, Caret(text, "First"), forward: true);
        var after = Apply(text, result);

        Assert.Equal(string.Join("\n",
            "class C",
            "{",
            "    void M()",
            "    {",
            "        Second(); // 二番目",
            "        // 最初",
            "        First();",
            "    }",
            "}"), after);
        Assert.Equal("First();", Selected(after, result.Selection!));
        Assert.Equal("文を下へ移動しました。", result.Summary);
    }

    /// <summary>上下の移動は、キャレットが引数の中にあっても「行で並ぶもの」まで上がって文を動かす。</summary>
    [Fact]
    public void 引数の中のキャレットでも上下の移動は文を動かす()
    {
        var text = "class C\n{\n    void M()\n    {\n        A();\n        B(x, y);\n    }\n}";
        var after = Apply(text, MoveLines(text, Caret(text, "y)"), forward: false));

        Assert.Equal("class C\n{\n    void M()\n    {\n        B(x, y);\n        A();\n    }\n}", after);
    }

    [Fact]
    public void メソッドはドキュメントコメントごと動き間の空行は残る()
    {
        var text = string.Join("\n",
            "class C",
            "{",
            "    /// <summary>一つ目</summary>",
            "    void One() { }",
            "",
            "    [Obsolete]",
            "    void Two() { }",
            "}");
        var result = MoveLines(text, Caret(text, "One"), forward: true);
        var after = Apply(text, result);

        Assert.Equal(string.Join("\n",
            "class C",
            "{",
            "    [Obsolete]",
            "    void Two() { }",
            "",
            "    /// <summary>一つ目</summary>",
            "    void One() { }",
            "}"), after);
        Assert.Equal("メソッドを下へ移動しました。", result.Summary);
    }

    /// <summary>同じ行に2つの文があるときは、字下げを壊さないよう構文の範囲だけを入れ替える。</summary>
    [Fact]
    public void 同じ行に並んだ文は構文の範囲だけを入れ替える()
    {
        var text = "class C { void M() { A(); B(); } }";
        var after = Apply(text, MoveLines(text, Caret(text, "A()"), forward: true));

        Assert.Equal("class C { void M() { B(); A(); } }", after);
    }

    [Fact]
    public void ディレクティブをまたぐ入れ替えはしない()
    {
        var text = string.Join("\n",
            "class C",
            "{",
            "    void One() { }",
            "    #region 後半",
            "    void Two() { }",
            "    #endregion",
            "}");
        var result = MoveLines(text, Caret(text, "One"), forward: true);

        Assert.Null(result.Edits);
        Assert.Contains("ディレクティブ", result.Error);
    }

    [Fact]
    public void 選択した範囲を含む要素を動かす()
    {
        var text = "class C\n{\n    void M()\n    {\n        A(1);\n        B(2);\n    }\n}";
        // 文全体（行頭の字下げから行末まで）を選んでいても、その文を掴む。
        var selection = new LspRange(new LspPosition(4, 0), new LspPosition(4, 13));
        var after = Apply(text, MoveLines(text, selection.Start, forward: true, selection));

        Assert.Equal("class C\n{\n    void M()\n    {\n        B(2);\n        A(1);\n    }\n}", after);
    }

    [Fact]
    public void 動かせるものが無い位置では理由を返す()
    {
        var text = "class C { }";
        var result = MoveElement(text, Caret(text, "C"), forward: true);

        Assert.Null(result.Edits);
        Assert.Contains("左右に動かせる要素", result.Error);
    }

    [Fact]
    public void 途中の引数は後ろのカンマごと削除する()
    {
        var text = "class C { void M() { Foo(alpha, beta, gamma); } }";
        var result = CSharpStructuralEditing.Delete(text, null, Caret(text, "beta"));

        Assert.Equal("class C { void M() { Foo(alpha, gamma); } }", Apply(text, result));
        Assert.Equal("引数を削除しました。", result.Summary);
    }

    [Fact]
    public void 末尾の引数は前のカンマごと削除する()
    {
        var text = "class C { void M() { Foo(alpha, beta); } }";

        Assert.Equal("class C { void M() { Foo(alpha); } }",
            Apply(text, CSharpStructuralEditing.Delete(text, null, Caret(text, "beta"))));
    }

    [Fact]
    public void 複数行に並んだ引数を消しても字下げが崩れない()
    {
        var text = "class C { void M() { Foo(\n    alpha,\n    beta,\n    gamma); } }";

        Assert.Equal("class C { void M() { Foo(\n    alpha,\n    gamma); } }",
            Apply(text, CSharpStructuralEditing.Delete(text, null, Caret(text, "beta"))));
    }

    [Fact]
    public void メンバーを消すと空行が二つ続かない()
    {
        var text = string.Join("\n",
            "class C",
            "{",
            "    void One() { }",
            "",
            "    // 二つ目",
            "    void Two() { }",
            "",
            "    void Three() { }",
            "}");
        var result = CSharpStructuralEditing.Delete(text, null, Caret(text, "Two"));

        Assert.Equal(string.Join("\n",
            "class C",
            "{",
            "    void One() { }",
            "",
            "    void Three() { }",
            "}"), Apply(text, result));
        Assert.Equal("メソッドを削除しました。", result.Summary);
    }

    [Fact]
    public void 最後のメンバーを消すと閉じ括弧の前に空行を残さない()
    {
        var text = "class C\n{\n    void One() { }\n\n    void Two() { }\n}";

        Assert.Equal("class C\n{\n    void One() { }\n}",
            Apply(text, CSharpStructuralEditing.Delete(text, null, Caret(text, "Two"))));
    }

    [Fact]
    public void 削除後のキャレットは消した位置に置く()
    {
        var text = "class C { void M() { Foo(alpha, beta); } }";
        var result = CSharpStructuralEditing.Delete(text, null, Caret(text, "alpha"));

        Assert.Equal(result.Selection!.Start, result.Selection.End);
        Assert.Equal(Caret(text, "alpha"), result.Selection.Start);
    }

    [Fact]
    public void カタログとメニューに構造編集が載っている()
    {
        var ids = CSharpEditorCommandCatalog.All.Select(command => command.Id).ToList();
        Assert.Contains(CSharpEditorCommandCatalog.MoveStatementUp, ids);
        Assert.Contains(CSharpEditorCommandCatalog.DeleteSyntaxNode, ids);
        Assert.Equal("Ctrl+Shift+Up", CSharpEditorMenu.GestureFor(CSharpEditorCommandCatalog.MoveStatementUp));
        Assert.Equal(5, CSharpEditorMenu.Build(hasSelection: false).Structure.Count);
    }
}
