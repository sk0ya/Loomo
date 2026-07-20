using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>リポジトリ初期化、ステージ、変更破棄、コミットを行う。</summary>
public sealed class GitCommitService
{
    private readonly GitRootState _rootState;
    private readonly GitCommandRunner _runner;
    private readonly GitMutationExecutor _mutations;

    public GitCommitService(
        GitRootState rootState, GitCommandRunner runner, GitMutationExecutor mutations)
    {
        _rootState = rootState;
        _runner = runner;
        _mutations = mutations;
    }

    public Task<GitCommandResult> InitializeAsync() => _mutations.ExecuteAsync("init");

    public Task<GitCommandResult> StageAsync(string path) =>
        _mutations.ExecuteAsync("add", "-A", "--", path);

    public Task<GitCommandResult> StageAllAsync() => _mutations.ExecuteAsync("add", "-A");

    public Task<GitCommandResult> UnstageAsync(string path) =>
        _mutations.ExecuteAsync("restore", "--staged", "--", path);

    public Task<GitCommandResult> UnstageAllAsync() =>
        _mutations.ExecuteAsync("restore", "--staged", "--", ".");

    public Task<GitCommandResult> StageAsync(IReadOnlyCollection<string> paths) =>
        paths.Count == 0
            ? Task.FromResult(new GitCommandResult(0, "", ""))
            : _mutations.ExecuteAsync(new[] { "add", "-A", "--" }.Concat(paths).ToArray());

    public Task<GitCommandResult> UnstageAsync(IReadOnlyCollection<string> paths) =>
        paths.Count == 0
            ? Task.FromResult(new GitCommandResult(0, "", ""))
            : _mutations.ExecuteAsync(new[] { "restore", "--staged", "--" }.Concat(paths).ToArray());

    public Task<GitCommandResult> DiscardAsync(GitChangeEntry entry) =>
        entry.IsUntracked
            ? _mutations.ExecuteAsync("clean", "-fd", "--", entry.Path)
            : _mutations.ExecuteAsync("restore", "--", entry.Path);

    public async Task<GitCommandResult> DiscardAsync(IReadOnlyCollection<GitChangeEntry> entries)
    {
        var untracked = entries.Where(entry => entry.IsUntracked).Select(entry => entry.Path).ToArray();
        var tracked = entries.Where(entry => !entry.IsUntracked).Select(entry => entry.Path).ToArray();
        GitCommandResult? last = null;
        if (untracked.Length > 0)
            last = await _mutations.ExecuteAsync(
                new[] { "clean", "-fd", "--" }.Concat(untracked).ToArray()).ConfigureAwait(false);
        if (tracked.Length > 0 && (last is null || last.Success))
            last = await _mutations.ExecuteAsync(
                new[] { "restore", "--" }.Concat(tracked).ToArray()).ConfigureAwait(false);
        return last ?? new GitCommandResult(0, "", "");
    }

    public async Task<GitCommandResult> ApplyReverseDiscardPatchAsync(string patch)
    {
        if (string.IsNullOrEmpty(_rootState.CurrentRoot))
            return new GitCommandResult(-1, "", "ワークスペースフォルダが開かれていません。");

        var temp = Path.Combine(Path.GetTempPath(), $"loomo-discard-{Guid.NewGuid():N}.patch");
        try
        {
            await File.WriteAllTextAsync(
                temp, patch.Replace("\r\n", "\n"), new UTF8Encoding(false)).ConfigureAwait(false);
            return await _runner.RunAsync(
                "apply", "--reverse", "--recount", "--whitespace=nowarn", temp).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* 後始末の失敗は無視 */ }
            _mutations.NotifyRepositoryChanged();
        }
    }

    public Task<GitCommandResult> CommitAsync(string message, bool amend = false, bool sign = false)
    {
        var args = new List<string> { "commit" };
        if (amend) args.Add("--amend");
        if (sign) args.Add("-S");
        args.Add("-m");
        args.Add(message);
        return _mutations.ExecuteAsync(args.ToArray());
    }
}
