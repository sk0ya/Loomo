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

    /// <summary>
    /// スタッシュを差分で見るための親とスタッシュ本体のハッシュ、未追跡のファイル数を引く
    /// （<see cref="GitStashSnapshot"/>）。スタッシュが見つからなければ null。
    /// </summary>
    public async Task<GitStashSnapshot?> ResolveAsync(string stashRef)
    {
        // --verify は 1 つしか取らないので付けない（無い参照は 2 つとも失敗で返る）。
        var hashes = await _runner.RunAsync("rev-parse", $"{stashRef}^1", stashRef)
            .ConfigureAwait(false);
        if (!hashes.Success)
            return null;
        var lines = hashes.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
            return null;

        // 3 つ目の親は -u で退避したときだけある。無いのは正常（rev-parse は失敗で返す）。
        var untracked = 0;
        var files = await _runner.RunAsync("ls-tree", "-r", "--name-only", $"{stashRef}^3")
            .ConfigureAwait(false);
        if (files.Success)
            untracked = files.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        return new GitStashSnapshot(lines[0], lines[1], untracked);
    }

    /// <summary>
    /// スタッシュの中身（未追跡のファイルとバイナリも含む）をパッチとして <paramref name="path"/> に書き出す。
    /// git に直接書かせる（<c>--output</c>）——標準出力を文字列で受けて書き直すと、UTF-8 でない
    /// ファイルの中身が化けて <c>git apply</c> できないパッチになる。
    /// </summary>
    public Task<GitCommandResult> SavePatchAsync(string stashRef, string path) =>
        _runner.RunAsync("stash", "show", "-p", "--binary", "--include-untracked",
            $"--output={path}", stashRef);
}
