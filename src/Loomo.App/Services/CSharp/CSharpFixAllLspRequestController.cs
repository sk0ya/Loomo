using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.App.Services;

/// <summary>応答しないことがあるLSPのsource.fixAll要求を制限時間付きで待つ。</summary>
internal static class CSharpFixAllLspRequestController
{
    internal static async Task<CSharpFixAllExecutionResult> ExecuteAsync(
        SolutionModel solution,
        CSharpFixAllPlan plan,
        IReadOnlyDictionary<string, string> openTexts,
        TimeSpan timeout,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string>, CancellationToken,
            Task<LspSourceFixAllResult>> request)
    {
        LspSourceFixAllResult? lspResult = null;
        try
        {
            lspResult = await RequestWithTimeoutAsync(plan.Files, openTexts, timeout, request);
        }
        catch (OperationCanceledException)
        {
            // LSP側のキャンセルはCSharp DLL側のフォールバックへ引き継ぐ。
        }

        if (lspResult?.Error is { Length: > 0 } lspError)
            return new(null, lspResult.DocumentsScanned, lspResult.ActionsFound, lspError);
        if (lspResult?.Edit is not null)
            return new(lspResult.Edit, lspResult.DocumentsScanned, lspResult.ActionsFound);

        // Roslyn LSPはStyleCopのsource.fixAllを返さないことがある。Loomo.CSharpの
        // 公式CodeFixProviderへフォールバックし、結果は共通WorkspaceEdit適用経路へ渡す。
        var fallback = await Task.Run(() => CSharpFixAllService.ApplyAsync(
            solution, plan, openTexts));
        if (fallback.Error is { Length: > 0 } fallbackError)
            return new(null, fallback.DocumentsScanned, fallback.ActionsFound, fallbackError);

        return new(fallback.Edit, lspResult?.DocumentsScanned ?? 0, fallback.ActionsFound);
    }

    internal static async Task<LspSourceFixAllResult?> RequestWithTimeoutAsync(
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, string> openTexts,
        TimeSpan timeout,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string>, CancellationToken,
            Task<LspSourceFixAllResult>> request)
    {
        var cancellation = new CancellationTokenSource(timeout);
        var requestTask = Task.Run(() => request(files, openTexts, cancellation.Token));
        _ = requestTask.ContinueWith(
            task =>
            {
                _ = task.Exception;
                cancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        if (await Task.WhenAny(requestTask, Task.Delay(timeout)) == requestTask)
            return await requestTask;

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // timeoutと完了が重なり、完了継続処理が先にCTSを破棄した場合もフォールバックを続ける。
        }
        return null;
    }
}

internal sealed record CSharpFixAllExecutionResult(
    LspWorkspaceEdit? Edit,
    int DocumentsScanned,
    int ActionsFound,
    string? Error = null);
