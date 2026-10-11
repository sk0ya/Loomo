using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.App.Views;

/// <summary>プロジェクト／ソリューション範囲のコード整理（Rider の Code Cleanup をソリューションに掛ける操作）。
/// 対象ファイルは Fix All と同じ <see cref="CSharpFixAllPlanner"/>（選択中 TFM の Compile 項目）で決め、
/// 全ファイルぶんを 1 つの WorkspaceEdit として既存のトランザクションへ渡す
/// （全件検証してから適用・途中で失敗したら巻き戻し・Undo 1 回で全部戻る）。</summary>
public partial class ShellWindow
{
    private async Task RunCSharpBulkCleanupAsync(string targetPath, CSharpFixAllScope scope)
    {
        var model = _solutionModel?.Current;
        if (model is null)
        {
            ShowRefactorStatus("C#プロジェクトがまだ読み込まれていません。");
            return;
        }

        // ソリューションの行から来たときは、読み込み済みのどれか 1 つを起点にする（Fix All と同じ）。
        var project = scope == CSharpFixAllScope.Project
            ? model.Projects.FirstOrDefault(candidate => string.Equals(
                Path.GetFullPath(candidate.FullPath), targetPath, StringComparison.OrdinalIgnoreCase))
            : model.Projects.FirstOrDefault(candidate => candidate.State == ProjectLoadState.Ready);
        if (project is null)
        {
            ShowRefactorStatus("コード整理の対象のC#プロジェクトが見つかりません。");
            return;
        }

        var plan = CSharpFixAllPlanner.Create(model, project.FullPath, scope);
        if (!plan.IsValid)
        {
            ShowRefactorStatus(plan.Error ?? "コード整理の対象を決められません。");
            return;
        }

        var scopeLabel = scope == CSharpFixAllScope.Solution ? "ソリューション" : "プロジェクト";
        // 開いているタブは未保存の本文が正本（ディスクではない）。
        var openTexts = _editorTabs.Where(tab => tab.IsRealized && tab.Control.FilePath is not null)
            .Select(tab => (Path: Path.GetFullPath(tab.Control.FilePath!), Text: tab.Control.Text))
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Text, StringComparer.OrdinalIgnoreCase);

        ShowRefactorStatus($"コード整理（{scopeLabel}）: {plan.Files.Count} ファイルを整理中…");
        CSharpBulkCleanupResult result;
        try
        {
            result = await Task.Run(() => CSharpBulkCleanup.Run(plan.Files,
                path => openTexts.TryGetValue(path, out var text) ? text : ReadTextOrNull(path),
                CSharpCleanupProfile.Full));
        }
        catch (Exception ex)
        {
            ShowRefactorStatus($"コード整理（{scopeLabel}）に失敗しました: {ex.Message}");
            return;
        }

        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus($"コード整理（{scopeLabel}）: 変更はありません（{result.FilesScanned} ファイルを確認）。");
            return;
        }

        // プレビュー画面は無いので、何ファイル書き換わるかだけは先に見せて確かめる。
        var message = $"{result.FilesChanged} ファイル（{result.ChangedLines} 行）を書き換えます。\n" +
                      "using整理・整形・行末空白・改行を .editorconfig に沿って適用します（未使用usingの削除は Fix All で）。\n" +
                      "Ctrl+Z 1 回で全ファイルを元に戻せます。";
        if (result.GeneratedSkipped > 0) message += $"\n生成コード {result.GeneratedSkipped} ファイルは対象外です。";
        if (MessageBox.Show(this, message, $"コードスタイルを一括適用（{scopeLabel}）",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            ShowRefactorStatus("コード整理を取り消しました。");
            return;
        }

        var outcome = ApplyLspWorkspaceEdit(edit.Changes, edit.DocumentVersions, edit.FileOperations,
            expectedTexts: result.ExpectedTexts);
        var applied = $"コード整理（{scopeLabel}）: {result.FilesChanged} ファイル・{result.ChangedLines} 行を整えました。";
        if (result.Failures.Count > 0) applied += $"（読めなかったファイル {result.Failures.Count}）";
        ShowRefactorStatus(outcome.Error is { } error ? $"コード整理を適用できませんでした: {error}" : applied);
    }

    private static string? ReadTextOrNull(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
