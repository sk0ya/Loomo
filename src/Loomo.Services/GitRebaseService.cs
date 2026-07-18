using System;
using System.IO;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>リベース、履歴リセット、履歴書き換えを扱う。</summary>
public sealed class GitRebaseService
{
    private readonly GitCommandRunner _runner;
    private readonly GitMutationExecutor _mutations;

    public GitRebaseService(GitCommandRunner runner, GitMutationExecutor mutations)
    {
        _runner = runner;
        _mutations = mutations;
    }

    public Task<GitCommandResult> RebaseAsync(string onto) =>
        _mutations.ExecuteAsync("rebase", onto);

    public async Task<GitCommandResult> ContinueAsync()
    {
        var result = await _mutations.ExecuteAsync("rebase", "--continue").ConfigureAwait(false);
        if (result.Success)
            await DeleteScriptedArtifactsAsync().ConfigureAwait(false);
        return result;
    }

    public Task<GitCommandResult> SkipAsync() =>
        _mutations.ExecuteAsync("rebase", "--skip");

    public async Task<GitCommandResult> AbortAsync()
    {
        var result = await _mutations.ExecuteAsync("rebase", "--abort").ConfigureAwait(false);
        await DeleteScriptedArtifactsAsync().ConfigureAwait(false);
        return result;
    }

    public Task<GitCommandResult> ResetAsync(string hash, GitResetMode mode) =>
        _mutations.ExecuteAsync("reset", $"--{mode.ToString().ToLowerInvariant()}", hash);

    public async Task<string> GetCommitMessageAsync(string hash)
    {
        var result = await _runner.RunAsync("show", "-s", "--format=%B", hash).ConfigureAwait(false);
        return result.Success ? result.Output.TrimEnd('\r', '\n') : "";
    }

    internal async Task DeleteScriptedArtifactsAsync()
    {
        var gitDirectory = await _runner.GetGitDirectoryAsync().ConfigureAwait(false);
        if (gitDirectory is null) return;
        TryDelete(Path.Combine(gitDirectory, "loomo-squash-message.txt"));
        try
        {
            foreach (var file in Directory.EnumerateFiles(gitDirectory, "loomo-rebase-msg-*.txt"))
                TryDelete(file);
        }
        catch { /* 列挙失敗は無視 */ }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 後始末の失敗は無視 */ }
    }
}
