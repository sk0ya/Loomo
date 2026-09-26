using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.App.Services;

/// <summary>シグネチャ変更計画の選択、適用、結果文面を調整する。</summary>
internal static class CSharpSignatureChangeCoordinator
{
    internal static async Task ApplyAsync(
        MethodSignature signature,
        SignatureChange change,
        ILspWorkspace workspace,
        IReadOnlyList<string> folders,
        Func<string, string?> findOpenEditorText,
        string? currentText,
        SolutionModel? solution,
        IReadOnlyDictionary<string, string> openEditorTexts,
        CSharpEditorConfigService editorConfig,
        Func<IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>>, IReadOnlyDictionary<string, string>?, WorkspaceEditOutcome> apply,
        Action<string> showStatus)
    {
        var plan = currentText is null
            ? await new CSharpSignatureRefactoring(workspace, folders, findOpenEditorText).PlanAsync(signature, change)
            : await CSharpSignatureRefactoring.PlanWithSolutionAsync(
                workspace, folders, findOpenEditorText, solution,
                signature.FilePath, currentText, signature, change,
                openEditorTexts, editorConfig);
        if (plan.Error is { } planError)
        {
            showStatus(planError);
            return;
        }

        var outcome = apply(plan.Changes, plan.ExpectedTexts);
        if (outcome.Error is { } error)
        {
            showStatus($"シグネチャを変更できませんでした: {error}");
            return;
        }
        showStatus(plan.SkippedOutsideWorkspace > 0
            ? $"シグネチャを変更しました（{plan.SiteCount} 箇所）。"
              + $"ワークスペース外の {plan.SkippedOutsideWorkspace} 箇所は変更していません。"
            : $"シグネチャを変更しました（{plan.SiteCount} 箇所）。");
    }
}
