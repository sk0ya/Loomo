using System.IO;
using Editor.Core.Lsp;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

public sealed class EditorDiagnosticSessionTests
{
    [Fact]
    public void NewTextVersionRetainsPresentationButRejectsOldAnalysisAndQuickFixSnapshot()
    {
        var session = new EditorDiagnosticSession();
        var path = Path.Combine(Path.GetTempPath(), "DiagnosticSession.cs");
        var firstVersion = session.Begin(path, "class A { }", [EditorDiagnosticOrigin.Compiler]);
        var diagnostic = Diagnostic("CS1002", 0, 8);
        Assert.True(session.Publish(firstVersion, EditorDiagnosticOrigin.Compiler, [diagnostic]));

        var secondVersion = session.Begin(path, "class A { int value }", [EditorDiagnosticOrigin.LanguageServer]);

        Assert.True(secondVersion > firstVersion);
        Assert.False(session.TryGetCurrent(path, "class A { int value }", out _));
        Assert.Equal(secondVersion, session.Presentation!.Version);
        Assert.False(session.Presentation.HasCurrentResult);
        Assert.Empty(session.Presentation.Diagnostics);
        Assert.False(session.Publish(firstVersion, EditorDiagnosticOrigin.Compiler, []));
        Assert.True(session.Publish(secondVersion, EditorDiagnosticOrigin.LanguageServer, []));

        Assert.True(session.TryGetCurrent(path, "class A { int value }", out var current));
        Assert.Equal(secondVersion, current.Version);
        Assert.Empty(current.Diagnostics);
    }

    [Fact]
    public void ReanalysisOfSameTextKeepsRangesVisibleButStillDisablesCurrentQuickFixes()
    {
        var session = new EditorDiagnosticSession();
        var path = Path.Combine(Path.GetTempPath(), "DiagnosticSession.cs");
        var text = "class A { }";
        var firstVersion = session.Begin(path, text, [EditorDiagnosticOrigin.Compiler]);
        var diagnostic = Diagnostic("CS1002", 0, 8);
        Assert.True(session.Publish(firstVersion, EditorDiagnosticOrigin.Compiler, [diagnostic]));

        var secondVersion = session.Begin(path, text, [EditorDiagnosticOrigin.Compiler]);

        Assert.Equal(secondVersion, session.Presentation!.Version);
        Assert.False(session.Presentation.HasCurrentResult);
        Assert.Equal(diagnostic, Assert.Single(session.Presentation.Diagnostics));
        Assert.False(session.TryGetCurrent(path, text, out _));
    }

    [Fact]
    public void CurrentSnapshotMergesSourcesAndPrefersLanguageServerDuplicate()
    {
        var session = new EditorDiagnosticSession();
        var path = Path.Combine(Path.GetTempPath(), "DiagnosticSession.cs");
        var version = session.Begin(path, "class A { }",
            [EditorDiagnosticOrigin.LanguageServer, EditorDiagnosticOrigin.Compiler, EditorDiagnosticOrigin.StyleCop]);
        var lsp = Diagnostic("CS1002", 0, 8, "LSP message");
        var compilerDuplicate = Diagnostic("CS1002", 0, 8, "Compiler message");
        var styleCop = Diagnostic("SA1200", 1, 0);

        Assert.True(session.Publish(version, EditorDiagnosticOrigin.Compiler, [compilerDuplicate]));
        Assert.False(session.TryGetCurrent(path, "class A { }", out _));
        Assert.True(session.Publish(version, EditorDiagnosticOrigin.StyleCop, [styleCop]));
        Assert.False(session.TryGetCurrent(path, "class A { }", out _));
        Assert.True(session.Publish(version, EditorDiagnosticOrigin.LanguageServer, [lsp], languageServerVersion: 4));

        Assert.True(session.TryGetCurrent(path, "class A { }", out var current));
        Assert.Equal(4, current.LanguageServerVersion);
        Assert.Equal(2, current.Entries.Count);
        Assert.Equal(EditorDiagnosticOrigin.LanguageServer, current.Entries[0].Origin);
        Assert.Equal("LSP message", current.Entries[0].Diagnostic.Message);
        Assert.Equal(EditorDiagnosticOrigin.StyleCop, current.Entries[1].Origin);

        Assert.True(session.Publish(version, EditorDiagnosticOrigin.LanguageServer, [lsp with { Message = "Updated message" }], 5));
        Assert.True(session.TryGetCurrent(path, "class A { }", out var updated));
        Assert.True(updated.SnapshotId > current.SnapshotId);
        Assert.Equal("Updated message", updated.Entries[0].Diagnostic.Message);
    }

    [Fact]
    public void ProblemsRejectsOlderEditorSnapshotAndReplacesTheWholeVersionAtomically()
    {
        var vm = new ProblemsViewModel();
        var path = Path.Combine(Path.GetTempPath(), "DiagnosticSession.cs");
        var current = new EditorDiagnosticEntry(EditorDiagnosticOrigin.LanguageServer,
            Diagnostic("CS1002", 0, 8));

        Assert.True(vm.SetEditorDiagnostics(path, 2, [current]));
        Assert.False(vm.SetEditorDiagnostics(path, 1, []));
        Assert.Equal("CS1002", Assert.Single(vm.Groups.SelectMany(group => group.Items)).Code);

        Assert.True(vm.SetEditorDiagnostics(path, 3, []));
        Assert.Empty(vm.Groups);
    }

    [Fact]
    public void ShowsWhatHasArrivedWhileTheSlowestAnalyzerIsStillRunning()
    {
        // 表示は一番遅い解析元を待たない。待たせると、開いた直後にLSPがエラーを返していても
        // StyleCopのRoslyn解析が終わるまで波線もProblemsも空、という壊れ方をする。
        var session = new EditorDiagnosticSession();
        var path = Path.Combine(Path.GetTempPath(), "DiagnosticSession.cs");
        var text = "class A { }";
        var version = session.Begin(path, text,
            [EditorDiagnosticOrigin.LanguageServer, EditorDiagnosticOrigin.StyleCop]);
        Assert.Empty(session.Presentation!.Diagnostics);

        var lsp = Diagnostic("CS1002", 0, 8);
        Assert.True(session.Publish(version, EditorDiagnosticOrigin.LanguageServer, [lsp], 1));

        Assert.Equal(lsp, Assert.Single(session.Presentation!.Diagnostics));
        Assert.False(session.Presentation.HasCurrentResult);
        // 行動（Quick Fix）だけは全員分が揃うまで待つ。
        Assert.False(session.TryGetCurrent(path, text, out _));

        Assert.True(session.Publish(version, EditorDiagnosticOrigin.StyleCop, [Diagnostic("SA1200", 1, 0)]));
        Assert.True(session.TryGetCurrent(path, text, out var current));
        Assert.Equal(2, current.Entries.Count);
    }

    [Fact]
    public void ProblemsTakesTheNewestSnapshotEvenFromABufferWithASmallerVersion()
    {
        // 同じファイルを分割・切り離しで2枚開くと、版番号は別々に進む。Problemsはファイルパスで
        // 束ねているので、版番号で新旧を決めると後から開いた側の更新が永久に弾かれる。
        var path = Path.Combine(Path.GetTempPath(), "DiagnosticSession.cs");
        var text = "class A { }";
        var first = new EditorDiagnosticSession();
        first.Begin(path, text, [EditorDiagnosticOrigin.Compiler]);
        var firstVersion = first.Begin(path, text, [EditorDiagnosticOrigin.Compiler]);
        Assert.True(first.Publish(firstVersion, EditorDiagnosticOrigin.Compiler, [Diagnostic("CS1002", 0, 8)]));
        var older = first.Presentation!;

        var second = new EditorDiagnosticSession();
        var secondVersion = second.Begin(path, text, [EditorDiagnosticOrigin.Compiler]);
        Assert.True(second.Publish(secondVersion, EditorDiagnosticOrigin.Compiler, []));
        var newer = second.Presentation!;

        Assert.True(newer.Version < older.Version);
        Assert.True(newer.SnapshotId > older.SnapshotId);

        var vm = new ProblemsViewModel();
        Assert.True(vm.SetEditorDiagnostics(path, older.SnapshotId, older.Entries));
        Assert.True(vm.SetEditorDiagnostics(path, newer.SnapshotId, newer.Entries));
        Assert.Empty(vm.Groups);
    }

    private static LspDiagnostic Diagnostic(string code, int line, int character, string? message = null)
        => new(new(new(line, character), new(line, character + 1)), message ?? code,
            DiagnosticSeverity.Warning, code.StartsWith("SA", StringComparison.Ordinal) ? "StyleCop" : "Compiler", code);
}
