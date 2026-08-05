using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.Services.Refactoring;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>C# の「シグネチャの変更」（設計書 §32.5）の構文書き換え。
/// 呼び出し元の列挙は言語サーバーの仕事なので、ここでは「位置を渡したら正しく書き換わるか」だけを見る。</summary>
public sealed class CSharpSignatureSyntaxTests
{
    private static MethodSignature Read(string text, int line, int character)
    {
        var target = CSharpSignatureSyntax.Read("C:\\p\\A.cs", "file:///c:/p/A.cs", text, line, character);
        Assert.Null(target.Error);
        return target.Signature!;
    }

    /// <summary>指定行のうち <paramref name="marker"/> が最初に現れる桁を、その位置として使う。</summary>
    private static (SourceText Source, Microsoft.CodeAnalysis.SyntaxNode Root, int Offset) At(
        string text, string marker, int occurrence = 0)
    {
        var source = SourceText.From(text);
        int offset = -1;
        for (int i = 0; i <= occurrence; i++)
            offset = text.IndexOf(marker, offset + 1, System.StringComparison.Ordinal);
        Assert.True(offset >= 0, $"'{marker}' が見つかりません。");
        return (source, CSharpSyntaxTree.ParseText(source).GetRoot(), offset);
    }

    private static string Apply(string text, IReadOnlyList<LspTextEdit> edits)
    {
        var source = SourceText.From(text);
        foreach (var edit in edits.OrderByDescending(e => e.Range.Start.Line)
                                  .ThenByDescending(e => e.Range.Start.Character))
        {
            int start = CSharpSignatureSyntax.ClampToLine(source, edit.Range.Start.Line, edit.Range.Start.Character);
            int end = CSharpSignatureSyntax.ClampToLine(source, edit.Range.End.Line, edit.Range.End.Character);
            source = source.Replace(TextSpan.FromBounds(start, end), edit.NewText);
        }
        return source.ToString();
    }

    private const string Sample = """
        class Calculator
        {
            public int Add(int left, int right)
            {
                return left + right;
            }

            void Use()
            {
                var total = Add(1, 2);
            }
        }
        """;

    [Fact]
    public void Reading_a_declaration_yields_its_parameters_and_return_type()
    {
        var signature = Read(Sample, line: 2, character: 20);   // "Add" の上

        Assert.Equal("Add", signature.Name);
        Assert.Equal("int", signature.ReturnType);
        Assert.False(signature.IsConstructor);
        Assert.Equal(["left", "right"], signature.Parameters.Select(p => p.Name));
        Assert.Equal(["int", "int"], signature.Parameters.Select(p => p.Type));
        Assert.Equal("int Add(int left, int right)", signature.Display);
    }

    [Fact]
    public void Reading_outside_a_method_explains_what_to_do()
    {
        var target = CSharpSignatureSyntax.Read("A.cs", "file:///a.cs", Sample, line: 0, character: 2);

        Assert.Null(target.Signature);
        Assert.Contains("宣言の中にキャレット", target.Error);
    }

    [Fact]
    public void Swapping_two_parameters_rewrites_the_declaration_and_the_call()
    {
        var signature = Read(Sample, 2, 20);
        var change = new SignatureChange("int", [
            new SignatureParameterChange(1, signature.Parameters[1]),
            new SignatureParameterChange(0, signature.Parameters[0]),
        ]);

        var (source, root, offset) = At(Sample, "Add", occurrence: 1);   // 呼び出し側
        var (callEdits, callError) = CSharpSignatureSyntax.RewriteReference(source, root, offset, signature, change);
        var (_, declRoot, declOffset) = At(Sample, "Add", occurrence: 0);
        var (declEdits, declError) = CSharpSignatureSyntax.RewriteReference(source, declRoot, declOffset, signature, change);

        Assert.Null(callError);
        Assert.Null(declError);
        var result = Apply(Sample, [.. declEdits, .. callEdits]);
        Assert.Contains("public int Add(int right, int left)", result);
        Assert.Contains("Add(2, 1)", result);
    }

    [Fact]
    public void Removing_a_parameter_drops_its_argument()
    {
        var signature = Read(Sample, 2, 20);
        var change = new SignatureChange("int", [new SignatureParameterChange(0, signature.Parameters[0])]);

        var (source, root, offset) = At(Sample, "Add", occurrence: 1);
        var (edits, error) = CSharpSignatureSyntax.RewriteReference(source, root, offset, signature, change);

        Assert.Null(error);
        Assert.Contains("Add(1)", Apply(Sample, edits));
    }

    [Fact]
    public void Adding_a_parameter_writes_the_given_value_at_every_call()
    {
        var signature = Read(Sample, 2, 20);
        var change = new SignatureChange("int", [
            new SignatureParameterChange(0, signature.Parameters[0]),
            new SignatureParameterChange(1, signature.Parameters[1]),
            new SignatureParameterChange(
                SignatureParameterChange.Added, new SignatureParameter("scale", "int"), "1"),
        ]);

        var (source, root, offset) = At(Sample, "Add", occurrence: 1);
        var (edits, error) = CSharpSignatureSyntax.RewriteReference(source, root, offset, signature, change);

        Assert.Null(error);
        Assert.Contains("Add(1, 2, 1)", Apply(Sample, edits));
    }

    /// <summary>呼び出し側に書く値も既定値も無い追加は、黙って壊れたコードを作らずに止める。</summary>
    [Fact]
    public void Adding_a_parameter_without_a_value_or_default_is_refused()
    {
        var signature = Read(Sample, 2, 20);
        var change = new SignatureChange("int", [
            new SignatureParameterChange(0, signature.Parameters[0]),
            new SignatureParameterChange(1, signature.Parameters[1]),
            new SignatureParameterChange(
                SignatureParameterChange.Added, new SignatureParameter("scale", "int")),
        ]);

        var (source, root, offset) = At(Sample, "Add", occurrence: 1);
        var (_, error) = CSharpSignatureSyntax.RewriteReference(source, root, offset, signature, change);

        Assert.Contains("呼び出し側へ書く値も既定値もありません", error);
    }

    private const string NamedSample = """
        class Sample
        {
            void Draw(int width, int height, string label = "")
            {
            }

            void Use()
            {
                Draw(height: 3, width: 4);
                Draw(1, 2, "x");
            }
        }
        """;

    /// <summary>名前付き引数は名前ごと運ぶ。並べ替えても呼び出しの意味が変わってはいけない。</summary>
    [Fact]
    public void Named_arguments_keep_their_names_when_parameters_move()
    {
        var signature = Read(NamedSample, 2, 10);
        var change = new SignatureChange("void", [
            new SignatureParameterChange(1, signature.Parameters[1]),
            new SignatureParameterChange(0, signature.Parameters[0]),
            new SignatureParameterChange(2, signature.Parameters[2]),
        ]);

        var (source, root, offset) = At(NamedSample, "Draw", occurrence: 1);
        var (edits, error) = CSharpSignatureSyntax.RewriteReference(source, root, offset, signature, change);

        Assert.Null(error);
        Assert.Contains("Draw(height: 3, width: 4)", Apply(NamedSample, edits));
    }

    /// <summary>パラメーターを改名したら、名前付き引数の名前も追随させる（さもないとコンパイルが壊れる）。</summary>
    [Fact]
    public void Renaming_a_parameter_updates_named_arguments()
    {
        var signature = Read(NamedSample, 2, 10);
        var change = new SignatureChange("void", [
            new SignatureParameterChange(0, signature.Parameters[0] with { Name = "w" }),
            new SignatureParameterChange(1, signature.Parameters[1] with { Name = "h" }),
            new SignatureParameterChange(2, signature.Parameters[2]),
        ]);

        var (source, root, offset) = At(NamedSample, "Draw", occurrence: 1);
        var (edits, error) = CSharpSignatureSyntax.RewriteReference(source, root, offset, signature, change);

        Assert.Null(error);
        Assert.Contains("Draw(w: 4, h: 3)", Apply(NamedSample, edits));
    }

    /// <summary>省略された既定値つき引数は省略のまま運ぶ（勝手に書き足さない）。
    /// 書き換えた実引数は宣言順に並び直る——名前付きなので意味は変わらない。</summary>
    [Fact]
    public void Omitted_optional_arguments_stay_omitted()
    {
        var signature = Read(NamedSample, 2, 10);
        var change = new SignatureChange("void", [
            new SignatureParameterChange(0, signature.Parameters[0]),
            new SignatureParameterChange(1, signature.Parameters[1]),
            new SignatureParameterChange(2, signature.Parameters[2]),
        ]);

        var (source, root, offset) = At(NamedSample, "Draw", occurrence: 1);
        var (edits, error) = CSharpSignatureSyntax.RewriteReference(source, root, offset, signature, change);

        Assert.Null(error);
        var result = Apply(NamedSample, edits);
        Assert.Contains("Draw(width: 4, height: 3)", result);
        Assert.DoesNotContain("label:", result);
    }

    /// <summary>省略された引数が並べ替えで途中に来たら、既定値を書き出して位置を保つ。</summary>
    [Fact]
    public void An_omitted_optional_that_moves_into_the_middle_is_written_out()
    {
        var signature = Read(NamedSample, 2, 10);
        var change = new SignatureChange("void", [
            new SignatureParameterChange(2, signature.Parameters[2]),   // label（省略されていた）
            new SignatureParameterChange(0, signature.Parameters[0]),
            new SignatureParameterChange(1, signature.Parameters[1]),
        ]);

        var (source, root, offset) = At(NamedSample, "Draw", occurrence: 1);
        var (edits, error) = CSharpSignatureSyntax.RewriteReference(source, root, offset, signature, change);

        Assert.Null(error);
        Assert.Contains("""Draw(label: "", width: 4, height: 3)""", Apply(NamedSample, edits));
    }

    private const string MethodGroupSample = """
        using System;
        class Sample
        {
            int Twice(int value) => value * 2;

            void Use()
            {
                Func<int, int> f = Twice;
            }
        }
        """;

    /// <summary>メソッドグループとして渡されている参照は、引数の数を変えると壊れる。
    /// 黙って半分だけ書き換えるくらいなら、理由を出して何もしないほうがよい。</summary>
    [Fact]
    public void A_method_group_reference_aborts_with_a_reason()
    {
        var signature = Read(MethodGroupSample, 3, 8);
        var change = new SignatureChange("int", []);

        var (source, root, offset) = At(MethodGroupSample, "Twice", occurrence: 1);
        var (edits, error) = CSharpSignatureSyntax.RewriteReference(source, root, offset, signature, change);

        Assert.Empty(edits);
        Assert.Contains("メソッドそのものとして参照されています", error);
    }

    private const string ConstructorSample = """
        class Box
        {
            public Box(int size, string name)
            {
            }
        }

        class Use
        {
            void Make()
            {
                var box = new Box(1, "a");
            }
        }
        """;

    [Fact]
    public void Constructors_are_rewritten_at_their_object_creations()
    {
        var target = CSharpSignatureSyntax.Read("A.cs", "file:///a.cs", ConstructorSample, 2, 12);
        var signature = target.Signature!;
        Assert.True(signature.IsConstructor);

        var change = new SignatureChange("", [
            new SignatureParameterChange(1, signature.Parameters[1]),
            new SignatureParameterChange(0, signature.Parameters[0]),
        ]);

        var (source, root, offset) = At(ConstructorSample, "Box", occurrence: 2);   // new Box(...)
        var (edits, error) = CSharpSignatureSyntax.RewriteReference(source, root, offset, signature, change);

        Assert.Null(error);
        Assert.Contains("""new Box("a", 1)""", Apply(ConstructorSample, edits));
    }

    private const string DocumentedSample = """
        class Sample
        {
            /// <summary>足す。</summary>
            /// <param name="left">左。</param>
            /// <param name="right">右。</param>
            public int Add(int left, int right) => left + right;
        }
        """;

    /// <summary>XMLドキュメントを置き去りにすると CS1572/CS1573 の警告が新たに生える。</summary>
    [Fact]
    public void Doc_comments_follow_renamed_and_removed_parameters()
    {
        var signature = Read(DocumentedSample, 5, 16);
        var change = new SignatureChange("int", [
            new SignatureParameterChange(0, signature.Parameters[0] with { Name = "a" }),
        ]);

        var (source, root, offset) = At(DocumentedSample, "Add", occurrence: 0);
        var (edits, error) = CSharpSignatureSyntax.RewriteReference(source, root, offset, signature, change);

        Assert.Null(error);
        var result = Apply(DocumentedSample, edits);
        Assert.Contains("""<param name="a">左。</param>""", result);
        Assert.DoesNotContain("name=\"right\"", result);
        Assert.Contains("public int Add(int a) => left + right;", result);
    }

    [Fact]
    public void Changing_only_types_and_the_return_type_leaves_call_sites_alone()
    {
        var signature = Read(Sample, 2, 20);
        var change = new SignatureChange("long", [
            new SignatureParameterChange(0, signature.Parameters[0] with { Type = "long" }),
            new SignatureParameterChange(1, signature.Parameters[1] with { Type = "long" }),
        ]);

        Assert.True(CSharpSignatureSyntax.CallSitesUnaffected(signature, change));
    }

    [Fact]
    public void Reordering_makes_call_sites_relevant_again()
    {
        var signature = Read(Sample, 2, 20);
        var change = new SignatureChange("int", [
            new SignatureParameterChange(1, signature.Parameters[1]),
            new SignatureParameterChange(0, signature.Parameters[0]),
        ]);

        Assert.False(CSharpSignatureSyntax.CallSitesUnaffected(signature, change));
    }

    private const string OverloadSample = """
        class Sample
        {
            void Log(string message) { }

            void Use()
            {
                Log("a");
            }
        }
        """;

    /// <summary>宣言そのものが参照一覧にも現れるので、同じ範囲へ2回編集を作らない。</summary>
    [Fact]
    public void The_declaration_is_not_edited_twice_when_it_also_appears_as_a_reference()
    {
        var signature = Read(OverloadSample, 2, 10);
        var change = new SignatureChange("void", [
            new SignatureParameterChange(0, signature.Parameters[0] with { Name = "text" }),
        ]);

        var (source, root, offset) = At(OverloadSample, "Log", occurrence: 0);
        var (edits, error) = CSharpSignatureSyntax.RewriteReference(source, root, offset, signature, change);

        Assert.Null(error);
        var result = Apply(OverloadSample, edits);
        Assert.Contains("void Log(string text) { }", result);
        Assert.DoesNotContain("string text, string text", result);
    }
}
