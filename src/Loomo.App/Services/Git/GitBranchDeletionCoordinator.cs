using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>ブランチ削除と、未マージ時の強制削除確認への切り替えをまとめる。</summary>
internal static class GitBranchDeletionCoordinator
{
    internal static async Task DeleteWithForceFallbackAsync(
        GitSessionCommandHandler commands,
        GitBranchInfo branch,
        Func<string, bool> confirmDelete,
        Func<string, bool> confirmForceDelete)
    {
        if (!confirmDelete(branch.Name)) return;

        var result = await commands.DeleteBranchAsync(branch, force: false);
        if (result is { Success: false } && GitBranchActionPolicy.RequiresForceDelete(result.Message)
            && confirmForceDelete(branch.Name))
            await commands.DeleteBranchAsync(branch, force: true);
    }
}
