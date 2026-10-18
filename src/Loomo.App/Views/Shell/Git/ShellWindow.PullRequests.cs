namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: ホスティング（GitHub §24.27・Azure DevOps §24.27.1）に依らない PR の扱い。
/// 先頭と基準を ref を作らずに取り（<c>fetch</c> → <c>FETCH_HEAD</c>）、Diff ペインの2点比較（分岐点から）で
/// 見せる・<c>pr/&lt;番号&gt;</c> ブランチのワークツリーで別の部屋として開く。
/// </summary>
public partial class ShellWindow
{
    private sealed record FetchedPullRequest(string HeadHash, string BaseHash);

    /// <summary>PR の先頭（<paramref name="headRef"/>）と基準（<paramref name="baseRef"/>）を取ってハッシュにする。
    /// 失敗は <paramref name="showError"/> で理由を出して null。</summary>
    private async Task<FetchedPullRequest?> FetchPullRequestAsync(
        string remote, string headRef, string baseRef, string label, Action<string, string> showError)
    {
        var head = await FetchToHashAsync(remote, headRef);
        var @base = head.Hash is null ? head : await FetchToHashAsync(remote, baseRef);
        if (head.Hash is null || @base.Hash is null)
        {
            showError($"{label} を取得できませんでした", (head.Hash is null ? head.Error : @base.Error) ?? "");
            return null;
        }
        return new FetchedPullRequest(head.Hash, @base.Hash);
    }

    private async Task<(string? Hash, string? Error)> FetchToHashAsync(string remote, string reference)
    {
        var fetch = await _git.RunAsync("fetch", "--no-tags", remote, reference);
        if (!fetch.Success) return (null, fetch.Message);
        var hash = await _git.RunAsync("rev-parse", "FETCH_HEAD");
        return hash.Success ? (hash.Output.Trim(), null) : (null, hash.Message);
    }

    /// <summary>分岐点からの差分（GitHub の Files changed・Azure Repos の「ファイル」と同じ見え方）。</summary>
    private void ShowPullRequestDiff(FetchedPullRequest fetched, string baseLabel, string headLabel)
        => ShowDiff(new DiffOpenTarget.PointsRange(
            new GitCompareEndpoint(GitCompareEndpointKind.Ref, fetched.BaseHash, baseLabel),
            new GitCompareEndpoint(GitCompareEndpointKind.Ref, fetched.HeadHash, headLabel),
            FromMergeBase: true, headLabel));

    /// <summary><paramref name="branch"/> を PR の先頭から作ってワークツリーに置き、その部屋へ移る。今の作業ツリーには触らない。</summary>
    private async Task OpenPullRequestWorktreeAsync(
        string branch, string folderName, string headHash, Action<string, string> showError)
    {
        var git = _vm.GitSession;
        if (git.Worktrees.FirstOrDefault(w => w.Branch == branch) is { } existing)
        {
            // もう置いてある：先頭が進んでいればそのワークツリーで pull してもらう（勝手に書き換えない）。
            git.OpenWorktree(existing, GitWorktreeOpenMode.Workspace);
            return;
        }
        var exists = git.BranchInfos.Any(b => !b.IsRemote && b.Name == branch);
        if (exists)
        {
            // どこにも置かれていない古いブランチは PR の今の先頭へ合わせ直す（置かれていないので誰の作業も消さない）。
            var moved = await _git.RunAsync("branch", "-f", branch, headHash);
            if (!moved.Success)
            {
                showError($"ブランチ {branch} を更新できませんでした", moved.Message);
                return;
            }
        }
        var request = new GitWorktreeAddRequest(
            exists ? GitWorktreeAddMode.ExistingBranch : GitWorktreeAddMode.NewBranch,
            git.SuggestWorktreePath(folderName), branch, exists ? null : headHash);
        await git.CreateWorktreeAsync(request, GitWorktreeOpenMode.Workspace);
    }
}
