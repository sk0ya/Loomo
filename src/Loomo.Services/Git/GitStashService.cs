using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>スタッシュの照会、作成、復元、削除を行う。</summary>
public sealed class GitStashService
{
    private readonly GitCommandRunner _runner;
    private readonly GitMutationExecutor _mutations;

    public GitStashService(GitCommandRunner runner, GitMutationExecutor mutations)
    {
        _runner = runner;
        _mutations = mutations;
    }

    public Task<GitCommandResult> PushAsync(string? message, bool includeUntracked)
    {
        var args = new List<string> { "stash", "push" };
        if (includeUntracked) args.Add("-u");
        if (!string.IsNullOrWhiteSpace(message))
        {
            args.Add("-m");
            args.Add(message.Trim());
        }
        return _mutations.ExecuteAsync(args.ToArray());
    }

    public async Task<IReadOnlyList<GitStashEntry>> GetStashesAsync()
    {
        var result = await _runner.RunAsync("stash", "list", "--format=%gd%x09%gs")
            .ConfigureAwait(false);
        if (!result.Success)
            return Array.Empty<GitStashEntry>();

        var entries = new List<GitStashEntry>();
        foreach (var line in result.Output.Split('\n'))
        {
            var value = line.TrimEnd('\r');
            if (value.Length == 0) continue;
            var tab = value.IndexOf('\t');
            entries.Add(tab < 0
                ? new GitStashEntry(value, "")
                : new GitStashEntry(value[..tab], value[(tab + 1)..]));
        }
        return entries;
    }

    public Task<GitCommandResult> ApplyAsync(string stashRef) =>
        _mutations.ExecuteAsync("stash", "apply", stashRef);

    public Task<GitCommandResult> PopAsync(string stashRef) =>
        _mutations.ExecuteAsync("stash", "pop", stashRef);

    public Task<GitCommandResult> DropAsync(string stashRef) =>
        _mutations.ExecuteAsync("stash", "drop", stashRef);
}
