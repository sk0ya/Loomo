using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.App.Services;

/// <summary>アクティブファイルに対するLSPの案内と実行失敗をまとめて解決する。</summary>
internal sealed class EditorLspNoticeCoordinator(
    LspManagementService management,
    LspWorkspaceService workspace,
    LspPromptViewModel promptViewModel)
{
    private string? _lastEvaluatedPath;
    private LspPromptInfo? _lastPrompt;
    private bool _hasEvaluatedPath = true;

    /// <summary>同じファイルの切替とアウトライン更新で案内を二重評価しない。</summary>
    private LspPromptInfo? ActivePromptFor(string? filePath)
    {
        _lastEvaluatedPath = filePath;
        _hasEvaluatedPath = true;
        _lastPrompt = management.EvaluateForFile(filePath);
        return _lastPrompt;
    }

    public void ShowForFile(string? filePath)
    {
        if (_hasEvaluatedPath &&
            string.Equals(filePath, _lastEvaluatedPath, StringComparison.OrdinalIgnoreCase))
            return;

        promptViewModel.Show(ActivePromptFor(filePath));
    }

    public LspPromptInfo? EvaluatePrompt(string? filePath)
    {
        var info = _hasEvaluatedPath &&
            string.Equals(filePath, _lastEvaluatedPath, StringComparison.OrdinalIgnoreCase)
                ? _lastPrompt
                : management.EvaluateForFile(filePath);
        return promptViewModel.Filter(info);
    }

    public LspServerFailure? EvaluateFailure(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return null;
        var extension = LspExtensions.NormalizeExt(Path.GetExtension(filePath));
        if (extension.Length == 0 || management.ResolveServerFor(extension) is not { } server)
            return null;

        return LspNoticeModel.FindFailure(
            workspace.ServerStatuses, extension, server.Executable, server.DisplayName);
    }
}
