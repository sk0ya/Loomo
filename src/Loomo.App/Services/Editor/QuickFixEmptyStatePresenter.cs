using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.App.Services;

/// <summary>Quick Fix候補が無い理由を、診断・プロジェクト・LSPの準備状態から表示文へ変換する。</summary>
internal static class QuickFixEmptyStatePresenter
{
    internal static string Describe(
        string? filePath,
        IReadOnlyList<EditorTab> tabs,
        IReadOnlyDictionary<VimEditorControl, EditorDiagnosticSession> sessions,
        SolutionModel? solution,
        LspManagementService management,
        IReadOnlyList<LspServerRuntimeStatus> serverStatuses)
    {
        if (CSharpLspFallbackController.IsTarget(filePath ?? ""))
        {
            var currentPath = filePath!;
            var tab = tabs.FirstOrDefault(candidate => candidate.IsRealized &&
                candidate.Control.FilePath is { Length: > 0 } editorPath &&
                string.Equals(Path.GetFullPath(editorPath), Path.GetFullPath(currentPath),
                    StringComparison.OrdinalIgnoreCase));
            if (tab is not null &&
                (!sessions.TryGetValue(tab.Control, out var session) ||
                 !session.TryGetCurrent(currentPath, tab.Control.Text, out _)))
                return "診断を更新しています…";

            if (solution?.ProjectForFile(currentPath) is not { State: ProjectLoadState.Ready })
                return "ソリューションを読み込んでいます（読み込みが終わると使えます）";
        }

        if (RefactoringRequestController.IsLanguageServerReadyFor(
                filePath, management, serverStatuses) is false)
            return "言語サーバーの準備中です（プロジェクトの読み込みが終わると使えます）";
        return "この位置に適用できる修正はありません";
    }
}
