using Editor.Core.Lsp;
using sk0ya.Loomo.Services.Refactoring;

namespace sk0ya.Loomo.App.Services;

/// <summary>LSPリファクタリングの解決・適用・command実行をUIから切り離して調整する。</summary>
internal static class RefactoringActionCoordinator
{
    internal static async Task ApplyAsync(
        ILspDocument? document,
        RefactoringItem item,
        Func<RefactoringItem, IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>>,
            (IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> Changes, bool Cancelled)> renameExtractedSymbol,
        Func<LspWorkspaceEdit, WorkspaceEditOutcome> applyWorkspaceEdit,
        Action<string> showStatus)
    {
        if (document is not { IsConnected: true })
        {
            showStatus($"「{item.Title}」: 言語サーバーに接続していません。");
            return;
        }

        var action = item.Action;
        try
        {
            if (action.Edit is null && action.NeedsResolve)
                action = await document.ResolveCodeActionAsync(action) ?? action;

            if (action.Edit is { } edit &&
                (edit.Changes.Count > 0 || edit.FileOperations is { Count: > 0 }))
            {
                var (changes, cancelled) = renameExtractedSymbol(item, edit.Changes);
                if (cancelled) return;

                var outcome = applyWorkspaceEdit(edit with { Changes = changes });
                showStatus(outcome.Describe(item.Title) ?? $"「{item.Title}」を適用しました。");
                return;
            }

            if (action.Command is { } command)
            {
                // 編集は応答ではなくサーバー起点の applyEdit で返る。
                bool sent = await document.ExecuteCommandAsync(command);
                if (!sent) showStatus($"「{item.Title}」: サーバーがコマンドを実行できませんでした。");
                return;
            }

            showStatus($"「{item.Title}」: 適用できる編集がサーバーから返りませんでした。");
        }
        catch (Exception ex)
        {
            showStatus($"「{item.Title}」を適用できませんでした: {ex.Message}");
        }
    }
}
