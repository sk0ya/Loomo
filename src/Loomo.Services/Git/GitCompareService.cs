using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>
/// 比較基準（<see cref="GitCompareBaseSelection"/>）を実際の ref へ解決し、その基準に対する
/// 変更ファイル一覧と差分を引く。引数の組み立てと既定ブランチの推定は純粋な
/// <see cref="GitCompareArgs"/> 側にあり、ここは git の起動と失敗理由の日本語化だけを担う。
/// 対象リポジトリは <see cref="GitRootState"/>（＝<see cref="GitService.RootPath"/>）——
/// <c>IWorkspaceService.PrimaryFolder</c> とは別概念なので、ここでは一切参照しない。
/// </summary>
public sealed class GitCompareService
{
    private readonly GitCommandRunner _runner;
    private readonly GitWorktreeService _worktrees;

    public GitCompareService(GitCommandRunner runner, GitWorktreeService worktrees)
    {
        _runner = runner;
        _worktrees = worktrees;
    }

    /// <summary>
    /// 既定ブランチを推定する。<c>origin/HEAD</c> → <c>main</c> → <c>master</c> →
    /// <c>origin/main</c> → <c>origin/master</c> の順で<b>実在するもの</b>を選ぶ。
    /// リモート追跡が無いリポジトリでも壊れない（<c>origin/HEAD</c> の照会が失敗しても次へ進む）。
    /// どれも無ければ null。
    /// </summary>
    public async Task<string?> GetDefaultBranchAsync(IReadOnlyList<string>? availableRefs = null)
    {
        var originHead = await _runner
            .RunAsync("symbolic-ref", "--quiet", "refs/remotes/origin/HEAD").ConfigureAwait(false);
        var refs = availableRefs ?? await GetComparableRefsAsync().ConfigureAwait(false);
        return GitCompareArgs.PickDefaultBranch(
            originHead.Success ? originHead.Output : null, refs);
    }

    /// <summary>
    /// 比較基準として選べる ref（ローカルブランチとリモート追跡ブランチ）。
    /// <c>refs/remotes/…/HEAD</c> は実体のある枝ではないので除く。
    /// <b>短縮名は重複し得る</b>——ローカルに <c>origin/main</c> という名前のブランチがあると
    /// <c>refs/heads/origin/main</c> と <c>refs/remotes/origin/main</c> が同じ文字列になる。
    /// 一覧に同じ行を2つ並べても選び分けられないので最初の1つだけ残す（その名前で <c>git diff</c> を
    /// 引くと git は曖昧警告付きでローカル側を選ぶ＝refs/heads を先に問い合わせているこの順と一致する）。
    /// </summary>
    public async Task<IReadOnlyList<string>> GetComparableRefsAsync()
    {
        var result = await _runner.RunAsync(
            "for-each-ref", "--format=%(refname:short)", "refs/heads", "refs/remotes")
            .ConfigureAwait(false);
        var names = new List<string>();
        if (!result.Success) return names;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in result.Output.Split('\n'))
        {
            var value = line.Trim();
            if (value.Length == 0 || value.EndsWith("/HEAD", StringComparison.Ordinal)) continue;
            if (seen.Add(value)) names.Add(value);
        }
        return names;
    }

    /// <summary>
    /// 比較基準を実際の ref へ解決する。失敗は例外ではなく
    /// <see cref="GitCompareResolution.Error"/>（日本語の理由）として返す——
    /// 空リポジトリ・基準ブランチ不在・分岐点なし（無関係な履歴）で壊れず理由が出るのが要件。
    /// </summary>
    /// <param name="progress">いま何をしているか（大きなリポジトリで待たせるときに画面へ出す）。</param>
    /// <param name="cancellationToken">中止。中止されたら <see cref="GitCompareResolution.Canceled"/> を返す（例外にしない）。</param>
    public async Task<GitCompareResolution> ResolveAsync(
        GitCompareBaseSelection selection, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ResolveCoreAsync(selection, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return GitCompareResolution.Canceled;
        }
    }

    private async Task<GitCompareResolution> ResolveCoreAsync(
        GitCompareBaseSelection selection, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (selection.IsWorkingTree)
            return GitCompareResolution.WorkingTree;

        if (selection.Kind == GitCompareBaseKind.Worktree)
            return await ResolveWorktreeAsync(selection.Target, progress, cancellationToken).ConfigureAwait(false);
        if (selection.Kind == GitCompareBaseKind.Revision)
            return await ResolveRevisionAsync(selection.Target).ConfigureAwait(false);

        var branch = selection.Target?.Trim();
        if (string.IsNullOrEmpty(branch))
            return new GitCompareResolution(null, "比較するブランチが選ばれていません。", "基準未選択");

        if (!await ExistsAsync(branch).ConfigureAwait(false))
            return new GitCompareResolution(
                null, $"基準ブランチ「{branch}」が見つかりません。", $"{branch}（不明）");

        if (selection.Kind == GitCompareBaseKind.Branch)
            return new GitCompareResolution(branch, null, $"{branch} と比較");

        // 分岐点：HEAD が無ければ（コミット0件の空リポジトリ）merge-base は成り立たない。
        if (!await ExistsAsync("HEAD").ConfigureAwait(false))
            return new GitCompareResolution(
                null, "コミットがまだありません（空のリポジトリ）。", $"{branch} との分岐点");

        progress?.Report($"{branch} との分岐点を求めています…");
        var mergeBase = await _runner
            .RunAsync(cancellationToken, GitCompareArgs.MergeBaseArgs(branch)).ConfigureAwait(false);
        var hash = mergeBase.Output.Trim();
        // 無関係な履歴（共通の祖先が無い）では merge-base が非0で終わるか、何も出さない。
        if (!mergeBase.Success || hash.Length == 0)
            return new GitCompareResolution(
                null, $"「{branch}」と HEAD に共通の分岐点がありません（履歴が無関係です）。",
                $"{branch} との分岐点");

        return new GitCompareResolution(hash, null, $"{branch} との分岐点と比較");
    }

    /// <summary>
    /// 別の作業ツリーのいまの状態を基準にする。相手を一覧から引き直して実在・自分自身でないことを確かめ、
    /// 未コミット・未追跡込みで tree に固める（<see cref="GitWorktreeService.SnapshotAsync"/>）。
    /// </summary>
    private async Task<GitCompareResolution> ResolveWorktreeAsync(
        string? path, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new GitCompareResolution(null, "比較するワークツリーが選ばれていません。", "基準未選択");

        var worktrees = await _worktrees.ListAsync(includeChangeCounts: false).ConfigureAwait(false);
        var target = worktrees.FirstOrDefault(w => w.IsSamePath(path));
        if (target is null)
            return new GitCompareResolution(
                null, $"ワークツリー「{path}」が見つかりません（削除されたか、別のリポジトリです）。", "ワークツリー（不明）");
        if (target.IsCurrent)
            return new GitCompareResolution(
                null, "いま開いているワークツリー自身です。別のワークツリーを選んでください。", target.DisplayName);
        if (!target.Exists)
            return new GitCompareResolution(
                null, $"ワークツリー「{target.DisplayName}」のフォルダーがありません（掃除の対象です）。", target.DisplayName);

        progress?.Report($"ワークツリー {target.DisplayName} の作業中の状態を固めています…");
        var snapshot = await _worktrees.SnapshotAsync(target.Path, cancellationToken).ConfigureAwait(false);
        return snapshot.Success
            ? new GitCompareResolution(snapshot.Tree, null, $"ワークツリー {target.DisplayName}（作業中）と比較")
            : new GitCompareResolution(null, snapshot.Error, target.DisplayName);
    }

    private async Task<GitCompareResolution> ResolveRevisionAsync(string? revision)
    {
        var value = revision?.Trim();
        if (string.IsNullOrEmpty(value))
            return new GitCompareResolution(null, "比較するリビジョンを入力してください（タグ・ハッシュ・HEAD~1 など）。", "基準未選択");
        if (!GitWorktreeArgs.IsValidReference(value))
            return new GitCompareResolution(null, $"リビジョン「{value}」は使えません。", value);
        var hash = await _worktrees.ResolveCommitAsync(value).ConfigureAwait(false);
        return hash is null
            ? new GitCompareResolution(null, $"リビジョン「{value}」が見つかりません。", $"{value}（不明）")
            : new GitCompareResolution(hash, null, $"{Short(value)} と比較");
    }

    /// <summary>
    /// 2点比較（<paramref name="from"/> → <paramref name="to"/>）を <c>git diff</c> に渡せる2つのハッシュへ解決する。
    /// 作業ツリーの端はいまの状態（未コミット・未追跡込み）の tree。<paramref name="fromMergeBase"/> なら
    /// 起点を「2つの分岐点」に置き換える＝<c>to</c> 側が分かれてから入れた変更だけになる。分岐点は
    /// 履歴の上の概念なので、作業ツリーの端はその作業ツリーの HEAD で計算する（未コミット分は to 側に残る）。
    /// <b>分岐点からなら左は固めない</b>——大きなリポジトリでは作業ツリーを固めるのがいちばん重い。
    /// 中止されたら <see cref="GitCompareRange.IsCanceled"/> の結果を返す（例外にしない）。
    /// </summary>
    public async Task<GitCompareRange> ResolveRangeAsync(
        GitCompareEndpoint from, GitCompareEndpoint to, bool fromMergeBase,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var label = fromMergeBase ? $"{from.Label} との分岐点 → {to.Label}" : $"{from.Label} → {to.Label}";
        try
        {
            return await ResolveRangeCoreAsync(from, to, fromMergeBase, label, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new GitCompareRange(null, null, label, GitCompareCancellation.Message);
        }
    }

    private async Task<GitCompareRange> ResolveRangeCoreAsync(
        GitCompareEndpoint from, GitCompareEndpoint to, bool fromMergeBase, string label,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var toSide = await ResolveEndpointAsync(to, progress, cancellationToken).ConfigureAwait(false);
        if (toSide.Error is not null) return new GitCompareRange(null, null, label, toSide.Error);

        if (fromMergeBase)
        {
            var fromCommit = await ResolveEndpointCommitAsync(from).ConfigureAwait(false);
            var toCommit = await ResolveEndpointCommitAsync(to).ConfigureAwait(false);
            if (fromCommit.Error is not null) return new GitCompareRange(null, null, label, fromCommit.Error);
            if (toCommit.Error is not null) return new GitCompareRange(null, null, label, toCommit.Error);
            progress?.Report("分岐点を求めています…");
            var mergeBase = await _runner.RunAsync(cancellationToken, "merge-base", fromCommit.Hash!, toCommit.Hash!)
                .ConfigureAwait(false);
            var hash = mergeBase.Output.Trim();
            if (!mergeBase.Success || hash.Length == 0)
                return new GitCompareRange(null, null, label,
                    $"「{from.Label}」と「{to.Label}」に共通の分岐点がありません（履歴が無関係です）。");
            return new GitCompareRange(hash, toSide.Hash, label, null);
        }

        var fromSide = await ResolveEndpointAsync(from, progress, cancellationToken).ConfigureAwait(false);
        return fromSide.Error is not null
            ? new GitCompareRange(null, null, label, fromSide.Error)
            : new GitCompareRange(fromSide.Hash, toSide.Hash, label, null);
    }

    /// <summary>
    /// ワークツリーの「ブランチ元」を求める。覚えていたもの（Loomo で作ったときの起点）→ reflog の
    /// 「Created from …」→ 既定ブランチの順で、<b>実在するもの</b>を選ぶ（覚えていた枝が消されていれば次へ）。
    /// 自分自身のブランチは元になり得ないので外す。どれも無ければ null。
    /// </summary>
    public async Task<GitBranchOrigin?> ResolveBranchOriginAsync(GitWorktreeInfo worktree)
    {
        if (worktree.Branch is { } branch)
        {
            var recorded = await _runner.RunAsync("config", "--get", GitWorktreeArgs.OriginConfigKey(branch))
                .ConfigureAwait(false);
            if (await UsableOriginAsync(recorded.Success ? recorded.Output : null, branch).ConfigureAwait(false) is { } fromConfig)
                return new GitBranchOrigin(fromConfig, GitBranchOriginSource.Recorded);

            var reflog = await _runner.RunAsync("log", "-g", "--format=%gs", $"refs/heads/{branch}", "--")
                .ConfigureAwait(false);
            var created = reflog.Success ? GitWorktreeArgs.ParseCreatedFrom(reflog.Output) : null;
            if (await UsableOriginAsync(created, branch).ConfigureAwait(false) is { } fromReflog)
                return new GitBranchOrigin(fromReflog, GitBranchOriginSource.Reflog);
        }

        var fallback = await GetDefaultBranchAsync().ConfigureAwait(false);
        return await UsableOriginAsync(fallback, worktree.Branch).ConfigureAwait(false) is { } defaultBranch
            ? new GitBranchOrigin(defaultBranch, GitBranchOriginSource.DefaultBranch)
            : null;
    }

    /// <summary>ブランチ元の候補として使えるか（ref として正しく・自分自身でなく・いま実在する）。</summary>
    private async Task<string?> UsableOriginAsync(string? candidate, string? ownBranch)
    {
        var value = candidate?.Trim();
        if (string.IsNullOrEmpty(value) || !GitWorktreeArgs.IsValidReference(value)) return null;
        if (string.Equals(value, ownBranch, StringComparison.Ordinal)) return null;
        return await ExistsAsync(value).ConfigureAwait(false) ? value : null;
    }

    /// <summary>端の中身そのもの（ref はコミット、作業ツリーはいまの状態の tree）。</summary>
    private async Task<(string? Hash, string? Error)> ResolveEndpointAsync(
        GitCompareEndpoint endpoint, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (endpoint.Kind == GitCompareEndpointKind.Worktree)
        {
            if (!Directory.Exists(endpoint.Value))
                return (null, $"ワークツリーのフォルダーがありません: {endpoint.Value}");
            progress?.Report($"{endpoint.Label}の状態を固めています…");
            var snapshot = await _worktrees.SnapshotAsync(endpoint.Value, cancellationToken).ConfigureAwait(false);
            return (snapshot.Tree, snapshot.Error);
        }
        return await ResolveRefAsync(endpoint.Value).ConfigureAwait(false);
    }

    /// <summary>端の「履歴の上の位置」（ref はそのコミット、作業ツリーはその HEAD）。</summary>
    private async Task<(string? Hash, string? Error)> ResolveEndpointCommitAsync(GitCompareEndpoint endpoint)
    {
        if (endpoint.Kind != GitCompareEndpointKind.Worktree)
            return await ResolveRefAsync(endpoint.Value).ConfigureAwait(false);
        if (!Directory.Exists(endpoint.Value))
            return (null, $"ワークツリーのフォルダーがありません: {endpoint.Value}");
        var head = await _worktrees.ResolveCommitAsync("HEAD", endpoint.Value).ConfigureAwait(false);
        return head is null ? (null, $"ワークツリー「{endpoint.Label}」にはまだコミットがありません。") : (head, null);
    }

    private async Task<(string? Hash, string? Error)> ResolveRefAsync(string reference)
    {
        var value = reference.Trim();
        if (!GitWorktreeArgs.IsValidReference(value))
            return (null, $"「{reference}」は ref として使えません。");
        var hash = await _worktrees.ResolveCommitAsync(value).ConfigureAwait(false);
        return hash is null ? (null, $"「{value}」が見つかりません。") : (hash, null);
    }

    /// <summary>40桁ハッシュは7桁に詰める（見出し用）。ref 名はそのまま。</summary>
    private static string Short(string value)
        => value.Length == 40 && value.All(Uri.IsHexDigit) ? value[..7] : value;

    /// <summary>基準に対する変更ファイル一覧。未追跡ファイルは含まない・リネームは1件にまとめる
    /// （理由は <see cref="GitCompareArgs"/> の説明を参照）。失敗は空リストではなく理由付きで返す
    /// ——黙って「変更なし」と出すと、差分があるのに無いと嘘をつくことになる。</summary>
    public async Task<GitCompareChanges> GetChangesAsync(string baseRef, CancellationToken cancellationToken = default)
    {
        GitCommandResult result;
        try
        {
            result = await _runner
                .RunAsync(cancellationToken, GitCompareArgs.NameStatusArgs(baseRef)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return GitCompareChanges.Canceled;
        }
        return result.Success
            ? new GitCompareChanges(GitNameStatusParser.Parse(result.Output), null)
            : new GitCompareChanges(
                Array.Empty<GitCommitFileChange>(), $"変更一覧を取得できませんでした: {result.Message}");
    }

    /// <summary>基準に対する1ファイルの差分テキスト（失敗時は git のメッセージをそのまま返す）。</summary>
    public async Task<string> GetFileDiffAsync(
        string baseRef, GitCommitFileChange file, int contextLines = 3, bool ignoreWhitespace = false)
    {
        var result = await _runner
            .RunAsync(GitCompareArgs.FileDiffArgs(baseRef, file, contextLines, ignoreWhitespace)).ConfigureAwait(false);
        return result.Success ? result.Output : result.Message;
    }

    private async Task<bool> ExistsAsync(string reference)
        => (await _runner.RunAsync(GitCompareArgs.VerifyCommitArgs(reference))
            .ConfigureAwait(false)).Success;
}
