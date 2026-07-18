using System.IO;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>リポジトリの作業ツリー状態と進行中操作を照会する。</summary>
public sealed class GitStatusService
{
    private readonly GitCommandRunner _runner;

    public GitStatusService(GitCommandRunner runner)
    {
        _runner = runner;
    }

    public async Task<GitStatusSnapshot> GetStatusAsync()
    {
        var result = await _runner.RunAsync(
            "--no-optional-locks", "status", "--porcelain=v2", "--branch").ConfigureAwait(false);
        if (!result.Success)
            return new GitStatusSnapshot { IsRepository = false };

        var snapshot = GitStatusParser.Parse(result.Output);
        var gitDir = await _runner.GetGitDirectoryAsync().ConfigureAwait(false);
        if (gitDir is null)
            return snapshot;

        return snapshot with
        {
            RebaseInProgress = Directory.Exists(Path.Combine(gitDir, "rebase-merge"))
                || Directory.Exists(Path.Combine(gitDir, "rebase-apply")),
            MergeInProgress = File.Exists(Path.Combine(gitDir, "MERGE_HEAD")),
            CherryPickInProgress = File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")),
        };
    }

}
