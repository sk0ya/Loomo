using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

public sealed record DiffCommandResult(bool Success, string Message);

/// <summary>Diff の巻き戻し、破棄、記録クリアを実行する Command Handler。</summary>
public sealed class DiffSessionCommandHandler
{
    private readonly DiffFileGateway _files;
    private readonly IFileChangeJournal _journal;
    private readonly GitService _git;

    public DiffSessionCommandHandler(DiffFileGateway files, IFileChangeJournal journal, GitService git)
    {
        _files = files;
        _journal = journal;
        _git = git;
    }

    public async Task<DiffCommandResult> RevertAiAsync(DiffFileItem item)
    {
        try
        {
            if (item.IsNew) _files.DeleteIfExists(item.FullPath);
            else await _files.WriteTextAsync(item.FullPath, item.OldContent!);
            _journal.RemoveForPath(item.FullPath);
            return new(true, $"{item.DisplayPath} を元に戻しました。");
        }
        catch (Exception ex) { return new(false, $"巻き戻しに失敗しました: {ex.Message}"); }
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

    public void ClearAiChanges() => _journal.Clear();

    private static string Truncate(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }
}
