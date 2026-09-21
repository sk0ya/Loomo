using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.App.Services;

/// <summary>現行の診断スナップショットを基に、C# のホスト Quick Fix を組み立てる。</summary>
internal sealed class CSharpEditorQuickFixCoordinator(
    Func<SolutionModel?> currentSolution,
    StyleCopCodeFixService styleCopCodeFix,
    Func<VimEditorControl, string, string, EditorDiagnosticSession> ensureSession,
    Func<IReadOnlyDictionary<string, string>> openEditorTexts)
{
    public async Task<IReadOnlyList<LspCodeAction>> RequestAsync(
        VimEditorControl control, LspRange range, IReadOnlyList<string>? only)
    {
        if (!CSharpEditorResultMapper.AllowsQuickFixKinds(only))
            return [];
        if (control.FilePath is not { Length: > 0 } path ||
            !string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
            return [];

        if (only?.Any(kind => LspCodeActionKinds.Matches(kind, "source.fixAll") ||
                              LspCodeActionKinds.Matches("source.fixAll", kind)) == true)
            return await RequestCompilerFixAllAsync(path);

        var source = control.Text;
        var session = ensureSession(control, path, source);
        var version = session.Version;
        var snapshot = session.TryGetCurrent(path, source, out var current) ? current : null;
        if (snapshot is null)
            return [];

        var styleClock = Stopwatch.StartNew();
        var styleCop = await RequestStyleCopAsync(control, range, only, session, snapshot);
        RefactorDebugLog.Write(
            $"quickfix host StyleCop file={path} elapsed={styleClock.ElapsedMilliseconds}ms actions={styleCop.Count}");
        if (!IsCurrent(control, session, version, snapshot, source))
            return [];

        var compilerClock = Stopwatch.StartNew();
        var diagnostics = CSharpEditorResultMapper.ExpandUnnecessaryUsingEntries(
                snapshot.Entries, CSharpEditorResultMapper.UnnecessaryUsingRanges(snapshot))
            .Select(entry => entry.Diagnostic).ToArray();
        var compiler = await CSharpCompilerCodeFixService.GetForDiagnosticsAsync(
            currentSolution(), path, source, range, diagnostics, only, openEditorTexts());
        RefactorDebugLog.Write(
            $"quickfix host compiler file={path} elapsed={compilerClock.ElapsedMilliseconds}ms actions={compiler.Count}");

        var suppressionClock = Stopwatch.StartNew();
        var suppressions = snapshot.Entries.Select(entry => entry.Diagnostic)
            .Where(diagnostic => CSharpEditorResultMapper.IsInRange(diagnostic.Range, range))
            .GroupBy(diagnostic => $"{diagnostic.Code}|{diagnostic.Range.Start.Line}|{diagnostic.Range.Start.Character}|" +
                                   $"{diagnostic.Range.End.Line}|{diagnostic.Range.End.Character}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .SelectMany(diagnostic => CSharpSuppressionService.Get(path, source, diagnostic))
            .ToArray();
        RefactorDebugLog.Write(
            $"quickfix host suppressions file={path} elapsed={suppressionClock.ElapsedMilliseconds}ms actions={suppressions.Length}");
        if (!IsCurrent(control, session, version, snapshot, source))
            return [];
        return styleCop.Concat(compiler).Concat(suppressions).ToArray();
    }

    private async Task<IReadOnlyList<LspCodeAction>> RequestStyleCopAsync(
        VimEditorControl control,
        LspRange range,
        IReadOnlyList<string>? only,
        EditorDiagnosticSession session,
        EditorDiagnosticSnapshot snapshot)
    {
        if (!CSharpEditorResultMapper.AllowsQuickFixKinds(only) || control.FilePath is not { Length: > 0 } path)
            return [];
        if (currentSolution()?.ProjectForFile(path) is not { State: ProjectLoadState.Ready } project ||
            !string.Equals(Path.GetFullPath(snapshot.FilePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(control.Text, snapshot.Text, StringComparison.Ordinal) ||
            !session.TryGetCurrent(path, snapshot.Text, out var current) ||
            current.SnapshotId != snapshot.SnapshotId)
            return [];

        var candidates = snapshot.Entries.Select(entry => entry.Diagnostic)
            .Where(diagnostic => diagnostic.Code?.StartsWith("SA", StringComparison.OrdinalIgnoreCase) == true)
            .Where(diagnostic => CSharpEditorResultMapper.IsInRange(diagnostic.Range, range)).ToArray();
        if (candidates.Length == 0 || !styleCopCodeFix.IsAvailable(project))
            return [];

        var actions = new List<LspCodeAction>();
        var version = snapshot.Version;
        var snapshotId = snapshot.SnapshotId;
        var clock = Stopwatch.StartNew();
        foreach (var diagnostic in candidates)
        {
            if (session.Version != version || session.SnapshotId != snapshotId ||
                !string.Equals(control.Text, snapshot.Text, StringComparison.Ordinal))
                return [];
            var result = await styleCopCodeFix.ApplyAsync(project, path, snapshot.Text, diagnostic);
            if (CSharpEditorResultMapper.StyleCopQuickFix(result, diagnostic) is { } action)
                actions.Add(action);
        }
        RefactorDebugLog.Write(
            $"quickfix host StyleCop-fix file={path} elapsed={clock.ElapsedMilliseconds}ms candidates={candidates.Length} actions={actions.Count}");
        return IsCurrent(control, session, version, snapshot, snapshot.Text) ? actions : [];
    }

    private async Task<IReadOnlyList<LspCodeAction>> RequestCompilerFixAllAsync(string path)
    {
        if (currentSolution() is not { State: ProjectLoadState.Ready } solution ||
            solution.ProjectForFile(path) is not { State: ProjectLoadState.Ready })
            return [];
        var plan = CSharpFixAllPlanner.CreateForDocument(solution, path);
        var result = await CSharpFixAllService.ApplyAsync(solution, plan, openEditorTexts());
        return CSharpEditorResultMapper.CompilerFixAll(result) is { } action ? [action] : [];
    }

    private static bool IsCurrent(
        VimEditorControl control,
        EditorDiagnosticSession session,
        int version,
        EditorDiagnosticSnapshot snapshot,
        string source)
        => session.Version == version
           && session.SnapshotId == snapshot.SnapshotId
           && string.Equals(control.Text, source, StringComparison.Ordinal);
}
