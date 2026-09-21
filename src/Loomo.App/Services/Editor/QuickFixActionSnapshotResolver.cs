using Editor.Core.Lsp;

namespace sk0ya.Loomo.App.Services;

/// <summary>Quick Fix の適用に使う、候補を返した文書と診断の版を解決する。</summary>
internal static class QuickFixActionSnapshotResolver
{
    internal static QuickFixActionSnapshot? Resolve(
        VimEditorControl control,
        string? filePath,
        string text,
        Func<VimEditorControl, EditorDiagnosticSession> getSession)
    {
        if (filePath is { Length: > 0 } documentPath &&
            string.Equals(Path.GetExtension(documentPath), ".cs", StringComparison.OrdinalIgnoreCase))
        {
            var session = getSession(control);
            if (!session.TryGetCurrent(documentPath, text, out var snapshot))
                return null;
            return new(session, snapshot.Version, snapshot.SnapshotId, snapshot.LanguageServerVersion);
        }

        if (control.LspDocument is { IsReady: true, IsConnected: true } document &&
            string.Equals(document.Text, text, StringComparison.Ordinal))
            return new(null, 0, 0, document.Version);

        return null;
    }
}

internal sealed record QuickFixActionSnapshot(
    EditorDiagnosticSession? DiagnosticSession,
    int DocumentVersion,
    long DiagnosticSnapshotId,
    int? LanguageServerVersion);
