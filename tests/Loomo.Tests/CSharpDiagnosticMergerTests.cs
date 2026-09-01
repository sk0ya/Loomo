using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Configuration;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpDiagnosticMergerTests
{
    [Fact]
    public void Excludes_only_same_code_and_range_from_fallback()
    {
        var same = Diagnostic("sa1101", 2, 4, 2, 9);
        var differentRange = Diagnostic("SA1101", 3, 4, 3, 9);
        var differentCode = Diagnostic("CS0168", 2, 4, 2, 9);

        var result = CSharpDiagnosticMerger.ExcludeDuplicates(
            [Diagnostic("SA1101", 2, 4, 2, 9)],
            [same, differentRange, differentCode]);

        Assert.Equal([differentRange, differentCode], result);
    }

    [Fact]
    public void Does_not_treat_diagnostics_without_code_as_duplicates()
    {
        var left = Diagnostic(null, 0, 0, 0, 1);
        var right = Diagnostic(null, 0, 0, 0, 1);

        Assert.False(CSharpDiagnosticMerger.IsSame(left, right));
        Assert.Single(CSharpDiagnosticMerger.ExcludeDuplicates([left], [right]));
    }

    private static LspDiagnostic Diagnostic(
        string? code, int startLine, int startCharacter, int endLine, int endCharacter)
        => new(new(new(startLine, startCharacter), new(endLine, endCharacter)),
            "diagnostic", DiagnosticSeverity.Warning, "C#", code);
}
