using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;
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
        Assert.True(CSharpSignatureSyntax.SignatureContractChanged(signature, change));
    }

    [Fact]
    public void A_parameter_modifier_change_requires_reference_safety_even_when_call_text_is_unchanged()
    {
        var signature = Read(Sample, 2, 20);
        var change = new SignatureChange(signature.ReturnType, [
            new SignatureParameterChange(0, signature.Parameters[0] with { Modifiers = "in" }),
            new SignatureParameterChange(1, signature.Parameters[1]),
        ]);

        Assert.True(CSharpSignatureSyntax.CallSitesUnaffected(signature, change));
        Assert.True(CSharpSignatureSyntax.SignatureContractChanged(signature, change));
    }

    [Fact]
    public void An_unchanged_signature_does_not_require_a_method_group_safety_scan()
    {
        var signature = Read(Sample, 2, 20);
        var change = new SignatureChange(signature.ReturnType, [
            new SignatureParameterChange(0, signature.Parameters[0]),
            new SignatureParameterChange(1, signature.Parameters[1]),
        ]);

        Assert.False(CSharpSignatureSyntax.SignatureContractChanged(signature, change));
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

    [Fact]
    public void Generic_invocation_is_rewritten_without_losing_type_arguments()
    {
        const string text = """
            class Sample
            {
                T Pick<T>(T left, T right) => left;
                void Use() { var value = Pick<int>(1, 2); }
            }
            """;
        var signature = Read(text, 2, 20);
        var change = new SignatureChange("T", [
            new SignatureParameterChange(1, signature.Parameters[1]),
            new SignatureParameterChange(0, signature.Parameters[0]),
        ]);

        var (source, root, offset) = At(text, "Pick", occurrence: 1);
        var (edits, error) = CSharpSignatureSyntax.RewriteReference(
            source, root, offset, signature, change);

        Assert.Null(error);
        Assert.Contains("Pick<int>(2, 1)", Apply(text, edits));
    }

    [Fact]
    public void A_change_that_collides_with_an_existing_overload_is_refused()
    {
        const string text = """
            class Sample
            {
                void Run(int value) { }
                void Run(string value) { }
            }
            """;
        var signature = Read(text, 2, 22);
        var change = new SignatureChange("void", [
            new SignatureParameterChange(0, signature.Parameters[0] with { Type = "string" }),
        ]);

        var error = CSharpSignatureSyntax.ValidateChange(SourceText.From(text), signature, change);

        Assert.Contains("overloadと衝突", error);
    }

    [Fact]
    public void Explicit_dynamic_and_reflection_references_are_reported_as_unsafe()
    {
        const string dynamicText = """
            class Sample
            {
                void Use(dynamic api) { api.Compute(1); }
            }
            """;
        var dynamicHazard = CSharpSignatureSyntax.FindDynamicOrReflectionHazard(
            SourceText.From(dynamicText), "Compute");
        Assert.Contains("dynamic", dynamicHazard);

        const string reflectionText = """
            using System;
            class Sample
            {
                void Use() { typeof(Sample).GetMethod("Compute"); }
            }
            """;
        var reflectionHazard = CSharpSignatureSyntax.FindDynamicOrReflectionHazard(
            SourceText.From(reflectionText), "Compute");
        Assert.Contains("reflection", reflectionHazard);
    }

    [Fact]
    public void Method_group_and_nameof_references_are_reported_as_unsafe()
    {
        const string methodGroupText = """
            using System;
            class Sample
            {
                void Compute(int value) { }
                void Use() { Action<int> callback = Compute; }
            }
            """;
        var methodGroupHazard = CSharpSignatureSyntax.FindDynamicOrReflectionHazard(
            SourceText.From(methodGroupText), "Compute");
        Assert.Contains("メソッドグループ", methodGroupHazard);

        const string nameofText = """
            class Sample
            {
                void Compute(int value) { }
                string Use() => nameof(Compute);
            }
            """;
        var nameofHazard = CSharpSignatureSyntax.FindDynamicOrReflectionHazard(
            SourceText.From(nameofText), "Compute");
        Assert.Contains("nameof", nameofHazard);
    }

    [Fact]
    public void Semantic_safety_matches_the_target_method_and_ignores_unrelated_same_named_members()
    {
        const string text = """
            using System;
            class Sample
            {
                void Compute(int value) { }
                void Use() { Action<int> callback = Compute; }
            }
            class Other
            {
                int Compute { get; }
            }
            """;
        var path = "C:\\p\\A.cs";
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = text });
        var signature = Read(text, 3, 10);

        var hazard = CSharpSignatureSemanticSafety.FindMethodGroupHazard(compilation, signature);

        Assert.Contains("メソッドグループ", hazard);
    }

    [Fact]
    public void Semantic_reference_scan_finds_bound_calls_without_a_language_server()
    {
        const string service = """
            public class Service
            {
                public string GetValue(int repeat) => repeat.ToString();
            }
            """;
        const string caller = """
            public class Consumer
            {
                public string Read() => new Service().GetValue(1);
            }
            """;
        var servicePath = Path.GetFullPath("C:\\p\\Service.cs");
        var callerPath = Path.GetFullPath("C:\\p\\Consumer.cs");
        var compilation = CSharpSemanticCompilation.Create(new Dictionary<string, string>
        {
            [servicePath] = service,
            [callerPath] = caller,
        });
        var offset = service.IndexOf("GetValue", StringComparison.Ordinal);
        var position = PositionOf(service, offset);
        var signature = CSharpSignatureSyntax.Read(
            servicePath, LspUri.FromPath(servicePath), service,
            position.Line, position.Character).Signature;
        Assert.NotNull(signature);

        var references = CSharpSignatureSemanticSafety.FindInvocationReferences(
            compilation, signature!);

        var reference = Assert.Single(references!);
        Assert.Equal(callerPath, LspUri.TryToLocalPath(reference.Uri));
        Assert.Equal(2, reference.Range.Start.Line);
    }

    [Fact]
    public async Task Change_signature_uses_semantic_references_when_lsp_is_unavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "LoomoChangeSignatureSemantic");
        var servicePath = Path.Combine(root, "Service.cs");
        var callerPath = Path.Combine(root, "Consumer.cs");
        const string service = """
            public class Service
            {
                public string GetValue(int repeat) => repeat.ToString();
            }
            """;
        const string caller = """
            public class Consumer
            {
                public string Read() => new Service().GetValue(1);
            }
            """;
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [servicePath] = service,
            [callerPath] = caller,
        };
        var compilation = CSharpSemanticCompilation.Create(texts);
        var offset = service.IndexOf("GetValue", StringComparison.Ordinal);
        var position = PositionOf(service, offset);
        var signature = CSharpSignatureSyntax.Read(
            servicePath, LspUri.FromPath(servicePath), service,
            position.Line, position.Character).Signature;
        Assert.NotNull(signature);

        var change = new SignatureChange("string", [
            new SignatureParameterChange(0, new SignatureParameter("repeat", "int")),
            new SignatureParameterChange(
                SignatureParameterChange.Added,
                new SignatureParameter("prefix", "string"),
                "\"value:\"")
        ]);
        var refactoring = new CSharpSignatureRefactoring(
            null!, [root], path => texts.GetValueOrDefault(Path.GetFullPath(path)), compilation);

        var plan = await refactoring.PlanAsync(signature!, change);

        Assert.Null(plan.Error);
        Assert.Equal(2, plan.SiteCount);
        Assert.Equal(service, plan.ExpectedTexts![servicePath]);
        Assert.Equal(caller, plan.ExpectedTexts[callerPath]);
        var callerEdits = plan.Changes.Single(pair =>
            string.Equals(LspUri.TryToLocalPath(pair.Key), callerPath,
                StringComparison.OrdinalIgnoreCase)).Value;
        Assert.Contains(callerEdits, edit => edit.NewText == "(1, \"value:\")");
    }

    [Fact]
    public void Semantic_signature_check_detects_an_overload_in_another_partial_declaration()
    {
        const string first = """
            public partial class Sample
            {
                public void Run(int value) { }
            }
            """;
        const string second = """
            public partial class Sample
            {
                public void Run(string value) { }
            }
            """;
        var firstPath = Path.GetFullPath("C:\\p\\Sample.A.cs");
        var secondPath = Path.GetFullPath("C:\\p\\Sample.B.cs");
        var compilation = CSharpSemanticCompilation.Create(new Dictionary<string, string>
        {
            [firstPath] = first,
            [secondPath] = second,
        });
        var offset = first.IndexOf("Run", StringComparison.Ordinal);
        var position = PositionOf(first, offset);
        var signature = CSharpSignatureSyntax.Read(
            firstPath, LspUri.FromPath(firstPath), first,
            position.Line, position.Character).Signature;
        Assert.NotNull(signature);
        var change = new SignatureChange("void", [
            new SignatureParameterChange(0,
                new SignatureParameter("value", "string")),
        ]);

        var error = CSharpSignatureSemanticSafety.FindSignatureConflict(
            compilation, signature!, change);

        Assert.Contains("partial", error);
        Assert.Contains("overload", error);
    }

    [Fact]
    public async Task Semantic_reference_scan_includes_override_interface_and_base_typed_calls()
    {
        const string contract = "public interface IRunner { void Run(int value); }";
        const string baseType = "public class Base : IRunner { public virtual void Run(int value) { } }";
        const string derived = "public class Derived : Base { public override void Run(int value) { } }";
        const string caller = """
            public class Consumer
            {
                public void Use(IRunner contract, Base baseValue, Derived derived)
                {
                    contract.Run(1);
                    baseValue.Run(2);
                    derived.Run(3);
                }
            }
            """;
        var contractPath = Path.GetFullPath("C:\\p\\IRunner.cs");
        var basePath = Path.GetFullPath("C:\\p\\Base.cs");
        var derivedPath = Path.GetFullPath("C:\\p\\Derived.cs");
        var callerPath = Path.GetFullPath("C:\\p\\Consumer.cs");
        var compilation = CSharpSemanticCompilation.Create(new Dictionary<string, string>
        {
            [contractPath] = contract,
            [basePath] = baseType,
            [derivedPath] = derived,
            [callerPath] = caller,
        });
        var offset = derived.IndexOf("Run", StringComparison.Ordinal);
        var position = PositionOf(derived, offset);
        var signature = CSharpSignatureSyntax.Read(
            derivedPath, LspUri.FromPath(derivedPath), derived,
            position.Line, position.Character).Signature;
        Assert.NotNull(signature);

        var invocations = CSharpSignatureSemanticSafety.FindInvocationReferences(
            compilation, signature!);
        Assert.Equal(3, invocations!.Count);
        Assert.All(invocations, location =>
            Assert.Equal(callerPath, LspUri.TryToLocalPath(location.Uri)));

        var declarations = CSharpSignatureSemanticSafety.FindRelatedDeclarationReferences(
            compilation, signature!);
        Assert.Equal(3, declarations!.Count);
        Assert.Contains(declarations, location =>
            string.Equals(LspUri.TryToLocalPath(location.Uri), contractPath,
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(declarations, location =>
            string.Equals(LspUri.TryToLocalPath(location.Uri), basePath,
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(declarations, location =>
            string.Equals(LspUri.TryToLocalPath(location.Uri), derivedPath,
                StringComparison.OrdinalIgnoreCase));

        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [contractPath] = contract,
            [basePath] = baseType,
            [derivedPath] = derived,
            [callerPath] = caller,
        };
        var change = new SignatureChange("void", [
            new SignatureParameterChange(0, new SignatureParameter("value", "int")),
            new SignatureParameterChange(
                SignatureParameterChange.Added,
                new SignatureParameter("label", "string"),
                "\"run\""),
        ]);
        var plan = await new CSharpSignatureRefactoring(
            null!, [Path.GetFullPath("C:\\p")],
            path => texts.GetValueOrDefault(Path.GetFullPath(path)), compilation)
            .PlanAsync(signature!, change);

        Assert.Null(plan.Error);
        Assert.Equal(6, plan.SiteCount); // 3契約宣言 + 3呼び出し
        Assert.Equal(4, plan.Changes.Count);
        Assert.Contains(plan.Changes.Values.SelectMany(edits => edits), edit =>
            edit.NewText.Contains("label", StringComparison.Ordinal));
    }

    [Fact]
    public void Semantic_reference_scan_does_not_merge_different_signatures_in_inherited_interfaces()
    {
        const string baseContract = "public interface IBase { void Run(int value); }";
        const string derivedContract = "public interface IDerived : IBase { void Run(string value); }";
        const string implementation = "public class Impl : IDerived { public void Run(int value) { } public void Run(string value) { } }";
        const string caller = "public class Consumer { public void Use(IDerived value) { value.Run(1); value.Run(\"text\"); } }";
        var basePath = Path.GetFullPath("C:\\p\\IBase.cs");
        var derivedPath = Path.GetFullPath("C:\\p\\IDerived.cs");
        var implementationPath = Path.GetFullPath("C:\\p\\Impl.cs");
        var callerPath = Path.GetFullPath("C:\\p\\Consumer.cs");
        var compilation = CSharpSemanticCompilation.Create(new Dictionary<string, string>
        {
            [basePath] = baseContract,
            [derivedPath] = derivedContract,
            [implementationPath] = implementation,
            [callerPath] = caller,
        });
        var offset = baseContract.IndexOf("Run", StringComparison.Ordinal);
        var position = PositionOf(baseContract, offset);
        var signature = CSharpSignatureSyntax.Read(
            basePath, LspUri.FromPath(basePath), baseContract,
            position.Line, position.Character).Signature;
        Assert.NotNull(signature);

        var invocations = CSharpSignatureSemanticSafety.FindInvocationReferences(
            compilation, signature!);
        var declarations = CSharpSignatureSemanticSafety.FindRelatedDeclarationReferences(
            compilation, signature!);

        var invocation = Assert.Single(invocations!);
        Assert.Equal(callerPath, LspUri.TryToLocalPath(invocation.Uri));
        Assert.Equal(2, declarations!.Count);
        Assert.DoesNotContain(declarations, location =>
            string.Equals(LspUri.TryToLocalPath(location.Uri), derivedPath,
                StringComparison.OrdinalIgnoreCase));
    }

    private static LspPosition PositionOf(string text, int offset)
    {
        var source = Microsoft.CodeAnalysis.Text.SourceText.From(text);
        var position = source.Lines.GetLinePosition(offset);
        return new LspPosition(position.Line, position.Character);
    }
}
