using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>サブモジュールの照会、初期化、更新、同期を行う。</summary>
public sealed class GitSubmoduleService
{
    private readonly GitCommandRunner _runner;
    private readonly GitMutationExecutor _mutations;

    public GitSubmoduleService(GitCommandRunner runner, GitMutationExecutor mutations)
    {
        _runner = runner;
        _mutations = mutations;
    }

    public async Task<IReadOnlyList<GitSubmoduleInfo>> GetSubmodulesAsync()
    {
        var result = await _runner.RunAsync("submodule", "status").ConfigureAwait(false);
        return result.Success
            ? GitSubmoduleParser.Parse(result.Output)
            : Array.Empty<GitSubmoduleInfo>();
    }

    public Task<GitCommandResult> InitializeAsync(string? path = null) =>
        string.IsNullOrEmpty(path)
            ? _mutations.ExecuteAsync("submodule", "init")
            : _mutations.ExecuteAsync("submodule", "init", "--", path);

    public Task<GitCommandResult> UpdateAsync(
        string? path = null, bool initialize = true, bool recursive = true)
    {
        var args = new List<string> { "submodule", "update" };
        if (initialize) args.Add("--init");
        if (recursive) args.Add("--recursive");
        if (!string.IsNullOrEmpty(path))
        {
            args.Add("--");
            args.Add(path);
        }
        return _mutations.ExecuteAsync(args.ToArray());
    }

    public Task<GitCommandResult> SynchronizeAsync() =>
        _mutations.ExecuteAsync("submodule", "sync", "--recursive");
}
