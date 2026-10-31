using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

public sealed record GitOperationStatus(bool IsBusy, bool IsError, string Message);

/// <summary>切替がローカル変更で止まったときの選択（§24.99）。</summary>
public enum CheckoutRefusalChoice
{
    Cancel,
    /// <summary>変更をスタッシュに退避して切り替える。</summary>
    Stash,
    /// <summary>変更を持ったまま切り替える（<c>git checkout -m</c>）。</summary>
    Carry,
}

/// <summary>Git セッションの更新系操作と実行状態を扱う Command Handler。</summary>
public sealed class GitSessionCommandHandler
{
    private readonly GitService _git;
    private int _busy;

    public GitSessionCommandHandler(GitService git) => _git = git;

    public event EventHandler<GitOperationStatus>? StatusChanged;

    public Task<GitCommandResult?> FetchAsync() => RunAsync("フェッチ", _git.FetchAsync);
    public Task<GitCommandResult?> PullAsync() => RunAsync("プル", _git.PullAsync);

    /// <summary>取り込み方を指定してプルする（マージ／リベース／早送りのみ）。</summary>
    public Task<GitCommandResult?> PullAsync(GitPullMode mode) =>
        RunAsync(PullLabel(mode), () => _git.PullAsync(mode));

    public Task<GitCommandResult?> PushAsync() => RunAsync("プッシュ", _git.PushAsync);

    /// <summary>強制プッシュ（<c>--force-with-lease</c>）。確認は呼び出し側（ビュー）の責務。</summary>
    public Task<GitCommandResult?> PushForceAsync() =>
        RunAsync("強制プッシュ", () => _git.PushAsync(force: true));

    public Task<GitCommandResult?> PullBranchAsync(GitBranchInfo branch) =>
        RunAsync($"{branch.Name} をプル", () => _git.PullBranchAsync(branch));
    public Task<GitCommandResult?> PushBranchAsync(
        GitBranchInfo branch, string? defaultRemote, bool force = false) =>
        RunAsync(force ? $"{branch.Name} を強制プッシュ" : $"{branch.Name} をプッシュ",
            () => _git.PushBranchAsync(branch, defaultRemote, force));

    /// <summary>リモート上のブランチを削除する（一覧の <c>origin/foo</c> 行に効く）。</summary>
    public Task<GitCommandResult?> DeleteRemoteBranchAsync(GitBranchInfo branch) =>
        RunAsync($"リモートから削除 {branch.Name}", () => _git.DeleteRemoteBranchAsync(branch.Name));

    public Task<GitCommandResult?> SetUpstreamAsync(GitBranchInfo branch, string upstream) =>
        RunAsync($"{branch.Name} の上流を {upstream} に設定",
            () => _git.SetUpstreamAsync(branch.Name, upstream));

    public Task<GitCommandResult?> UnsetUpstreamAsync(GitBranchInfo branch) =>
        RunAsync($"{branch.Name} の上流を解除", () => _git.UnsetUpstreamAsync(branch.Name));

    /// <summary>作業ツリーのファイルをそのコミット時点の内容へ戻す。</summary>
    public Task<GitCommandResult?> RestoreFileAtRevisionAsync(
        string hash, string shortHash, string relativePath, string? renamedTo = null) =>
        RunAsync($"{renamedTo ?? relativePath} を {shortHash} の内容へ戻す",
            () => _git.RestoreFileAtRevisionAsync(hash, relativePath, renamedTo));

    private static string PullLabel(GitPullMode mode) => mode switch
    {
        GitPullMode.Rebase => "プル（リベース）",
        GitPullMode.FastForwardOnly => "プル（早送りのみ）",
        _ => "プル",
    };

    /// <summary>
    /// ブランチへ切り替える。ローカル変更が切替先とぶつかって止まったら、<see cref="LocalChangesBlockCheckout"/> に
    /// 「退避して切り替える／持って切り替える／やめる」を聞いて、選ばれた方でやり直す（§24.99）。
    /// </summary>
    public async Task<GitCommandResult?> CheckoutBranchAsync(GitBranchInfo branch)
    {
        var label = $"チェックアウト {branch.Name}";
        var track = branch.IsRemote;
        var result = await RunAsync(label,
            () => track ? _git.CheckoutTrackAsync(branch.Name) : _git.CheckoutAsync(branch.Name));
        if (result is not { Success: false } || LocalChangesBlockCheckout is not { } ask
            || GitRefusal.CheckoutBlockingFiles(result.Error, out var untracked) is not { } files)
            return result;
        return ask(branch.Name, files, untracked) switch
        {
            CheckoutRefusalChoice.Stash => await RunAsync($"{label}（変更を退避）",
                () => _git.CheckoutStashingAsync(branch.Name, track)),
            CheckoutRefusalChoice.Carry => await RunAsync($"{label}（変更を持っていく）",
                () => _git.CheckoutCarryingAsync(branch.Name, track)),
            _ => result,
        };
    }

    /// <summary>切替がローカル変更で止まったときに人へ聞く口（ブランチ名・ぶつかったファイル・追跡していないファイルか）。
    /// ダイアログは App の責務なので外から渡す。null なら聞かずに git の文面のまま返す。</summary>
    public Func<string, IReadOnlyList<string>, bool, CheckoutRefusalChoice>? LocalChangesBlockCheckout { get; set; }
    public Task<GitCommandResult?> CreateBranchAsync(string name, string? startPoint = null) =>
        RunAsync($"ブランチ作成 {name}", () => _git.CreateBranchAsync(name, startPoint));
    public Task<GitCommandResult?> DeleteBranchAsync(GitBranchInfo branch, bool force) =>
        RunAsync($"ブランチ削除 {branch.Name}", () => _git.DeleteBranchAsync(branch.Name, force));

    public async Task<GitCommandResult?> MergeAsync(
        GitBranchInfo branch, GitMergeStrategy strategy = GitMergeStrategy.Default)
    {
        var label = strategy switch
        {
            GitMergeStrategy.FastForwardOnly => $"{branch.Name} をFast-forwardのみでマージ",
            GitMergeStrategy.NoFastForward => $"{branch.Name} をマージコミットを作成してマージ",
            GitMergeStrategy.Squash => $"{branch.Name} をスカッシュマージ",
            _ => $"{branch.Name} をマージ",
        };
        var result = await RunAsync(label, () => _git.MergeAsync(branch.Name, strategy));
        if (result is { Success: true } && strategy == GitMergeStrategy.Squash)
            StatusChanged?.Invoke(this, new(false, false,
                $"{label}してステージしました。内容を確認してコミットしてください。"));
        return result;
    }

    public Task<GitCommandResult?> RebaseAsync(GitBranchInfo branch) =>
        RunAsync($"{branch.Name} へリベース", () => _git.RebaseAsync(branch.Name));
    public Task<(IReadOnlyList<RebasePlanEntry> Entries, string? Error)> GetRebaseCandidatesAsync(GitLogRow row) =>
        row.Hash is null
            ? Task.FromResult<(IReadOnlyList<RebasePlanEntry>, string?)>((Array.Empty<RebasePlanEntry>(), null))
            : _git.GetRebaseCandidatesAsync(row.Hash);
    public Task<GitCommandResult?> InteractiveRebaseAsync(string fromHash, IReadOnlyList<RebasePlanEntry> plan,
        IReadOnlyDictionary<string, string> messages) =>
        RunAsync("インタラクティブリベース", () => _git.InteractiveRebaseAsync(fromHash, plan, messages));

    public Task<GitCommandResult?> CreateTagAsync(string name, string? target, string? message) =>
        RunAsync($"タグ作成 {name}", () => _git.CreateTagAsync(name, target,
            string.IsNullOrWhiteSpace(message) ? null : message));

    public Task<GitCommandResult?> DeleteTagAsync(GitTagInfo tag) =>
        RunAsync($"タグ削除 {tag.Name}", () => _git.DeleteTagAsync(tag.Name));
    public Task<GitCommandResult?> PushTagAsync(GitTagInfo tag) =>
        RunAsync($"タグ {tag.Name} をプッシュ", () => _git.PushTagAsync(tag.Name));
    public Task<GitCommandResult?> PushAllTagsAsync() => RunAsync("すべてのタグをプッシュ", _git.PushAllTagsAsync);
    public Task<GitCommandResult?> CheckoutTagAsync(GitTagInfo tag) =>
        RunAsync($"チェックアウト {tag.Name}", () => _git.CheckoutCommitAsync(tag.Name));

    public Task<GitCommandResult?> InitSubmoduleAsync(GitSubmoduleInfo submodule) =>
        RunAsync($"サブモジュール初期化 {submodule.Path}", () => _git.SubmoduleInitAsync(submodule.Path));
    public Task<GitCommandResult?> UpdateSubmoduleAsync(GitSubmoduleInfo submodule) =>
        RunAsync($"サブモジュール更新 {submodule.Path}", () => _git.SubmoduleUpdateAsync(submodule.Path));
    public Task<GitCommandResult?> SyncSubmodulesAsync() => RunAsync("サブモジュール同期", _git.SubmoduleSyncAsync);

    public Task<GitCommandResult?> AddWorktreeAsync(GitWorktreeAddRequest request) =>
        RunAsync($"ワークツリー作成 {Path.GetFileName(request.Path)}", () => _git.AddWorktreeAsync(request));
    public Task<GitCommandResult?> RemoveWorktreeAsync(GitWorktreeInfo worktree, bool force) =>
        RunAsync($"ワークツリー削除 {worktree.DisplayName}", () => _git.RemoveWorktreeAsync(worktree.Path, force));
    public Task<GitCommandResult?> PruneWorktreesAsync() => RunAsync("ワークツリーの掃除", _git.PruneWorktreesAsync);
    public Task<GitCommandResult?> LockWorktreeAsync(GitWorktreeInfo worktree, string? reason) =>
        RunAsync($"ワークツリーのロック {worktree.DisplayName}", () => _git.LockWorktreeAsync(worktree.Path, reason));
    public Task<GitCommandResult?> RepairWorktreeAsync(GitWorktreeInfo worktree, string movedPath) =>
        RunAsync($"ワークツリーを繋ぎ直す {worktree.DisplayName}", () => _git.RepairWorktreeAsync(movedPath));
    public Task<GitCommandResult?> UnlockWorktreeAsync(GitWorktreeInfo worktree) =>
        RunAsync($"ワークツリーのロック解除 {worktree.DisplayName}", () => _git.UnlockWorktreeAsync(worktree.Path));

    public Task<GitCommandResult?> CheckoutCommitAsync(GitLogRow row) => ForCommit(row, "チェックアウト", _git.CheckoutCommitAsync);
    public Task<GitCommandResult?> CherryPickAsync(GitLogRow row) => ForCommit(row, "チェリーピック", _git.CherryPickAsync);

    /// <summary>コミットを作らず、取り込んだ変更をステージ済みのまま作業ツリーへ残す。</summary>
    public Task<GitCommandResult?> CherryPickNoCommitAsync(GitLogRow row) =>
        ForCommit(row, "チェリーピック（コミットなし）", _git.CherryPickNoCommitAsync);

    public Task<GitCommandResult?> RevertAsync(GitLogRow row) => ForCommit(row, "リバート", _git.RevertAsync);

    /// <summary>打ち消しコミットを作らず、打ち消す変更だけ作業ツリーへ残す。</summary>
    public Task<GitCommandResult?> RevertNoCommitAsync(GitLogRow row) =>
        ForCommit(row, "リバート（コミットなし）", _git.RevertNoCommitAsync);
    public Task<GitCommandResult?> ResetAsync(GitLogRow row, GitResetMode mode) => row.Hash is null
        ? Task.FromResult<GitCommandResult?>(null)
        : RunAsync($"リセット（{mode.ToString().ToLowerInvariant()}）{row.ShortHash}",
            () => _git.ResetAsync(row.Hash, mode));
    public Task<string> GetCommitMessageAsync(GitLogRow row) => row.Hash is null
        ? Task.FromResult("") : _git.GetCommitMessageAsync(row.Hash);
    public Task<GitCommandResult?> RewriteCommitMessageAsync(GitLogRow row, string message) => row.Hash is null
        ? Task.FromResult<GitCommandResult?>(null)
        : RunAsync($"コミットメッセージ修正 {row.ShortHash}",
            () => _git.RewriteCommitMessageAsync(row.Hash, message));
    /// <summary>コミットから選んだファイルの変更を外し、作業ツリーへ戻す（§24.62）。</summary>
    public Task<GitCommandResult?> UncommitPathsAsync(GitLogRow row, IReadOnlyList<string> paths) => row.Hash is null
        ? Task.FromResult<GitCommandResult?>(null)
        : RunAsync($"コミットから外す {row.ShortHash}（{paths.Count} ファイル）",
            () => _git.UncommitPathsAsync(row.Hash, paths));
    /// <summary>書き換えるコミットがプッシュ済みなら、確認文の先頭に置く一文（§24.78）。どれもプッシュされていなければ null。
    /// 子孫がリモートにあれば祖先も必ずあるので、範囲のうち古いものほど引っかかる——渡された全部を見て和を取る。</summary>
    public async Task<string?> PublishedWarningAsync(IEnumerable<string?> hashes)
    {
        var remotes = new List<string>();
        foreach (var hash in hashes.OfType<string>().Distinct(StringComparer.Ordinal).Take(20))
            remotes.AddRange(await _git.GetRemoteBranchesContainingAsync(hash));
        return GitPublishedHistory.Describe(remotes.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary><paramref name="onto"/> へのリベースで作り直すコミットがプッシュ済みなら、その一文（§24.78）。</summary>
    /// <summary>いまのブランチへ <paramref name="branch"/> を取り込んだら衝突するファイル（§24.88）。調べられなければ null。</summary>
    public async Task<GitConflictForecast?> ForecastBranchConflictsAsync(string branch)
    {
        try
        {
            var forecast = await _git.ForecastBranchConflictsAsync(branch);
            return forecast.Error is null ? forecast : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;   // 予告は添え物。引けなくてもマージ・リベースそのものは止めない
        }
    }

    public async Task<string?> PublishedWarningForRebaseAsync(string onto)
        => await _git.GetOldestRewrittenByRebaseAsync(onto) is { } oldest
            ? await PublishedWarningAsync([oldest])
            : null;

    public Task<GitCommandResult?> SquashAsync(IReadOnlyList<GitLogRow> rows, string message)
    {
        var hashes = rows.Where(row => row.Hash is not null).Select(row => row.Hash!).ToList();
        return hashes.Count < 2 ? Task.FromResult<GitCommandResult?>(null)
            : RunAsync($"スカッシュ（{hashes.Count} 件）", () => _git.SquashAsync(hashes, message));
    }

    public Task<GitCommandResult?> ContinueAsync(GitStatusSnapshot status) =>
        status.RebaseInProgress ? RunAsync("リベース続行", _git.RebaseContinueAsync)
        : status.CherryPickInProgress ? RunAsync("チェリーピック続行", _git.CherryPickContinueAsync)
        : status.MergeInProgress ? RunAsync("マージ続行", _git.MergeContinueAsync)
        : Task.FromResult<GitCommandResult?>(null);
    public Task<GitCommandResult?> SkipAsync(GitStatusSnapshot status) =>
        status.RebaseInProgress ? RunAsync("リベーススキップ", _git.RebaseSkipAsync)
        : status.CherryPickInProgress ? RunAsync("チェリーピックスキップ", _git.CherryPickSkipAsync)
        : Task.FromResult<GitCommandResult?>(null);
    public Task<GitCommandResult?> AbortAsync(GitStatusSnapshot status) =>
        status.RebaseInProgress ? RunAsync("リベース中止", _git.RebaseAbortAsync)
        : status.CherryPickInProgress ? RunAsync("チェリーピック中止", _git.CherryPickAbortAsync)
        : status.MergeInProgress ? RunAsync("マージ中止", _git.MergeAbortAsync)
        : Task.FromResult<GitCommandResult?>(null);

    private Task<GitCommandResult?> ForCommit(
        GitLogRow row, string label, Func<string, Task<GitCommandResult>> operation) => row.Hash is null
        ? Task.FromResult<GitCommandResult?>(null)
        : RunAsync($"{label} {row.ShortHash}", () => operation(row.Hash));

    private async Task<GitCommandResult?> RunAsync(string label, Func<Task<GitCommandResult>> operation)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return null;
        StatusChanged?.Invoke(this, new(true, false, $"{label}を実行中…"));
        try
        {
            var result = await operation();
            StatusChanged?.Invoke(this, new(false, !result.Success,
                result.Success ? $"{label}が完了しました。" : Truncate(result.Message)));
            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static string Truncate(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }
}
