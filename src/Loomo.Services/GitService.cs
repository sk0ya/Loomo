using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Services;

/// <summary>
/// git CLI を独立した非対話プロセスで実行する Git クライアントサービス。
/// ワークスペースのプライマリフォルダー（<see cref="IWorkspaceService.PrimaryFolder"/>）をリポジトリとして扱う。
/// 表示ターミナルには一切流さない（AI と同様、人間のターミナルを汚さない方針）。
/// 認証プロンプト・エディタ起動で固まらないよう GIT_TERMINAL_PROMPT=0 / GIT_EDITOR=true を強制する
/// （rebase --continue 等のメッセージ編集は既定メッセージのまま確定される）。
/// </summary>
public sealed class GitService
{
    private readonly IWorkspaceService _workspace;
    private readonly GitRootState _rootState;
    private readonly GitCommandRunner _runner;
    private readonly GitStatusService _status;
    private readonly GitHistoryService _history;
    private readonly GitBranchService _branches;
    private readonly GitMutationExecutor _mutations;
    private readonly GitMergeService _merge;
    private readonly GitSubmoduleService _submodules;
    private readonly GitCommitService _commits;
    private readonly GitStashService _stashes;
    private readonly GitDiffService _diff;
    private readonly GitRebaseService _rebase;
    private readonly GitCompareService _compare;
    private readonly GitCloneService _clone;
    private readonly GitRepositoryMonitor _monitor;

    public GitService(IWorkspaceService workspace)
    {
        _workspace = workspace;
        _rootState = new GitRootState(workspace);
        _runner = new GitCommandRunner(_rootState);
        _status = new GitStatusService(_runner);
        _history = new GitHistoryService(_runner);
        _mutations = new GitMutationExecutor(_runner);
        _branches = new GitBranchService(_runner, _mutations);
        _merge = new GitMergeService(_mutations);
        _submodules = new GitSubmoduleService(_runner, _mutations);
        _commits = new GitCommitService(_rootState, _runner, _mutations);
        _stashes = new GitStashService(_runner, _mutations);
        _diff = new GitDiffService(_rootState, _runner, _mutations);
        _rebase = new GitRebaseService(_runner, _mutations);
        _compare = new GitCompareService(_runner);
        _clone = new GitCloneService(_runner);
        _monitor = new GitRepositoryMonitor(_rootState, _runner);
        _monitor.RepositoryChanged += (_, _) => RepositoryChanged?.Invoke(this, EventArgs.Empty);
        _mutations.RepositoryChanged += (_, _) => RepositoryChanged?.Invoke(this, EventArgs.Empty);
        _mutations.OperationExecuted += (_, e) => OperationExecuted?.Invoke(this, e);
    }

    public event EventHandler? RepositoryChanged;

    public event EventHandler<GitOperationEventArgs>? OperationExecuted;

    /// <summary>Git 操作の対象フォルダーが切り替わったとき（マルチルートの明示切替）。</summary>
    public event EventHandler? ActiveRootChanged
    {
        add => _rootState.Changed += value;
        remove => _rootState.Changed -= value;
    }

    public IDisposable TrackLiveChanges() => _monitor.TrackLiveChanges();

    public string? RootPath => _rootState.CurrentRoot;

    /// <summary>マルチルート：Git 操作の対象を選べるワークスペースフォルダー一覧。</summary>
    public IReadOnlyList<string> AvailableRoots => _workspace.Folders;

    /// <summary>Git 操作の対象フォルダーを明示的に切り替える（ワークスペースフォルダーでなければ無視）。</summary>
    public void SetActiveRoot(string? path) => _rootState.SetRoot(path);

    /// <summary>fullPath を含むワークスペースフォルダーへ Git 操作対象を切り替える（該当が無ければ何もしない）。
    /// ブレーム・履歴などファイル起点の操作を、現在の対象と違うフォルダーのファイルに対して行うときに使う。</summary>
    public void SetActiveRootForPath(string fullPath) => _rootState.SelectForPath(fullPath);

    public Task<GitStatusSnapshot> GetStatusAsync() => _status.GetStatusAsync();

    public Task<IReadOnlyList<string>> GetRemotesAsync() => _branches.GetRemotesAsync();

    public Task<IReadOnlyList<GitRemoteInfo>> GetRemoteUrlsAsync() => _branches.GetRemoteUrlsAsync();

    public Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync() => _branches.GetBranchesAsync();

    internal static (int Ahead, int Behind, bool Gone) ParseTrack(string track)
        => GitBranchService.ParseTrack(track);

    public Task<IReadOnlyList<GitTagInfo>> GetTagsAsync() => _branches.GetTagsAsync();

    public Task<IReadOnlyList<GitSubmoduleInfo>> GetSubmodulesAsync() => _submodules.GetSubmodulesAsync();

    public async Task<IReadOnlyList<GitLogRow>> GetLogAsync(
        string? branchRef = null, int limit = 300, int skip = 0, string? pathFilter = null)
        => await _history.GetLogAsync(branchRef, limit, skip, pathFilter).ConfigureAwait(false);

    /// <summary>絞り込み条件付きでコミットログを引く（作者・本文・日付は git 側で篩われる）。</summary>
    public Task<IReadOnlyList<GitLogRow>> GetLogAsync(GitLogQuery query) => _history.GetLogAsync(query);

    // ===== 特定リビジョンのファイル =====

    /// <summary>そのコミット時点のファイル内容（失敗は理由付き）。</summary>
    public Task<GitCommandResult> GetFileAtRevisionAsync(string revision, string relativePath) =>
        _diff.GetFileAtRevisionAsync(revision, relativePath);

    /// <summary>作業ツリーのファイルをそのコミット時点の内容へ戻す（履歴は書き換えない）。</summary>
    public Task<GitCommandResult> RestoreFileAtRevisionAsync(string revision, string relativePath) =>
        _diff.RestoreFileAtRevisionAsync(revision, relativePath);

    /// <summary>リポジトリをクローンする（ワークスペースの現在の対象には触らない）。</summary>
    public Task<GitCloneResult> CloneAsync(
        string url, string parentDirectory, string? folderName = null,
        System.Threading.CancellationToken cancellationToken = default) =>
        _clone.CloneAsync(url, parentDirectory, folderName, cancellationToken);

    public Task<string> GetDiffTextAsync(GitChangeEntry entry, bool staged, int contextLines = 3) =>
        _diff.GetDiffTextAsync(entry, staged, contextLines);

    // ===== 比較基準（作業ツリー／ブランチ／分岐点） =====

    /// <summary>比較基準として選べる ref（ローカル＋リモート追跡ブランチ）。</summary>
    public Task<IReadOnlyList<string>> GetComparableRefsAsync() => _compare.GetComparableRefsAsync();

    /// <summary>既定ブランチの推定（origin/HEAD → main → master …）。無ければ null。</summary>
    public Task<string?> GetDefaultBranchAsync(IReadOnlyList<string>? availableRefs = null) =>
        _compare.GetDefaultBranchAsync(availableRefs);

    /// <summary>比較基準を実際の ref へ解決する（失敗は理由付きで返る）。</summary>
    public Task<GitCompareResolution> ResolveCompareBaseAsync(GitCompareBaseSelection selection) =>
        _compare.ResolveAsync(selection);

    /// <summary>基準に対する変更ファイル一覧（未追跡は含まない・リネームは1件）。失敗は理由付きで返る。</summary>
    public Task<GitCompareChanges> GetCompareChangesAsync(string baseRef) =>
        _compare.GetChangesAsync(baseRef);

    /// <summary>基準に対する1ファイルの差分テキスト。</summary>
    public Task<string> GetCompareFileDiffAsync(
        string baseRef, GitCommitFileChange file, int contextLines = 3) =>
        _compare.GetFileDiffAsync(baseRef, file, contextLines);

    public Task<string> GetCommitSummaryAsync(string hash) => _history.GetCommitSummaryAsync(hash);

    public Task<string> GetCommitPatchAsync(string hash) => _history.GetCommitPatchAsync(hash);

    public Task<IReadOnlyList<GitCommitFileChange>> GetRangeChangesAsync(string? fromHash, string toHash) =>
        _history.GetRangeChangesAsync(fromHash, toHash);

    public async Task<string> GetRangeFileDiffAsync(
        string? fromHash, string toHash, GitCommitFileChange file, int contextLines = 3)
        => await _history.GetRangeFileDiffAsync(fromHash, toHash, file, contextLines).ConfigureAwait(false);

    public Task<string?> GetConflictStageContentAsync(string path, int stage) =>
        _diff.GetConflictStageContentAsync(path, stage);

    public Task<(string? Base, string? Ours, string? Theirs)> GetConflictSidesAsync(string path) =>
        _diff.GetConflictSidesAsync(path);

    public Task<GitCommandResult> InitAsync() => _commits.InitializeAsync();

    public Task<GitCommandResult> StageAsync(string path) => _commits.StageAsync(path);
    public Task<GitCommandResult> StageAllAsync() => _commits.StageAllAsync();
    public Task<GitCommandResult> UnstageAsync(string path) => _commits.UnstageAsync(path);
    public Task<GitCommandResult> UnstageAllAsync() => _commits.UnstageAllAsync();

    public Task<GitCommandResult> StageAsync(IReadOnlyCollection<string> paths) => _commits.StageAsync(paths);

    public Task<GitCommandResult> UnstageAsync(IReadOnlyCollection<string> paths) => _commits.UnstageAsync(paths);

    public Task<GitCommandResult> DiscardAsync(GitChangeEntry entry) => _commits.DiscardAsync(entry);

    public Task<GitCommandResult> DiscardAsync(IReadOnlyCollection<GitChangeEntry> entries) =>
        _commits.DiscardAsync(entries);

    public Task<GitCommandResult> ApplyReverseDiscardPatchAsync(string patch) =>
        _commits.ApplyReverseDiscardPatchAsync(patch);

    public Task<GitCommandResult> CommitAsync(string message, bool amend = false, bool sign = false) =>
        _commits.CommitAsync(message, amend, sign);

    public Task<GitCommandResult> FetchAsync() => _branches.FetchAsync();
    public Task<GitCommandResult> PullAsync() => _branches.PullAsync();

    /// <summary>取り込み方（マージ／リベース／早送りのみ）を指定してプルする。</summary>
    public Task<GitCommandResult> PullAsync(GitPullMode mode) => _branches.PullAsync(mode);
    public Task<GitCommandResult> PullBranchAsync(GitBranchInfo branch) => _branches.PullBranchAsync(branch);

    public Task<GitCommandResult> PushAsync() => _branches.PushAsync();

    /// <summary>強制プッシュ（<c>--force-with-lease</c>）。リベースや amend の後の押し直し用。</summary>
    public Task<GitCommandResult> PushAsync(bool force) => _branches.PushAsync(force);
    public Task<GitCommandResult> PushBranchAsync(GitBranchInfo branch, string? defaultRemote) =>
        _branches.PushBranchAsync(branch, defaultRemote);
    public Task<GitCommandResult> PushBranchAsync(
        GitBranchInfo branch, string? defaultRemote, bool force) =>
        _branches.PushBranchAsync(branch, defaultRemote, force);

    /// <summary>リモート上のブランチを削除する（<c>origin/foo</c> 形式で渡す）。</summary>
    public Task<GitCommandResult> DeleteRemoteBranchAsync(string remoteBranch) =>
        _branches.DeleteRemoteBranchAsync(remoteBranch);

    // ===== 上流・リモートの管理 =====

    public Task<GitCommandResult> SetUpstreamAsync(string branch, string upstream) =>
        _branches.SetUpstreamAsync(branch, upstream);

    public Task<GitCommandResult> UnsetUpstreamAsync(string branch) =>
        _branches.UnsetUpstreamAsync(branch);

    public Task<GitCommandResult> AddRemoteAsync(string name, string url) =>
        _branches.AddRemoteAsync(name, url);

    public Task<GitCommandResult> SetRemoteUrlAsync(string name, string url) =>
        _branches.SetRemoteUrlAsync(name, url);

    public Task<GitCommandResult> RemoveRemoteAsync(string name) => _branches.RemoveRemoteAsync(name);

    public Task<GitCommandResult> CheckoutAsync(string branch) => _branches.CheckoutAsync(branch);

    public Task<GitCommandResult> CheckoutTrackAsync(string remoteBranch) =>
        _branches.CheckoutTrackAsync(remoteBranch);
    public Task<GitCommandResult> CheckoutCommitAsync(string hash) => _branches.CheckoutCommitAsync(hash);

    public Task<GitCommandResult> CreateBranchAsync(string name, string? startPoint = null) =>
        _branches.CreateBranchAsync(name, startPoint);

    public Task<GitCommandResult> DeleteBranchAsync(string name, bool force = false) =>
        _branches.DeleteBranchAsync(name, force);

    public Task<GitCommandResult> CreateTagAsync(string name, string? target = null, string? message = null)
        => _branches.CreateTagAsync(name, target, message);

    public Task<GitCommandResult> DeleteTagAsync(string name) => _branches.DeleteTagAsync(name);
    public Task<GitCommandResult> PushTagAsync(string name) => _branches.PushTagAsync(name);
    public Task<GitCommandResult> PushAllTagsAsync() => _branches.PushAllTagsAsync();

    public Task<GitCommandResult> SubmoduleInitAsync(string? path = null) => string.IsNullOrEmpty(path)
        ? _submodules.InitializeAsync()
        : _submodules.InitializeAsync(path);

    public Task<GitCommandResult> SubmoduleUpdateAsync(string? path = null, bool init = true, bool recursive = true)
        => _submodules.UpdateAsync(path, init, recursive);

    public Task<GitCommandResult> SubmoduleSyncAsync() => _submodules.SynchronizeAsync();

    public Task<GitCommandResult> MergeAsync(string branch, GitMergeStrategy strategy = GitMergeStrategy.Default) =>
        _merge.MergeAsync(branch, strategy);

    public Task<GitCommandResult> MergeContinueAsync() => _merge.ContinueMergeAsync();
    public Task<GitCommandResult> MergeAbortAsync() => _merge.AbortMergeAsync();

    public Task<GitCommandResult> RebaseAsync(string onto) => _rebase.RebaseAsync(onto);
    public Task<GitCommandResult> RebaseContinueAsync() => _rebase.ContinueAsync();
    public Task<GitCommandResult> RebaseSkipAsync() => _rebase.SkipAsync();
    public Task<GitCommandResult> RebaseAbortAsync() => _rebase.AbortAsync();

    public Task<GitCommandResult> CherryPickAsync(string hash) => _merge.CherryPickAsync(hash);
    public Task<GitCommandResult> CherryPickContinueAsync() => _merge.ContinueCherryPickAsync();
    public Task<GitCommandResult> CherryPickSkipAsync() => _merge.SkipCherryPickAsync();
    public Task<GitCommandResult> CherryPickAbortAsync() => _merge.AbortCherryPickAsync();

    public Task<GitCommandResult> RevertAsync(string hash) => _merge.RevertAsync(hash);

    public Task<GitCommandResult> StashPushAsync(string? message, bool includeUntracked) =>
        _stashes.PushAsync(message, includeUntracked);

    public Task<IReadOnlyList<GitStashEntry>> GetStashesAsync() => _stashes.GetStashesAsync();

    public Task<GitCommandResult> StashApplyAsync(string stashRef) => _stashes.ApplyAsync(stashRef);
    public Task<GitCommandResult> StashPopAsync(string stashRef) => _stashes.PopAsync(stashRef);
    public Task<GitCommandResult> StashDropAsync(string stashRef) => _stashes.DropAsync(stashRef);

    public Task<GitCommandResult> ApplyCachedPatchAsync(string patch, bool reverse) =>
        _diff.ApplyCachedPatchAsync(patch, reverse);

    public Task<GitCommandResult> ResetAsync(string hash, GitResetMode mode) => _rebase.ResetAsync(hash, mode);

    public Task<string> GetCommitMessageAsync(string hash) => _rebase.GetCommitMessageAsync(hash);

    public Task<GitCommandResult> RewriteCommitMessageAsync(string hash, string message) =>
        _rebase.RewriteCommitMessageAsync(hash, message);

    public Task<GitCommandResult> SquashAsync(IReadOnlyList<string> hashes, string? commitMessage = null) =>
        _rebase.SquashAsync(hashes, commitMessage);

    public async Task<(IReadOnlyList<RebasePlanEntry> Entries, string? Error)> GetRebaseCandidatesAsync(string fromHash)
        => await _rebase.GetCandidatesAsync(fromHash).ConfigureAwait(false);

    public Task<GitCommandResult> InteractiveRebaseAsync(
        string fromHash, IReadOnlyList<RebasePlanEntry> plan, IReadOnlyDictionary<string, string> newMessages)
        => _rebase.InteractiveRebaseAsync(fromHash, plan, newMessages);

    public Task<GitCommandResult> RunAsync(params string[] args) => _runner.RunAsync(args);

}
