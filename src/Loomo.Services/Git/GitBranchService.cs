using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>リモート、ブランチ、タグの参照情報を照会する。</summary>
public sealed class GitBranchService
{
    private readonly GitCommandRunner _runner;
    private readonly GitMutationExecutor _mutations;

    public GitBranchService(GitCommandRunner runner, GitMutationExecutor mutations)
    {
        _runner = runner;
        _mutations = mutations;
    }

    public async Task<IReadOnlyList<string>> GetRemotesAsync()
    {
        var result = await _runner.RunAsync("remote").ConfigureAwait(false);
        return result.Success
            ? result.Output.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList()
            : Array.Empty<string>();
    }

    /// <summary>リモート名とfetch URLを取得する。push URLではなくリポジトリの正規URLを使う。</summary>
    public async Task<IReadOnlyList<GitRemoteInfo>> GetRemoteUrlsAsync()
    {
        var result = await _runner.RunAsync("remote", "-v").ConfigureAwait(false);
        if (!result.Success)
            return Array.Empty<GitRemoteInfo>();

        var remotes = new List<GitRemoteInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in result.Output.Split('\n'))
        {
            var value = line.TrimEnd('\r');
            var parts = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !value.EndsWith("(fetch)", StringComparison.Ordinal))
                continue;

            var url = parts[1].Trim();
            if (url.Length == 0 || !seen.Add(parts[0]))
                continue;
            remotes.Add(new GitRemoteInfo(parts[0], url));
        }
        return remotes;
    }

    public async Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync()
    {
        var result = await _runner.RunAsync(
            "branch", "-a",
            "--format=%(refname)\t%(HEAD)\t%(upstream:short)\t%(upstream:track)\t%(committerdate:iso-strict)")
            .ConfigureAwait(false);
        if (!result.Success)
            return Array.Empty<GitBranchInfo>();

        var branches = new List<GitBranchInfo>();
        foreach (var line in result.Output.Split('\n'))
        {
            var value = line.TrimEnd('\r');
            if (value.Length == 0) continue;
            var parts = value.Split('\t');
            if (parts.Length < 2) continue;

            var refName = parts[0];
            var upstream = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;
            var (ahead, behind, gone) = ParseTrack(parts.Length > 3 ? parts[3] : "");
            var lastCommit = ParseDate(parts.Length > 4 ? parts[4] : "");
            if (refName.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                branches.Add(new GitBranchInfo(
                    refName["refs/heads/".Length..], parts[1] == "*", IsRemote: false, upstream)
                {
                    Ahead = ahead,
                    Behind = behind,
                    UpstreamGone = gone,
                    LastCommit = lastCommit,
                });
            }
            else if (refName.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                var name = refName["refs/remotes/".Length..];
                if (name.EndsWith("/HEAD", StringComparison.Ordinal)) continue;
                branches.Add(new GitBranchInfo(name, IsCurrent: false, IsRemote: true, upstream)
                {
                    LastCommit = lastCommit,
                });
            }
        }
        return branches;
    }

    public async Task<IReadOnlyList<GitTagInfo>> GetTagsAsync()
    {
        var result = await _runner.RunAsync("for-each-ref", "refs/tags", "--sort=-creatordate",
            "--format=%(refname:short)\t%(objecttype)\t%(objectname:short)\t%(*objectname:short)\t%(subject)\t%(creatordate:format:%Y-%m-%d %H:%M)")
            .ConfigureAwait(false);
        if (!result.Success)
            return Array.Empty<GitTagInfo>();

        var tags = new List<GitTagInfo>();
        foreach (var line in result.Output.Split('\n'))
        {
            var value = line.TrimEnd('\r');
            if (value.Length == 0) continue;
            var parts = value.Split('\t');
            if (parts.Length < 6) continue;
            var isAnnotated = parts[1] == "tag";
            var target = isAnnotated && parts[3].Length > 0 ? parts[3] : parts[2];
            tags.Add(new GitTagInfo(parts[0], target,
                parts[4].Length > 0 ? parts[4] : null,
                isAnnotated,
                parts[5].Length > 0 ? parts[5] : null));
        }
        return tags;
    }

    public Task<GitCommandResult> FetchAsync() =>
        _mutations.ExecuteAsync("fetch", "--all", "--prune");

    public Task<GitCommandResult> PullAsync() => PullAsync(GitPullMode.Merge);

    /// <summary>取り込み方を指定してプルする。</summary>
    public Task<GitCommandResult> PullAsync(GitPullMode mode) => mode switch
    {
        GitPullMode.Rebase => _mutations.ExecuteAsync("pull", "--rebase"),
        GitPullMode.FastForwardOnly => _mutations.ExecuteAsync("pull", "--ff-only"),
        _ => _mutations.ExecuteAsync("pull"),
    };

    /// <summary>
    /// 指定したローカルブランチを上流へ同期する。現在ブランチは通常の pull を使い、
    /// それ以外は作業ツリーを切り替えずに fetch の refspec で fast-forward 同期する。
    /// </summary>
    public async Task<GitCommandResult> PullBranchAsync(GitBranchInfo branch)
    {
        if (branch.IsRemote)
            return await FailedBranchOperation("リモートブランチはプルの対象にできません。").ConfigureAwait(false);
        if (branch.IsCurrent)
            return await PullAsync().ConfigureAwait(false);
        var upstream = await ResolveUpstreamAsync(branch).ConfigureAwait(false);
        if (upstream is null)
            return await FailedBranchOperation($"ブランチ {branch.Name} には上流が設定されていません。").ConfigureAwait(false);

        var (remote, remoteBranch) = upstream.Value;

        // 非チェックアウトブランチは git pull（=現在のHEADへのmerge）を使えないため、
        // 上流追跡先とローカルブランチを同時に更新する。分岐している場合は fetch が拒否するので、
        // 未確認の上書きや作業ツリー変更は発生しない。
        return remote == "."
            ? await _mutations.ExecuteAsync("fetch", ".",
                $"refs/heads/{remoteBranch}:refs/heads/{branch.Name}").ConfigureAwait(false)
            : await _mutations.ExecuteAsync("fetch", remote,
                $"refs/heads/{remoteBranch}:refs/remotes/{remote}/{remoteBranch}",
                $"refs/heads/{remoteBranch}:refs/heads/{branch.Name}").ConfigureAwait(false);
    }

    public Task<GitCommandResult> PushAsync() => PushCurrentAsync(null, force: false);

    /// <summary>
    /// 現在ブランチをプッシュする。<paramref name="force"/> は <c>--force</c> ではなく
    /// <b><c>--force-with-lease</c></b>——素の <c>--force</c> は「自分が最後に見た後に誰かが積んだコミット」を
    /// 黙って消すのに対し、lease は追跡している ref が自分の知っている位置から動いていたら拒否する。
    /// rebase・amend・squash の後に押す操作なので、既定でこの安全側を使う。
    /// （<c>--force-if-includes</c> は更に安全だが git 2.30 以降でしか無いので付けない。）
    /// </summary>
    public Task<GitCommandResult> PushAsync(bool force) => PushCurrentAsync(null, force);

    private async Task<GitCommandResult> PushCurrentAsync(string? defaultRemote, bool force)
    {
        var result = await _mutations.ExecuteAsync(WithLease(force, "push")).ConfigureAwait(false);
        if (!result.Success && result.Error.Contains("no upstream", StringComparison.OrdinalIgnoreCase))
        {
            // 上流が無い＝たいていは「まだリモートに無いブランチ」だが、上流を解除しただけで
            // リモート側は残っていることもある（この機能自身が「上流を解除」を持っている）。
            // だから force はここでも<b>落とさない</b>——落とすと、強制プッシュを承認した人に
            // 「非早送りで拒否されました」を返すことになり、確認ダイアログの約束と食い違う。
            result = await _mutations.ExecuteAsync(WithLease(force, "push", "-u",
                string.IsNullOrWhiteSpace(defaultRemote) ? "origin" : defaultRemote, "HEAD"))
                .ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>指定したローカルブランチを、その上流または既定リモートへプッシュする。</summary>
    public Task<GitCommandResult> PushBranchAsync(GitBranchInfo branch, string? defaultRemote) =>
        PushBranchAsync(branch, defaultRemote, force: false);

    /// <summary>指定したローカルブランチをプッシュする（<paramref name="force"/> は
    /// <see cref="PushAsync(bool)"/> と同じく <c>--force-with-lease</c>）。</summary>
    public async Task<GitCommandResult> PushBranchAsync(
        GitBranchInfo branch, string? defaultRemote, bool force)
    {
        if (branch.IsRemote)
            return await FailedBranchOperation("リモートブランチはプッシュの対象にできません。").ConfigureAwait(false);
        if (branch.IsCurrent)
            return await PushCurrentAsync(defaultRemote, force).ConfigureAwait(false);

        var upstream = await ResolveUpstreamAsync(branch).ConfigureAwait(false);
        if (upstream is { } target)
            return await _mutations.ExecuteAsync(WithLease(force, "push", target.Remote,
                $"{branch.Name}:refs/heads/{target.Branch}")).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(defaultRemote))
            return await _mutations
                .ExecuteAsync(WithLease(force, "push", "-u", defaultRemote, branch.Name))
                .ConfigureAwait(false);
        return await FailedBranchOperation($"ブランチ {branch.Name} のプッシュ先リモートがありません。").ConfigureAwait(false);
    }

    /// <summary>
    /// <c>--force-with-lease</c> を <c>push</c> の直後（refspec より前）へ差し込む。
    /// git はオプションの位置に寛容だが、refspec の後ろに置くと読みにくいので組み立てを1箇所に寄せる。
    /// </summary>
    private static string[] WithLease(bool force, params string[] args)
    {
        if (!force) return args;
        var list = new List<string>(args.Length + 1);
        list.Add(args[0]);
        list.Add("--force-with-lease");
        list.AddRange(args.Skip(1));
        return list.ToArray();
    }

    /// <summary>
    /// リモート上のブランチを削除する（<c>git push &lt;remote&gt; --delete &lt;branch&gt;</c>）。
    /// <paramref name="remoteBranch"/> は一覧に出ている <c>origin/foo</c> 形式。
    /// <c>git branch -d</c> ではリモート追跡の参照が消えるだけでリモート側は残るので、別操作にしてある。
    /// </summary>
    public async Task<GitCommandResult> DeleteRemoteBranchAsync(string remoteBranch)
    {
        var remotes = await GetRemotesAsync().ConfigureAwait(false);
        if (GitRemoteRef.TrySplit(remoteBranch, remotes) is not { } target)
            return await FailedBranchOperation(
                $"{remoteBranch} のリモートを特定できませんでした。").ConfigureAwait(false);
        return await _mutations.ExecuteAsync("push", target.Remote, "--delete", target.Branch)
            .ConfigureAwait(false);
    }

    /// <summary>ローカルブランチの上流を設定する（<paramref name="upstream"/> は <c>origin/main</c> 形式）。</summary>
    public Task<GitCommandResult> SetUpstreamAsync(string branch, string upstream) =>
        _mutations.ExecuteAsync("branch", $"--set-upstream-to={upstream}", branch);

    /// <summary>ローカルブランチの上流を解除する。</summary>
    public Task<GitCommandResult> UnsetUpstreamAsync(string branch) =>
        _mutations.ExecuteAsync("branch", "--unset-upstream", branch);

    public Task<GitCommandResult> AddRemoteAsync(string name, string url) =>
        _mutations.ExecuteAsync("remote", "add", name, url);

    public Task<GitCommandResult> SetRemoteUrlAsync(string name, string url) =>
        _mutations.ExecuteAsync("remote", "set-url", name, url);

    /// <summary>リモートを削除する（追跡ブランチと <c>branch.*.remote</c> の設定も git が消す）。</summary>
    public Task<GitCommandResult> RemoveRemoteAsync(string name) =>
        _mutations.ExecuteAsync("remote", "remove", name);

    /// <summary>
    /// 上流の追跡先（リモートとブランチ名）を求める。<b>正本は <c>branch.&lt;name&gt;.remote</c> ／
    /// <c>branch.&lt;name&gt;.merge</c></b>で、git 自身が持っている分解済みの値をそのまま使う。
    /// </summary>
    private async Task<(string Remote, string Branch)?> ResolveUpstreamAsync(GitBranchInfo branch)
    {
        var remote = await ConfigValueAsync($"branch.{branch.Name}.remote").ConfigureAwait(false);
        var merge = await ConfigValueAsync($"branch.{branch.Name}.merge").ConfigureAwait(false);
        if (remote is not null && merge is not null)
        {
            var name = merge.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? merge["refs/heads/".Length..]
                : merge;
            if (name.Length > 0)
                return (remote, name);
        }

        return await ResolveUpstreamByNameAsync(branch.Upstream).ConfigureAwait(false);
    }

    /// <summary>
    /// 設定を引けなかったときの後詰め。upstream:short はリモート名を含む場合（例 team/foo/main）と、
    /// ローカル上流の場合（例 main）がある。登録済みリモート名との最長一致で前者を判定し、
    /// 残りをブランチ名として返す。
    /// </summary>
    private async Task<(string Remote, string Branch)?> ResolveUpstreamByNameAsync(string? upstream)
    {
        if (string.IsNullOrWhiteSpace(upstream))
            return null;

        var remotes = await GetRemotesAsync().ConfigureAwait(false);
        if (GitRemoteRef.TrySplit(upstream, remotes) is { } split)
            return split;

        // リモート名に一致しない＝ローカル上流のはず。ただしここには「git remote が失敗して一覧が
        // 空だった」場合も落ちてくるので、ローカルに同名ブランチが実在するときだけ "." を返す。
        // 取り違えると fetch は refs/heads/origin/main を探しに行き、push は「origin/main」という
        // 名前のローカルブランチを新しく作ってしまう。
        return await LocalBranchExistsAsync(upstream).ConfigureAwait(false)
            ? (".", upstream)
            : null;
    }

    private async Task<string?> ConfigValueAsync(string key)
    {
        var result = await _runner.RunAsync("config", "--get", key).ConfigureAwait(false);
        if (!result.Success)
            return null;
        var value = result.Output.Trim();
        return value.Length == 0 ? null : value;
    }

    private async Task<bool> LocalBranchExistsAsync(string branch)
    {
        var result = await _runner
            .RunAsync("rev-parse", "--verify", "--quiet", $"refs/heads/{branch}")
            .ConfigureAwait(false);
        return result.Success && result.Output.Trim().Length > 0;
    }

    private static Task<GitCommandResult> FailedBranchOperation(string message) =>
        Task.FromResult(new GitCommandResult(-1, "", message));

    public Task<GitCommandResult> CheckoutAsync(string branch) =>
        _mutations.ExecuteAsync("checkout", branch);

    public Task<GitCommandResult> CheckoutTrackAsync(string remoteBranch) =>
        _mutations.ExecuteAsync("checkout", "--track", remoteBranch);

    public Task<GitCommandResult> CheckoutCommitAsync(string hash) =>
        _mutations.ExecuteAsync("checkout", "--detach", hash);

    public Task<GitCommandResult> CreateBranchAsync(string name, string? startPoint = null) =>
        startPoint is null
            ? _mutations.ExecuteAsync("switch", "-c", name)
            : _mutations.ExecuteAsync("switch", "-c", name, startPoint);

    public Task<GitCommandResult> DeleteBranchAsync(string name, bool force = false) =>
        _mutations.ExecuteAsync("branch", force ? "-D" : "-d", name);

    public Task<GitCommandResult> CreateTagAsync(
        string name, string? target = null, string? message = null)
    {
        var args = new List<string> { "tag" };
        if (!string.IsNullOrWhiteSpace(message))
        {
            args.Add("-a");
            args.Add(name);
            args.Add("-m");
            args.Add(message.Trim());
        }
        else
        {
            args.Add(name);
        }
        if (target is not null)
            args.Add(target);
        return _mutations.ExecuteAsync(args.ToArray());
    }

    public Task<GitCommandResult> DeleteTagAsync(string name) =>
        _mutations.ExecuteAsync("tag", "-d", name);

    public Task<GitCommandResult> PushTagAsync(string name) =>
        _mutations.ExecuteAsync("push", "origin", name);

    public Task<GitCommandResult> PushAllTagsAsync() =>
        _mutations.ExecuteAsync("push", "--tags");

    internal static (int Ahead, int Behind, bool Gone) ParseTrack(string track)
    {
        if (track.Length == 0) return (0, 0, false);
        if (track.Contains("gone", StringComparison.Ordinal)) return (0, 0, true);
        return (ReadCount(track, "ahead "), ReadCount(track, "behind "), false);

        static int ReadCount(string value, string keyword)
        {
            var at = value.IndexOf(keyword, StringComparison.Ordinal);
            if (at < 0) return 0;
            var digits = value[(at + keyword.Length)..].TakeWhile(char.IsAsciiDigit).ToArray();
            return digits.Length > 0 && int.TryParse(digits, out var count) ? count : 0;
        }
    }

    private static DateTimeOffset? ParseDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}
