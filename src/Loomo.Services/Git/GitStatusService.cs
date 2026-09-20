using System.IO;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>リポジトリの作業ツリー状態と進行中操作を照会する。</summary>
public sealed class GitStatusService
{
    private readonly GitCommandRunner _runner;
    private readonly GitRootState _rootState;

    public GitStatusService(GitCommandRunner runner, GitRootState rootState)
    {
        _runner = runner;
        _rootState = rootState;
    }

    /// <summary>対象フォルダー（か、その親）に .git があるか。git が失敗した理由が
    /// 「リポジトリではない」なのか「一時的に読めなかった」なのかを、git の出力メッセージ
    /// （環境によっては訳される）ではなくファイルシステムで見分けるための判定。</summary>
    private static bool HasGitDirectory(string? root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        try
        {
            var directory = new DirectoryInfo(root);
            while (directory is not null)
            {
                // ワークツリーやサブモジュールでは .git がファイル（gitdir: 参照）になる。
                var marker = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(marker) || File.Exists(marker)) return true;
                directory = directory.Parent;
            }
        }
        catch { /* 走査できなければ「リポジトリではない」側に倒す（従来の挙動） */ }
        return false;
    }

    public async Task<GitStatusSnapshot> GetStatusAsync()
    {
        var result = await _runner.RunAsync(
            "--no-optional-locks", "status", "--porcelain=v2", "--branch").ConfigureAwait(false);
        if (!result.Success)
            // 「リポジトリではない」と「今は読めない」を混ぜない。混ぜると、rebase 中などの
            // 一度の失敗が「リポジトリではない」として覚え込まれ、Git パネルが空のまま戻らない。
            return new GitStatusSnapshot
            {
                IsRepository = false,
                QueryFailed = HasGitDirectory(_rootState.CurrentRoot),
            };

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
