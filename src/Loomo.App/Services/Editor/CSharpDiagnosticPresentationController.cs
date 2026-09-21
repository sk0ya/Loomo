using System.Collections.Generic;
using System.Linq;
using Editor.Controls;
using Editor.Core.Lsp;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>診断スナップショットをエディタ表示と両デバッグProblems一覧へ同期する。</summary>
internal sealed class CSharpDiagnosticPresentationController
{
    private readonly IDictionary<VimEditorControl, EditorDiagnosticSession> _sessions;
    private readonly ProblemsViewModel _dotnetProblems;
    private readonly ProblemsViewModel _typescriptProblems;
    private readonly Action<VimEditorControl> _cancelAnalysis;

    public CSharpDiagnosticPresentationController(
        IDictionary<VimEditorControl, EditorDiagnosticSession> sessions,
        ProblemsViewModel dotnetProblems,
        ProblemsViewModel typescriptProblems,
        Action<VimEditorControl> cancelAnalysis)
    {
        _sessions = sessions;
        _dotnetProblems = dotnetProblems;
        _typescriptProblems = typescriptProblems;
        _cancelAnalysis = cancelAnalysis;
    }

    public EditorDiagnosticSession GetSession(VimEditorControl control)
    {
        if (!_sessions.TryGetValue(control, out var session))
            _sessions[control] = session = new EditorDiagnosticSession();
        return session;
    }

    public void Refresh(VimEditorControl control)
    {
        if (control.FilePath is not { Length: > 0 } path)
        {
            Clear(control);
            return;
        }

        if (!string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
        {
            control.ReplaceLspDiagnosticPresentation(null);
            control.ReplaceDiagnostics([]);
            return;
        }

        if (GetSession(control).Presentation is not { } snapshot)
            return;
        var entries = CSharpEditorResultMapper.ExpandUnnecessaryUsingEntries(
            snapshot.Entries, CSharpEditorResultMapper.UnnecessaryUsingRanges(snapshot));
        var diagnostics = entries.Select(entry => entry.Diagnostic).ToArray();

        // エディタの波線と両Problems一覧は同じ版の診断を表示する。
        control.ReplaceLspDiagnosticPresentation(diagnostics);
        control.ReplaceDiagnostics([]);
        _dotnetProblems.SetEditorDiagnostics(path, snapshot.SnapshotId, entries);
        _typescriptProblems.SetEditorDiagnostics(path, snapshot.SnapshotId, entries);
    }

    public void Clear(VimEditorControl control)
    {
        _cancelAnalysis(control);
        var ownedPath = "";
        IReadOnlyList<LspDiagnostic> retainedLspDiagnostics = [];
        if (_sessions.TryGetValue(control, out var session))
        {
            var release = session.Release();
            ownedPath = release.FilePath;
            retainedLspDiagnostics = release.RetainedLanguageServerDiagnostics;
        }
        _sessions.Remove(control);
        if (control.FilePath is { Length: > 0 })
        {
            control.ClearDiagnostics();
            control.ReplaceLspDiagnosticPresentation(null);
        }
        if (ownedPath.Length == 0) return;

        _dotnetProblems.ClearEditorDiagnostics(ownedPath);
        _typescriptProblems.ClearEditorDiagnostics(ownedPath);

        // 同じファイルを別のエディタで表示中なら、その診断をProblemsへ戻す。
        var sibling = _sessions.FirstOrDefault(pair =>
            !ReferenceEquals(pair.Key, control) &&
            string.Equals(pair.Value.FilePath, ownedPath, StringComparison.OrdinalIgnoreCase)).Key;
        if (sibling is not null)
        {
            Refresh(sibling);
            return;
        }

        if (retainedLspDiagnostics.Count == 0)
            return;
        var uri = LspUri.FromPath(ownedPath);
        _dotnetProblems.SetLspDiagnostics(uri, retainedLspDiagnostics);
        _typescriptProblems.SetLspDiagnostics(uri, retainedLspDiagnostics);
    }

    public void ClearAll()
    {
        foreach (var session in _sessions.Values) session.Clear();
        _sessions.Clear();
        _dotnetProblems.ClearAllEditorDiagnostics();
        _typescriptProblems.ClearAllEditorDiagnostics();
    }
}
