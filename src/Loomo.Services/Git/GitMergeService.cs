using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>マージ、チェリーピック、リバート操作を実行する。</summary>
public sealed class GitMergeService
{
    private readonly GitMutationExecutor _mutations;

    public GitMergeService(GitMutationExecutor mutations) => _mutations = mutations;

    public Task<GitCommandResult> MergeAsync(
        string branch, GitMergeStrategy strategy = GitMergeStrategy.Default) =>
        strategy switch
        {
            GitMergeStrategy.FastForwardOnly => _mutations.ExecuteAsync("merge", "--ff-only", branch),
            GitMergeStrategy.NoFastForward => _mutations.ExecuteAsync("merge", "--no-ff", "--no-edit", branch),
            GitMergeStrategy.Squash => _mutations.ExecuteAsync("merge", "--squash", branch),
            _ => _mutations.ExecuteAsync("merge", "--no-edit", branch),
        };

    public Task<GitCommandResult> ContinueMergeAsync() =>
        _mutations.ExecuteAsync("merge", "--continue");

    public Task<GitCommandResult> AbortMergeAsync() =>
        _mutations.ExecuteAsync("merge", "--abort");

    public Task<GitCommandResult> CherryPickAsync(string hash) =>
        _mutations.ExecuteAsync("cherry-pick", hash);

    /// <summary>コミットを作らずに変更だけ取り込む（<c>--no-commit</c>）。
    /// 取り込んだ内容をそのまま出すのではなく<b>手直ししてから</b>コミットしたいときの経路で、
    /// 結果はステージされた状態で作業ツリーに残る。</summary>
    public Task<GitCommandResult> CherryPickNoCommitAsync(string hash) =>
        _mutations.ExecuteAsync("cherry-pick", "--no-commit", hash);

    public Task<GitCommandResult> ContinueCherryPickAsync() =>
        _mutations.ExecuteAsync("cherry-pick", "--continue");

    public Task<GitCommandResult> SkipCherryPickAsync() =>
        _mutations.ExecuteAsync("cherry-pick", "--skip");

    public Task<GitCommandResult> AbortCherryPickAsync() =>
        _mutations.ExecuteAsync("cherry-pick", "--abort");

    public Task<GitCommandResult> RevertAsync(string hash) =>
        _mutations.ExecuteAsync("revert", "--no-edit", hash);

    /// <summary>打ち消しコミットを作らずに、打ち消す変更だけ作業ツリーへ入れる（<c>--no-commit</c>）。</summary>
    public Task<GitCommandResult> RevertNoCommitAsync(string hash) =>
        _mutations.ExecuteAsync("revert", "--no-commit", hash);
}
