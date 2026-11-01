using System;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>変更系 git コマンドを実行し、リポジトリ変更と操作結果を通知する。</summary>
public sealed class GitMutationExecutor
{
    private readonly GitCommandRunner _runner;

    public GitMutationExecutor(GitCommandRunner runner) => _runner = runner;

    public event EventHandler? RepositoryChanged;
    public event EventHandler<GitOperationEventArgs>? OperationExecuted;

    public async Task<GitCommandResult> ExecuteAsync(params string[] args)
    {
        GitCommandResult? result = null;
        try
        {
            result = await _runner.RunAsync(args).ConfigureAwait(false);
            // 認証で止まったら、git の文面の前に次の一手を置く（§24.101・§24.99 と同じ形）。
            if (!result.Success && GitRefusal.IsAuthenticationFailure(result.Error))
                result = result with { Error = GitRefusal.AuthenticationFailureMessage + "\n\n" + result.Error.Trim() };
            return result;
        }
        finally
        {
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
            OperationExecuted?.Invoke(this,
                new GitOperationEventArgs(string.Join(' ', args), result?.Success ?? false));
        }
    }

    public void NotifyRepositoryChanged() => RepositoryChanged?.Invoke(this, EventArgs.Empty);
}
