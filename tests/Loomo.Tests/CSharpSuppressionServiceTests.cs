using System.IO;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpSuppressionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LoomoSuppressionTests",
        Guid.NewGuid().ToString("N"));

    public CSharpSuppressionServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Creates_a_line_scoped_pragma_for_compiler_diagnostics()
    {
        var path = Path.Combine(_root, "Sample.cs");
        var source = "class Sample\n{\n    void Run() { int unused; }\n}\n";
        var action = Assert.Single(CSharpSuppressionService.Get(path, source,
            Diagnostic("CS0168", 2, 24)));
        var edits = Assert.Single(action.Edit!.Changes.Values);
        var updated = Apply(source, edits);

        Assert.Contains("#pragma warning disable CS0168", updated);
        Assert.Contains("#pragma warning restore CS0168", updated);
        Assert.Contains("void Run() { int unused; }", updated);
    }

    [Fact]
    public void Supports_stylecop_codes_and_rejects_non_csharp_or_directive_lines()
    {
        var path = Path.Combine(_root, "Sample.cs");
        var source = "#if DEBUG\nclass Sample { }\n#endif\n";
        Assert.Single(CSharpSuppressionService.Get(path, source, Diagnostic("SA1600", 1, 6)));
        Assert.Empty(CSharpSuppressionService.Get(path, source, Diagnostic("CS0168", 0, 0)));
        Assert.Empty(CSharpSuppressionService.Get(Path.Combine(_root, "Sample.txt"), source,
            Diagnostic("CS0168", 1, 0)));
        Assert.Empty(CSharpSuppressionService.Get(path, source, Diagnostic("LOOMO", 1, 0)));
    }

    [Fact]
    public void Preserves_eof_without_joining_restore_to_the_last_statement()
    {
        var path = Path.Combine(_root, "Sample.cs");
        var source = "class Sample { void Run() { int unused; } }";
        var action = Assert.Single(CSharpSuppressionService.Get(path, source,
            Diagnostic("CS0168", 0, 32)));
        var updated = Apply(source, Assert.Single(action.Edit!.Changes.Values));

        Assert.Contains("; } }\n#pragma warning restore CS0168\n", updated);
    }

    [Fact]
    public async Task Suppresses_the_actual_roslyn_compiler_diagnostic_after_applying_the_edit()
    {
        var path = Path.Combine(_root, "Compiler.cs");
        var source = "class Compiler { void Run() { int unused; } }\n";
        var solution = CreateSolution(path);
        var compiler = new CSharpCompilerDiagnosticService();
        var before = await compiler.AnalyzeAsync(solution, path, source);
        var diagnostic = Assert.Single(before.Diagnostics, item => item.Code == "CS0168");

        var action = Assert.Single(CSharpSuppressionService.Get(path, source, diagnostic));
        var updated = Apply(source, Assert.Single(action.Edit!.Changes.Values));
        var after = await compiler.AnalyzeAsync(solution, path, updated);

        Assert.DoesNotContain(after.Diagnostics, item => item.Code == "CS0168");
    }

    private static LspDiagnostic Diagnostic(string code, int line, int character)
        => new(new LspRange(new LspPosition(line, character), new LspPosition(line, character + 1)),
            "diagnostic", DiagnosticSeverity.Warning, "Compiler", code);

    private SolutionModel CreateSolution(string sourcePath)
        => new(null, "Suppression", _root,
            [new ProjectModel("Suppression", Path.Combine(_root, "Suppression.csproj"), _root, [],
                [new TargetFrameworkModel("net10.0", [], "latest",
                    [new ProjectItem(Path.GetFileName(sourcePath), sourcePath)], [], [], [])],
                "net10.0", false, ProjectLoadState.Ready)],
            ProjectLoadState.Ready);

    private static string Apply(string source, IReadOnlyList<LspTextEdit> edits)
    {
        var text = source;
        foreach (var edit in edits.OrderByDescending(edit => edit.Range.Start.Line)
                     .ThenByDescending(edit => edit.Range.Start.Character))
        {
            var start = Offset(text, edit.Range.Start);
            var end = Offset(text, edit.Range.End);
            text = text[..start] + edit.NewText + text[end..];
        }
        return text;
    }

    private static int Offset(string text, LspPosition position)
    {
        var line = 0;
        var offset = 0;
        while (line < position.Line && offset < text.Length)
        {
            var next = text.IndexOf('\n', offset);
            if (next < 0) return text.Length;
            offset = next + 1;
            line++;
        }
        return Math.Min(text.Length, offset + position.Character);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
