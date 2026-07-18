using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

    public async Task<GitCommandResult> RewriteCommitMessageAsync(string hash, string message)
    {
        try
        {
            return await RewriteCommitMessageCoreAsync(hash, message).ConfigureAwait(false);
        }
        finally
        {
            _mutations.NotifyRepositoryChanged();
        }
    }

    private async Task<GitCommandResult> RewriteCommitMessageCoreAsync(string hash, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new GitCommandResult(-1, "", "コミットメッセージを入力してください。");

        var onHead = await _runner.RunAsync("merge-base", "--is-ancestor", hash, "HEAD")
            .ConfigureAwait(false);
        if (!onHead.Success)
            return new GitCommandResult(-1, "", "現在のブランチに含まれるコミットのみ修正できます。");

        var hasParent = (await _runner.RunAsync(
            "rev-parse", "--verify", "--quiet", $"{hash}^").ConfigureAwait(false)).Success;
        var range = hasParent ? $"{hash}^..HEAD" : "HEAD";
        var chainResult = await _runner.RunAsync(
            "rev-list", "--reverse", "--first-parent", range).ConfigureAwait(false);
        if (!chainResult.Success) return chainResult;
        var chain = SplitLines(chainResult.Output);
        if (chain.Count == 0 || !string.Equals(chain[0], hash, StringComparison.OrdinalIgnoreCase))
            return new GitCommandResult(-1, "", "現在のブランチの主系列にあるコミットのみ修正できます。");

        var merges = await _runner.RunAsync("rev-list", "--min-parents=2", range)
            .ConfigureAwait(false);
        if (!merges.Success) return merges;
        if (SplitLines(merges.Output).Count > 0)
            return new GitCommandResult(-1, "", "対象から HEAD までにマージコミットがあるため、メッセージを修正できません。");

        var todo = new StringBuilder().Append("reword ").Append(chain[0]).Append('\n');
        foreach (var commit in chain.Skip(1))
            todo.Append("pick ").Append(commit).Append('\n');

        var gitDirectory = await _runner.GetGitDirectoryAsync().ConfigureAwait(false);
        if (gitDirectory is null)
            return new GitCommandResult(-1, "", "git ディレクトリを特定できませんでした。");
        var todoPath = Path.Combine(gitDirectory, "loomo-reword-todo.txt");
        var messagePath = Path.Combine(gitDirectory, "loomo-reword-message.txt");
        try
        {
            await File.WriteAllTextAsync(todoPath, todo.ToString()).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                messagePath, message.TrimEnd() + Environment.NewLine).ConfigureAwait(false);
            var environment = new Dictionary<string, string>
            {
                ["GIT_SEQUENCE_EDITOR"] = $"cp '{ToMsysPath(todoPath)}'",
                ["GIT_EDITOR"] = $"cp '{ToMsysPath(messagePath)}'",
            };
            return await _runner.RunAsync(
                environment, "rebase", "-i", hasParent ? $"{hash}^" : "--root").ConfigureAwait(false);
        }
        finally
        {
            TryDelete(todoPath);
            TryDelete(messagePath);
        }
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

    private static List<string> SplitLines(string value) => value
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .ToList();

    private static string ToMsysPath(string path)
    {
        var value = path.Replace('\\', '/');
        if (value.Length >= 2 && value[1] == ':')
            value = "/" + char.ToLowerInvariant(value[0]) + value[2..];
        return value;
    }
}
