using System.Collections.Generic;
using Editor.Core.Lsp;
using sk0ya.Loomo.Services.Refactoring;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 「メソッドの抽出」の適用前に、サーバーが付けた名前（Roslyn は <c>NewMethod</c>）を見つけて
/// ユーザーの名前へ差し替える（§32.6）。テキストは**実際に Roslyn が返した編集**をそのまま使う。
/// </summary>
public sealed class ExtractedSymbolNameTests
{
    private static LspTextEdit Edit(string newText, int line = 0) =>
        new(new LspRange(new LspPosition(line, 0), new LspPosition(line, 0)), newText);

    // 実測（TempExtractProbe）: ローカル変数を含む選択の抽出で返ってきた2つの編集。
    private const string CallSiteEdit = "\n        int a, b;\n        NewMethod(seed, out a, out b)";
    private const string DeclarationEdit =
        "\n\n    private static void NewMethod(int seed, out int a, out int b)\n    {\n" +
        "        a = seed + 1;\n        b = a * 2;\n    }\n";

    [Fact]
    public void Finds_the_generated_method_name_from_the_declaration_edit()
        => Assert.Equal("NewMethod", ExtractedSymbolName.Find([Edit(CallSiteEdit), Edit(DeclarationEdit, 9)]));

    /// <summary>呼び出し側と宣言がひと続きで返ることもある（ローカル変数なしの選択で実測）。</summary>
    [Fact]
    public void Finds_the_name_when_the_call_site_and_declaration_arrive_as_one_edit()
    {
        const string combined =
            "\n        NewMethod(seed);\n    }\n\n    private static void NewMethod(int seed)\n    {\n" +
            "        System.Console.WriteLine(seed);\n    }";

        Assert.Equal("NewMethod", ExtractedSymbolName.Find([Edit(combined)]));
    }

    [Fact]
    public void Finds_local_function_names_too()
        => Assert.Equal("Helper", ExtractedSymbolName.FindInFragment("void Helper(int x) { }"));

    /// <summary>名前を取り出せない言語・断片では null。呼び出し側は名前を訊かずそのまま適用する。</summary>
    [Fact]
    public void Returns_null_when_nothing_is_declared()
        => Assert.Null(ExtractedSymbolName.Find([Edit("const x = 1;"), Edit("   ")]));

    [Fact]
    public void Renaming_rewrites_every_occurrence_in_the_edits()
    {
        var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>
        {
            ["file:///c:/p/A.cs"] = [Edit(CallSiteEdit), Edit(DeclarationEdit, 9)],
        };

        var renamed = ExtractedSymbolName.Rename(changes, "NewMethod", "BuildTotals");
        var edits = renamed["file:///c:/p/A.cs"];

        Assert.Contains("BuildTotals(seed, out a, out b)", edits[0].NewText);
        Assert.Contains("private static void BuildTotals(int seed", edits[1].NewText);
        Assert.DoesNotContain("NewMethod", edits[0].NewText);
        Assert.DoesNotContain("NewMethod", edits[1].NewText);
    }

    /// <summary>置き換えは単語単位。前方一致する別の識別子を巻き込まない。</summary>
    [Fact]
    public void Renaming_does_not_touch_identifiers_that_merely_start_with_the_same_text()
    {
        var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>
        {
            ["file:///c:/p/A.cs"] = [Edit("NewMethod(); NewMethodOther(); _newMethod = NewMethod;")],
        };

        var text = ExtractedSymbolName.Rename(changes, "NewMethod", "Run")["file:///c:/p/A.cs"][0].NewText;

        Assert.Equal("Run(); NewMethodOther(); _newMethod = Run;", text);
    }

    [Fact]
    public void Renaming_to_the_same_name_is_a_no_op()
    {
        var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>
        {
            ["file:///c:/p/A.cs"] = [Edit(CallSiteEdit)],
        };

        Assert.Same(changes, ExtractedSymbolName.Rename(changes, "NewMethod", "NewMethod"));
    }

    [Theory]
    [InlineData("Build", true)]
    [InlineData("_build2", true)]
    [InlineData("計算する", true)]
    [InlineData("", false)]
    [InlineData("2build", false)]
    [InlineData("my method", false)]
    [InlineData("class", false)]
    public void Identifier_validation_rejects_names_that_would_not_compile(string name, bool expected)
        => Assert.Equal(expected, ExtractedSymbolName.IsValidIdentifier(name));
}
