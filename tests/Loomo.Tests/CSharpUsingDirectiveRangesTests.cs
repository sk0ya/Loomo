using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Configuration;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpUsingDirectiveRangesTests
{
    [Fact]
    public void Finds_using_directives_at_file_and_namespace_scope_only()
    {
        var source = """
            using System;
            using System.Text;

            namespace App;

            using System.Xml;

            class Sample
            {
                void Run()
                {
                    using var stream = File.OpenRead("a");
                    using (var other = File.OpenRead("b")) { }
                }
            }
            """;

        var ranges = CSharpUsingDirectiveRanges.Find(source);

        // メソッド本文の using 文（UsingStatementSyntax／using var）は別物なので拾わない。
        Assert.Equal([0, 1, 5], ranges.Select(range => range.Start.Line));
        Assert.Equal([0, 1, 5], ranges.Select(range => range.End.Line));
        Assert.Equal("using System;".Length, ranges[0].End.Character);
    }

    [Fact]
    public void Finds_directives_even_when_the_rest_of_the_file_does_not_compile()
    {
        // 入力中の本文はほぼ常に壊れている。意味解析に頼らないのはこれが理由でもある。
        var ranges = CSharpUsingDirectiveRanges.Find("using System.Text;\nclass A { void B( }\n");

        Assert.Equal(0, Assert.Single(ranges).Start.Line);
    }

    [Fact]
    public void Splits_a_grouped_unnecessary_using_diagnostic_into_one_per_directive()
    {
        // Roslyn の IDE0005 は<b>トークンが隣り合う</b>不要usingだけを1本の範囲にまとめる。
        // だからグループ範囲に含まれる using は全部不要——構文位置だけで割ってよい。
        var source = "using System.Text;\nusing System.Xml;\nusing System;\n\nclass Sample { }\n";
        var grouped = new LspDiagnostic(
            new LspRange(new LspPosition(0, 0), new LspPosition(1, "using System.Xml;".Length)),
            "Using ディレクティブは不要です。", DiagnosticSeverity.Hint, "roslyn", "IDE0005");

        var expanded = CSharpDiagnosticMerger.ExpandUnnecessaryUsingGroups(
            [grouped], CSharpUsingDirectiveRanges.Find(source));

        Assert.Equal(2, expanded.Count);
        Assert.Equal([0, 1], expanded.Select(diagnostic => diagnostic.Range.Start.Line));
        Assert.All(expanded, diagnostic => Assert.Equal("IDE0005", diagnostic.Code));
    }
}
