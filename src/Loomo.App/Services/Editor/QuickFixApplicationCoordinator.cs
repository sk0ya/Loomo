using sk0ya.Loomo.CSharp.Editor;

namespace sk0ya.Loomo.App.Services;

/// <summary>Quick Fix候補を現在の診断スナップショットへ結び、編集またはコマンドを適用する。</summary>
internal sealed class QuickFixApplicationCoordinator
{
    private readonly Action<string> _showStatus;
    private readonly Func<LspWorkspaceEdit, WorkspaceEditOutcome> _applyWorkspaceEdit;

    public QuickFixApplicationCoordinator(
        Action<string> showStatus,
        Func<LspWorkspaceEdit, WorkspaceEditOutcome> applyWorkspaceEdit)
    {
        _showStatus = showStatus;
        _applyWorkspaceEdit = applyWorkspaceEdit;
    }

    public async Task ApplyAsync(
        VimEditorControl control,
        LspCodeAction action,
        EditorDiagnosticSession? diagnosticSession,
        int documentVersion,
        long diagnosticSnapshotId,
        string documentPath,
        string documentText,
        int? languageServerVersion)
    {
        try
        {
            if (!IsSnapshotCurrent(control, diagnosticSession, documentVersion,
                    diagnosticSnapshotId, documentPath, documentText, languageServerVersion))
            {
                _showStatus("文書が変更されたため、Quick Fixを取り直してください。");
                return;
            }

            if (action.Edit is null && action.NeedsResolve &&
                control.LspDocument is { IsConnected: true } resolveDocument)
                action = await resolveDocument.ResolveCodeActionAsync(action) ?? action;

            if (!IsSnapshotCurrent(control, diagnosticSession, documentVersion,
                    diagnosticSnapshotId, documentPath, documentText, languageServerVersion))
            {
                _showStatus("文書が変更されたため、Quick Fixを取り直してください。");
                return;
            }

            if (action.Edit is { } edit && (edit.Changes.Count > 0 || edit.FileOperations is { Count: > 0 }))
            {
                var outcome = _applyWorkspaceEdit(edit);
                _showStatus(outcome.Describe(action.Title) ?? $"「{action.Title}」を適用しました。");
                return;
            }

            if (action.Command is { } command)
            {
                if (control.LspDocument is not { IsConnected: true } commandDocument)
                {
                    _showStatus($"「{action.Title}」: 言語サーバーに接続していません。");
                    return;
                }
                // 編集は応答ではなくサーバー起点の applyEdit で返る。
                if (!await commandDocument.ExecuteCommandAsync(command))
                    _showStatus($"「{action.Title}」: サーバーがコマンドを実行できませんでした。");
                return;
            }

            _showStatus($"「{action.Title}」: 適用できる編集が返りませんでした。");
        }
        catch (Exception ex)
        {
            _showStatus($"「{action.Title}」を適用できませんでした: {ex.Message}");
        }
    }

    private static bool IsSnapshotCurrent(
        VimEditorControl control,
        EditorDiagnosticSession? diagnosticSession,
        int documentVersion,
        long diagnosticSnapshotId,
        string documentPath,
        string documentText,
        int? languageServerVersion)
    {
        if (!string.Equals(Path.GetFullPath(control.FilePath ?? ""), Path.GetFullPath(documentPath),
                StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(control.Text, documentText, StringComparison.Ordinal)) return false;
        if (diagnosticSession is not null &&
            (diagnosticSession.Version != documentVersion || diagnosticSession.SnapshotId != diagnosticSnapshotId))
            return false;
        return !languageServerVersion.HasValue || control.LspDocument?.Version == languageServerVersion;
    }
}
