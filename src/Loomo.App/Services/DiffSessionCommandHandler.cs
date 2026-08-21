using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

public sealed record DiffCommandResult(bool Success, string Message);

/// <summary>Diff の破棄を実行する Command Handler。</summary>
public sealed class DiffSessionCommandHandler
{
    private readonly GitService _git;

    public DiffSessionCommandHandler(GitService git)
    {
        _git = git;
    }

    public async Task<DiffCommandResult> DiscardAsync(DiffFileItem item)
    {
        var result = await _git.DiscardAsync(item.Entry!);
        return result.Success ? new(true, $"{item.DisplayPath} の変更を破棄しました。")
            : new(false, $"破棄に失敗しました: {Truncate(result.Message)}");
    }

    public async Task<DiffCommandResult> ApplyReverseAsync(string patch, string successMessage)
    {
        var result = await _git.ApplyReverseDiscardPatchAsync(patch);
        return result.Success ? new(true, successMessage)
            : new(false, $"選択行の破棄に失敗しました: {Truncate(result.Message)}");
    }

    private static string Truncate(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }
}
