using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Refactoring;
using sk0ya.Loomo.Services.Lsp;
using sk0ya.Loomo.Services.Refactoring;

namespace sk0ya.Loomo.App.Services;

/// <summary>リファクタリングメニューに必要なC#構文情報とLSP候補を並行して取得する。</summary>
internal static class RefactoringRequestController
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    internal static async Task<RefactoringMenuRequest> LoadAsync(
        string? filePath,
        string text,
        int line,
        int column,
        LspRange? selection,
        ILspDocument? document)
    {
        var signatureTask = FindChangeableSignatureAsync(filePath, text, line, column);
        var actionsTask = RequestRefactoringsAsync(document, selection, line, column);
        await Task.WhenAll(signatureTask, actionsTask);
        return new RefactoringMenuRequest(await signatureTask, await actionsTask);
    }

    internal static string NoActionsMessage(
        string? filePath,
        LspManagementService management,
        IReadOnlyList<LspServerRuntimeStatus> serverStatuses)
        => IsLanguageServerReadyFor(filePath, management, serverStatuses) is false
            ? "言語サーバーの準備中です（プロジェクトの読み込みが終わると使えます）"
            : "候補がありません（言語サーバーの解析が終わっていない可能性があります）";

    internal static bool? IsLanguageServerReadyFor(
        string? filePath,
        LspManagementService management,
        IReadOnlyList<LspServerRuntimeStatus> serverStatuses)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        var extension = LspExtensions.NormalizeExt(Path.GetExtension(filePath));
        if (extension.Length == 0 || management.ResolveServerFor(extension) is not { } server)
            return null;

        var executableName = Path.GetFileNameWithoutExtension(server.Executable);
        var statuses = serverStatuses.Where(status => string.Equals(
            Path.GetFileNameWithoutExtension(status.Executable), executableName,
            StringComparison.OrdinalIgnoreCase));
        var matchingStatus = false;
        var ready = false;
        foreach (var status in statuses)
        {
            matchingStatus = true;
            ready |= status.State == LspServerRuntimeState.Ready;
        }
        return matchingStatus ? ready : null;
    }

    internal static async Task<MethodSignature?> FindChangeableSignatureAsync(
        string? filePath, string text, int line, int column)
    {
        if (!CSharpSignatureRefactoring.AppliesTo(filePath)) return null;
        try
        {
            var target = await Task.Run(() => CSharpSignatureSyntax.Read(
                filePath!, LspUri.FromPath(Path.GetFullPath(filePath!)), text, line, column));
            return target.Signature;
        }
        catch { return null; }
    }

    private static async Task<IReadOnlyList<LspCodeAction>> RequestRefactoringsAsync(
        ILspDocument? document, LspRange? selection, int line, int column)
    {
        if (document is not { IsConnected: true }) return [];
        var range = selection ?? new LspRange(new LspPosition(line, column), new LspPosition(line, column));
        using var cts = new CancellationTokenSource(RequestTimeout);
        try { return await document.RequestCodeActionsAsync(range, RefactoringMenu.RequestKinds, cts.Token); }
        catch { return []; }
    }
}

internal sealed record RefactoringMenuRequest(
    MethodSignature? Signature,
    IReadOnlyList<LspCodeAction> Actions);
