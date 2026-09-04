using Editor.Controls;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.App.Views;

/// <summary>C# の project／solution 範囲 Fix all。文書単位のUIはEditor側に置き、
/// C#プロジェクトの列挙とLSPセッション共有はLoomo側で行う。</summary>
public partial class ShellWindow
{
    /// <summary>「C#」サブメニューの末尾に、ファイルより広い範囲のまとめて修正を足す。
    /// ファイル単位（<c>gA</c>）はエディタのネイティブ項目が持つので、ここは
    /// プロジェクト／ソリューションだけ。文言もネイティブ側と同じ「〜をまとめて修正」で揃える。</summary>
    private void AddCSharpFixAllMenuItems(System.Windows.Controls.MenuItem root, string filePath)
    {
        if (_solutionModel?.Current is not { State: ProjectLoadState.Ready } model ||
            model.ProjectForFile(filePath) is not { State: ProjectLoadState.Ready } project)
            return;

        if (root.Items.Count > 0)
            root.Items.Add(new System.Windows.Controls.Separator());
        AddScopeItem(root, "プロジェクト全体をまとめて修正", project.FullPath, CSharpFixAllScope.Project);
        if (model.Projects.Count > 1)
            AddScopeItem(root, "ソリューション全体をまとめて修正", project.FullPath, CSharpFixAllScope.Solution);
    }

    private void AddScopeItem(System.Windows.Controls.MenuItem root, string title,
        string projectPath, CSharpFixAllScope scope)
    {
        var item = new System.Windows.Controls.MenuItem { Header = title };
        System.Windows.Automation.AutomationProperties.SetAutomationId(
            item, $"CSharpFixAll.{scope}");
        System.Windows.Automation.AutomationProperties.SetName(item, title);
        item.Click += (_, _) => _ = RunCSharpFixAllAsync(projectPath, scope);
        root.Items.Add(item);
    }

    private async Task RunCSharpFixAllAsync(string projectPath, CSharpFixAllScope scope)
    {
        var model = _solutionModel?.Current;
        if (model is null)
        {
            ShowRefactorStatus("C#プロジェクトがまだ読み込まれていません。");
            return;
        }

        var plan = CSharpFixAllPlanner.Create(model, projectPath, scope);
        if (!plan.IsValid)
        {
            ShowRefactorStatus(plan.Error ?? "Fix Allの対象を決められません。");
            return;
        }

        var files = plan.Files;

        ShowRefactorStatus(scope == CSharpFixAllScope.Solution
            ? $"Fix all (solution): {files.Count} ファイルを解析中…"
            : $"Fix all (project): {files.Count} ファイルを解析中…");
        try
        {
            var openTexts = _editorTabs.Where(tab => tab.IsRealized && tab.Control.FilePath is not null)
                .Select(tab => (Path: Path.GetFullPath(tab.Control.FilePath!), Text: tab.Control.Text))
                .Where(item => files.Contains(item.Path, StringComparer.OrdinalIgnoreCase))
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Text, StringComparer.OrdinalIgnoreCase);
            LspSourceFixAllResult? result = null;
            try
            {
                // 一部のRoslynサーバー／CodeFixProviderはsource.fixAllを受理したまま
                // 応答しないことがある。CSharp DLL側のCodeFixフォールバックを阻害しない。
                var lspCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                // プロバイダーによっては要求の作成前に同期処理を行うため、UIスレッドから
                // 直接呼ばず、タイムアウト判定もUIへ戻れるようにする。
                var lspTask = Task.Run(
                    () => _lspWorkspace.RequestSourceFixAllAsync(files, openTexts, lspCts.Token));
                _ = lspTask.ContinueWith(
                    task =>
                    {
                        _ = task.Exception;
                        lspCts.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                var completed = await Task.WhenAny(lspTask, Task.Delay(TimeSpan.FromSeconds(15)));
                if (completed == lspTask)
                    result = await lspTask;
                else
                {
                    try
                    {
                        lspCts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // timeoutとLSP完了が同時に起きた場合、完了継続処理が先に
                        // CTSを破棄してもCSharp DLL fallbackは継続する。
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // LSP側のキャンセルはCSharp DLL側のフォールバックへ引き継ぐ。
            }
            if (result?.Error is { Length: > 0 } resultError)
            {
                ShowRefactorStatus($"Fix allを統合できませんでした: {resultError}");
                return;
            }
            if (result?.Edit is null)
            {
                // Roslyn LSPはStyleCopのsource.fixAllを返さないことがある。Loomo.CSharpの
                // 公式CodeFixProviderへフォールバックし、同じpreview／一括Undo経路へ乗せる。
                var fallback = await Task.Run(() => CSharpFixAllService.ApplyAsync(
                    model, plan, openTexts));
                if (fallback.Error is { Length: > 0 } fallbackError)
                {
                    ShowRefactorStatus($"Fix allを統合できませんでした: {fallbackError}");
                    return;
                }
                if (fallback.Edit is null)
                {
                    ShowRefactorStatus(
                        $"Fix all: 候補がありません（{result?.DocumentsScanned ?? 0} ファイルを確認）。");
                    return;
                }
                var fallbackErrorText = ApplyLspWorkspaceEdit(
                    fallback.Edit.Changes, null, null,
                    expectedTexts: fallback.ExpectedTexts);
                ShowRefactorStatus(fallbackErrorText is null
                    ? $"Fix all: {fallback.ActionsFound} 件の修正を適用しました。"
                    : $"Fix allを適用できませんでした: {fallbackErrorText}");
                return;
            }

            var error = ApplyLspWorkspaceEdit(
                result.Edit.Changes, result.Edit.DocumentVersions, result.Edit.FileOperations);
            ShowRefactorStatus(error is null
                ? $"Fix all: {result.ActionsFound} 件の修正を適用しました。"
                : $"Fix allを適用できませんでした: {error}");
        }
        catch (OperationCanceledException)
        {
            ShowRefactorStatus("Fix allをキャンセルしました。");
        }
        catch (Exception ex)
        {
            ShowRefactorStatus($"Fix allを適用できませんでした: {ex.Message}");
        }
    }

}
