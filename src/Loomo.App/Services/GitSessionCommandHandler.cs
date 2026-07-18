using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

public sealed record GitOperationStatus(bool IsBusy, bool IsError, string Message);

/// <summary>Git セッションの更新系操作と実行状態を扱う Command Handler。</summary>
public sealed class GitSessionCommandHandler
{
    private readonly GitService _git;
    private int _busy;

    public GitSessionCommandHandler(GitService git) => _git = git;

    public event EventHandler<GitOperationStatus>? StatusChanged;

    public Task<GitCommandResult?> FetchAsync() => RunAsync("フェッチ", _git.FetchAsync);
    public Task<GitCommandResult?> PullAsync() => RunAsync("プル", _git.PullAsync);
    public Task<GitCommandResult?> PushAsync() => RunAsync("プッシュ", _git.PushAsync);

    public Task<GitCommandResult?> CheckoutBranchAsync(GitBranchInfo branch) => branch.IsRemote
        ? RunAsync($"チェックアウト {branch.Name}", () => _git.CheckoutTrackAsync(branch.Name))
        : RunAsync($"チェックアウト {branch.Name}", () => _git.CheckoutAsync(branch.Name));
    public Task<GitCommandResult?> CreateBranchAsync(string name, string? startPoint = null) =>
        RunAsync($"ブランチ作成 {name}", () => _git.CreateBranchAsync(name, startPoint));
    public Task<GitCommandResult?> DeleteBranchAsync(GitBranchInfo branch, bool force) =>
        RunAsync($"ブランチ削除 {branch.Name}", () => _git.DeleteBranchAsync(branch.Name, force));

    public async Task<GitCommandResult?> MergeAsync(
        GitBranchInfo branch, GitMergeStrategy strategy = GitMergeStrategy.Default)
    {
        var label = strategy switch
        {
            GitMergeStrategy.FastForwardOnly => $"{branch.Name} をFast-forwardのみでマージ",
            GitMergeStrategy.NoFastForward => $"{branch.Name} をマージコミットを作成してマージ",
            GitMergeStrategy.Squash => $"{branch.Name} をスカッシュマージ",
            _ => $"{branch.Name} をマージ",
        };
        var result = await RunAsync(label, () => _git.MergeAsync(branch.Name, strategy));
        if (result is { Success: true } && strategy == GitMergeStrategy.Squash)
            StatusChanged?.Invoke(this, new(false, false,
                $"{label}してステージしました。内容を確認してコミットしてください。"));
        return result;
    }

    public Task<GitCommandResult?> RebaseAsync(GitBranchInfo branch) =>
        RunAsync($"{branch.Name} へリベース", () => _git.RebaseAsync(branch.Name));
    public Task<(IReadOnlyList<RebasePlanEntry> Entries, string? Error)> GetRebaseCandidatesAsync(GitLogRow row) =>
        row.Hash is null
            ? Task.FromResult<(IReadOnlyList<RebasePlanEntry>, string?)>((Array.Empty<RebasePlanEntry>(), null))
            : _git.GetRebaseCandidatesAsync(row.Hash);
    public Task<GitCommandResult?> InteractiveRebaseAsync(string fromHash, IReadOnlyList<RebasePlanEntry> plan,
        IReadOnlyDictionary<string, string> messages) =>
        RunAsync("インタラクティブリベース", () => _git.InteractiveRebaseAsync(fromHash, plan, messages));

    public Task<GitCommandResult?> CreateTagAsync(string name, string? target, string? message) =>
        RunAsync($"タグ作成 {name}", () => _git.CreateTagAsync(name, target, message));
    public Task<GitCommandResult?> DeleteTagAsync(GitTagInfo tag) =>
        RunAsync($"タグ削除 {tag.Name}", () => _git.DeleteTagAsync(tag.Name));
    public Task<GitCommandResult?> PushTagAsync(GitTagInfo tag) =>
        RunAsync($"タグ {tag.Name} をプッシュ", () => _git.PushTagAsync(tag.Name));
    public Task<GitCommandResult?> PushAllTagsAsync() => RunAsync("すべてのタグをプッシュ", _git.PushAllTagsAsync);
    public Task<GitCommandResult?> CheckoutTagAsync(GitTagInfo tag) =>
        RunAsync($"チェックアウト {tag.Name}", () => _git.CheckoutCommitAsync(tag.Name));

    public Task<GitCommandResult?> InitSubmoduleAsync(GitSubmoduleInfo submodule) =>
        RunAsync($"サブモジュール初期化 {submodule.Path}", () => _git.SubmoduleInitAsync(submodule.Path));
    public Task<GitCommandResult?> UpdateSubmoduleAsync(GitSubmoduleInfo submodule) =>
        RunAsync($"サブモジュール更新 {submodule.Path}", () => _git.SubmoduleUpdateAsync(submodule.Path));
    public Task<GitCommandResult?> SyncSubmodulesAsync() => RunAsync("サブモジュール同期", _git.SubmoduleSyncAsync);

    public Task<GitCommandResult?> CheckoutCommitAsync(GitLogRow row) => ForCommit(row, "チェックアウト", _git.CheckoutCommitAsync);
    public Task<GitCommandResult?> CherryPickAsync(GitLogRow row) => ForCommit(row, "チェリーピック", _git.CherryPickAsync);
    public Task<GitCommandResult?> RevertAsync(GitLogRow row) => ForCommit(row, "リバート", _git.RevertAsync);
    public Task<GitCommandResult?> ResetAsync(GitLogRow row, GitResetMode mode) => row.Hash is null
        ? Task.FromResult<GitCommandResult?>(null)
        : RunAsync($"リセット（{mode.ToString().ToLowerInvariant()}）{row.ShortHash}",
            () => _git.ResetAsync(row.Hash, mode));
    public Task<string> GetCommitMessageAsync(GitLogRow row) => row.Hash is null
        ? Task.FromResult("") : _git.GetCommitMessageAsync(row.Hash);
    public Task<GitCommandResult?> RewriteCommitMessageAsync(GitLogRow row, string message) => row.Hash is null
        ? Task.FromResult<GitCommandResult?>(null)
        : RunAsync($"コミットメッセージ修正 {row.ShortHash}",
            () => _git.RewriteCommitMessageAsync(row.Hash, message));
    public Task<GitCommandResult?> SquashAsync(IReadOnlyList<GitLogRow> rows, string message)
    {
        var hashes = rows.Where(row => row.Hash is not null).Select(row => row.Hash!).ToList();
        return hashes.Count < 2 ? Task.FromResult<GitCommandResult?>(null)
            : RunAsync($"スカッシュ（{hashes.Count} 件）", () => _git.SquashAsync(hashes, message));
    }

    public Task<GitCommandResult?> ContinueAsync(GitStatusSnapshot status) =>
        status.RebaseInProgress ? RunAsync("リベース続行", _git.RebaseContinueAsync)
        : status.CherryPickInProgress ? RunAsync("チェリーピック続行", _git.CherryPickContinueAsync)
        : status.MergeInProgress ? RunAsync("マージ続行", _git.MergeContinueAsync)
        : Task.FromResult<GitCommandResult?>(null);
    public Task<GitCommandResult?> SkipAsync(GitStatusSnapshot status) =>
        status.RebaseInProgress ? RunAsync("リベーススキップ", _git.RebaseSkipAsync)
        : status.CherryPickInProgress ? RunAsync("チェリーピックスキップ", _git.CherryPickSkipAsync)
        : Task.FromResult<GitCommandResult?>(null);
    public Task<GitCommandResult?> AbortAsync(GitStatusSnapshot status) =>
        status.RebaseInProgress ? RunAsync("リベース中止", _git.RebaseAbortAsync)
        : status.CherryPickInProgress ? RunAsync("チェリーピック中止", _git.CherryPickAbortAsync)
        : status.MergeInProgress ? RunAsync("マージ中止", _git.MergeAbortAsync)
        : Task.FromResult<GitCommandResult?>(null);

    private Task<GitCommandResult?> ForCommit(
        GitLogRow row, string label, Func<string, Task<GitCommandResult>> operation) => row.Hash is null
        ? Task.FromResult<GitCommandResult?>(null)
        : RunAsync($"{label} {row.ShortHash}", () => operation(row.Hash));

    private async Task<GitCommandResult?> RunAsync(string label, Func<Task<GitCommandResult>> operation)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return null;
        StatusChanged?.Invoke(this, new(true, false, $"{label}を実行中…"));
        try
        {
            var result = await operation();
            StatusChanged?.Invoke(this, new(false, !result.Success,
                result.Success ? $"{label}が完了しました。" : Truncate(result.Message)));
            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static string Truncate(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }
}
