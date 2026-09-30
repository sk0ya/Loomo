using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ワークツリーをどう開くか（行き先を決めるのは ShellWindow）。</summary>
public enum GitWorktreeOpenMode
{
    /// <summary>別のワークスペース（部屋）として開く＝そのワークツリー用の配置・タブ・軌跡を持つ。</summary>
    Workspace,

    /// <summary>今のワークスペースにフォルダーとして足す（マルチルート）。並べて見比べながら触るとき。</summary>
    AddFolder,

    /// <summary>表示ターミナルをその場所へ移す。</summary>
    Terminal,

    /// <summary>エクスプローラー（Windows）で開く。</summary>
    Explorer,
}

/// <summary>ワークツリーを開く要求。</summary>
public sealed record GitWorktreeOpenRequest(string Path, GitWorktreeOpenMode Mode);

/// <summary>
/// Git ペインのワークツリー部分：一覧・作成・削除・ロック・掃除と、「このワークツリーと比較」「2点比較」。
/// 比較は <see cref="DiffOpenTarget"/> を出すだけで、Diff ペインの VM も比較基準の VM も直接は触らない
/// （出し先・基準の書き込みは受け手＝<see cref="DiffSessionViewModel.ShowAsync"/> の仕事）。
/// </summary>
public sealed partial class GitSessionViewModel
{
    /// <summary>ワークツリー一覧（先頭がメイン）。ワークツリーのタブを見ている間だけ未コミットの件数まで数える。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkedWorktreeCount))]
    private IReadOnlyList<GitWorktreeInfo> _worktrees = Array.Empty<GitWorktreeInfo>();

    /// <summary>メイン以外のワークツリーの数（タブの件数）。メインしか無いリポジトリでは 0。</summary>
    public int LinkedWorktreeCount => Math.Max(0, Worktrees.Count - 1);

    /// <summary>ワークツリーを開いてほしい（ワークスペース切替・フォルダー追加・ターミナル・エクスプローラー）。</summary>
    public event EventHandler<GitWorktreeOpenRequest>? WorktreeOpenRequested;

    /// <summary>いま Git 操作の対象になっているワークツリー。</summary>
    public GitWorktreeInfo? CurrentWorktree => Worktrees.FirstOrDefault(w => w.IsCurrent);

    /// <summary>メインのワークツリー（新しいワークツリーの既定の置き場所の起点）。</summary>
    public string? MainWorktreePath => Worktrees.FirstOrDefault(w => w.IsMain)?.Path ?? _git.RootPath;

    /// <summary>ブランチ名から作る既定の置き場所（リポジトリの隣の <c>&lt;名前&gt;.worktrees</c> の下）。</summary>
    public string SuggestWorktreePath(string? branchOrName)
        => MainWorktreePath is { } main ? GitWorktreeArgs.SuggestPath(main, branchOrName) : "";

    /// <summary>直近に読み込んだブランチ一覧（作成ダイアログがローカル／リモートを見分けるのに使う）。</summary>
    public IReadOnlyList<GitBranchInfo> BranchInfos => _allBranches;

    /// <summary>作成ダイアログで起点・既存ブランチとして選べるもの（ローカル → リモート → タグ）。</summary>
    public IReadOnlyList<string> WorktreeStartPointOptions()
        => _allBranches.Where(b => !b.IsRemote).Select(b => b.Name)
            .Concat(_allBranches.Where(b => b.IsRemote).Select(b => b.Name))
            .Concat(Tags.Select(t => t.Name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>既存ブランチのうち、まだどのワークツリーにもチェックアウトされていないもの
    /// （git は同じブランチを2か所に置けない）。リモート追跡ブランチも含める——選べば git が追跡ブランチを作る。</summary>
    public IReadOnlyList<string> CheckoutableBranches()
    {
        var used = Worktrees.Select(w => w.Branch).Where(b => b is not null).ToHashSet(StringComparer.Ordinal);
        return _allBranches.Where(b => !b.IsRemote && !used.Contains(b.Name)).Select(b => b.Name)
            .Concat(_allBranches.Where(b => b.IsRemote && !b.Name.EndsWith("/HEAD", StringComparison.Ordinal))
                .Select(b => b.Name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>2点比較の端として選べるもの：ワークツリー（いまの状態）→ ローカルブランチ → リモート → タグ。</summary>
    public IReadOnlyList<GitCompareEndpoint> CompareEndpoints()
    {
        var endpoints = Worktrees.Where(w => !w.IsBare && w.Exists)
            .Select(GitCompareEndpoint.Worktree).ToList();
        endpoints.AddRange(WorktreeStartPointOptions().Select(GitCompareEndpoint.Ref));
        return endpoints;
    }

    /// <summary>ワークツリー一覧を読み直す（タブを見ているときは件数付き）。中身が同じなら差し替えない
    /// ——差し替えると行の選択が外れ、右クリックしようとしていた行を見失う。</summary>
    private async Task ReloadWorktreesAsync()
    {
        if (!IsRepository)
        {
            Worktrees = Array.Empty<GitWorktreeInfo>();
            return;
        }
        var withCounts = EffectiveReferenceTab == GitReferenceTab.Worktrees;
        var list = await _git.GetWorktreesAsync(withCounts);
        if (!list.SequenceEqual(Worktrees))
        {
            Worktrees = list;
            OnPropertyChanged(nameof(CurrentWorktree));
        }
    }

    /// <summary>ワークツリーのタブを開いたときに件数付きで読み直す（ほかのタブの間は名前だけ引いている）。</summary>
    private void OnReferenceTabChangedForWorktrees(GitReferenceTab value)
    {
        if (value == GitReferenceTab.Worktrees && _loaded)
            _ = ReloadWorktreesAsync();
    }

    /// <summary>手動の読み直し（相手のワークツリーで編集した分の件数は、こちらの監視には現れない）。</summary>
    public Task RefreshWorktreesAsync()
    {
        _git.InvalidateReadCache();
        return ReloadWorktreesAsync();
    }

    /// <summary>ワークツリーを作る。成功したら <paramref name="openAfter"/> の開き方で開く。</summary>
    public async Task<bool> CreateWorktreeAsync(GitWorktreeAddRequest request, GitWorktreeOpenMode? openAfter)
    {
        var result = await Commands.AddWorktreeAsync(request);
        if (result is not { Success: true }) return false;
        if (openAfter is { } mode)
            WorktreeOpenRequested?.Invoke(this, new GitWorktreeOpenRequest(request.Path, mode));
        return true;
    }

    /// <summary>
    /// ワークツリーを消す直前（ShellWindow が、そのフォルダーを今のワークスペースから外す）。
    /// 外さずに消すと、言語サーバー・ファイル監視・開いているタブがフォルダーを握ったままになり、
    /// Windows では削除そのものが「使用中」で失敗する。
    /// </summary>
    public event EventHandler<string>? WorktreeRemoving;

    public async Task<GitCommandResult?> RemoveWorktreeAsync(GitWorktreeInfo worktree, bool force)
    {
        if (!worktree.CanRemove || worktree.IsCurrent) return null;
        WorktreeRemoving?.Invoke(this, worktree.Path);
        return await Commands.RemoveWorktreeAsync(worktree, force);
    }

    public Task<GitCommandResult?> PruneWorktreesAsync() => Commands.PruneWorktreesAsync();

    public Task<GitCommandResult?> LockWorktreeAsync(GitWorktreeInfo worktree, string? reason)
        => Commands.LockWorktreeAsync(worktree, reason);

    public Task<GitCommandResult?> UnlockWorktreeAsync(GitWorktreeInfo worktree)
        => Commands.UnlockWorktreeAsync(worktree);

    public void OpenWorktree(GitWorktreeInfo worktree, GitWorktreeOpenMode mode)
    {
        if (!worktree.Exists)
        {
            SetStatus($"ワークツリー「{worktree.DisplayName}」のフォルダーがありません（「掃除」で一覧から外せます）。", isError: true);
            return;
        }
        WorktreeOpenRequested?.Invoke(this, new GitWorktreeOpenRequest(worktree.Path, mode));
    }

    /// <summary>このワークツリーのいまの状態（未コミット・未追跡込み）と、自分の作業ツリーを比べる。
    /// 右側は自分の作業ツリーなので、見ながらその場で取り込める。</summary>
    public void CompareWithWorktree(GitWorktreeInfo worktree)
    {
        if (worktree.IsCurrent)
        {
            SetStatus("いま開いているワークツリー自身です。別のワークツリーを選んでください。", isError: true);
            return;
        }
        DiffOpenRequested?.Invoke(this, new DiffOpenTarget.CompareBase(
            new GitCompareBaseSelection(GitCompareBaseKind.Worktree, worktree.Path), worktree.DisplayName));
    }

    /// <summary>コミットと作業ツリーを比べる（コミット以降にコミット済み・未コミットの変更を全部）。</summary>
    public void CompareCommitWithWorkingTree(GitLogRow row)
    {
        if (row.Hash is not { } hash) return;
        DiffOpenRequested?.Invoke(this, new DiffOpenTarget.CompareBase(
            new GitCompareBaseSelection(GitCompareBaseKind.Revision, hash), row.ShortHash ?? DiffOpenTarget.Short(hash)));
    }

    /// <summary>ブランチと作業ツリーを比べる。<paramref name="fromMergeBase"/> なら分岐点から
    /// （＝このブランチで自分が入れた変更だけ）。</summary>
    public void CompareBranchWithWorkingTree(GitBranchInfo branch, bool fromMergeBase)
        => DiffOpenRequested?.Invoke(this, new DiffOpenTarget.CompareBase(
            new GitCompareBaseSelection(fromMergeBase ? GitCompareBaseKind.MergeBase : GitCompareBaseKind.Branch, branch.Name),
            branch.Name));

    /// <summary>
    /// 2点比較。<b>右（to）がいま開いているワークツリーなら比較基準として開く</b>——右側が作業ツリーそのものに
    /// なり、差分を見ながらその場で編集できる。それ以外は2つを固めたコミット範囲として（読み取り専用で）開く。
    /// </summary>
    public async Task ComparePointsAsync(GitCompareEndpoint from, GitCompareEndpoint to, bool fromMergeBase)
    {
        if (await AsCompareBaseAsync(from, to, fromMergeBase) is { } selection)
        {
            DiffOpenRequested?.Invoke(this, new DiffOpenTarget.CompareBase(selection, from.Label));
            return;
        }

        SetStatus("比較の準備中…", isError: false);
        var range = await _git.ResolveCompareRangeAsync(from, to, fromMergeBase);
        if (range.HasError)
        {
            SetStatus(range.Error!, isError: true);
            return;
        }
        SetStatus("", isError: false);
        DiffOpenRequested?.Invoke(this, new DiffOpenTarget.CommitRange(range.FromRef, range.ToRef!, range.Label));
    }

    /// <summary>
    /// 2点比較が「比較基準 ↔ 自分の作業ツリー」で言い表せるならその基準（言い表せなければ null）。
    /// ブランチ／分岐点の基準は<b>ブランチ一覧に在る名前だけ</b>——タグやハッシュを入れると、比較基準の VM が
    /// 候補を読み直したときに「消えた枝」として既定ブランチへすり替えてしまう。それらはリビジョン基準で表す。
    /// </summary>
    private async Task<GitCompareBaseSelection?> AsCompareBaseAsync(
        GitCompareEndpoint from, GitCompareEndpoint to, bool fromMergeBase)
    {
        if (to.Kind != GitCompareEndpointKind.Worktree || CurrentWorktree is not { } current
            || !current.IsSamePath(to.Value))
            return null;

        if (from.Kind == GitCompareEndpointKind.Worktree)
            return fromMergeBase || current.IsSamePath(from.Value)
                ? null
                : new GitCompareBaseSelection(GitCompareBaseKind.Worktree, from.Value);

        var isBranch = (await _git.GetComparableRefsAsync()).Contains(from.Value, StringComparer.Ordinal);
        if (fromMergeBase)
            return isBranch ? new GitCompareBaseSelection(GitCompareBaseKind.MergeBase, from.Value) : null;
        return new GitCompareBaseSelection(
            isBranch ? GitCompareBaseKind.Branch : GitCompareBaseKind.Revision, from.Value);
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }
}
