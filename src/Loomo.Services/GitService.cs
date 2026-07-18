using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Services;

/// <summary>
/// git CLI を独立した非対話プロセスで実行する Git クライアントサービス。
/// ワークスペースルート（<see cref="IWorkspaceService.RootPath"/>）をリポジトリとして扱う。
/// 表示ターミナルには一切流さない（AI と同様、人間のターミナルを汚さない方針）。
/// 認証プロンプト・エディタ起動で固まらないよう GIT_TERMINAL_PROMPT=0 / GIT_EDITOR=true を強制する
/// （rebase --continue 等のメッセージ編集は既定メッセージのまま確定される）。
/// </summary>
public sealed class GitService
{
    /// <summary>ライブ監視のポーリング間隔。見えている間だけ、この間隔で軽く状態を見る。</summary>
    private const int PollIntervalMs = 1500;

    private readonly IWorkspaceService _workspace;
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
    // ライブ監視：FileSystemWatcher は使わず、git ビューが見えている間だけ軽量ポーリングする。
    private Timer? _pollTimer;
    private int _liveTrackers;
    private string? _lastSignature;
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    public GitService(IWorkspaceService workspace)
    {
        _workspace = workspace;
        _runner = new GitCommandRunner(workspace);
        _status = new GitStatusService(_runner);
        _history = new GitHistoryService(_runner);
        _mutations = new GitMutationExecutor(_runner);
        _branches = new GitBranchService(_runner, _mutations);
        _merge = new GitMergeService(_mutations);
        _submodules = new GitSubmoduleService(_runner, _mutations);
        _commits = new GitCommitService(workspace, _runner, _mutations);
        _stashes = new GitStashService(_runner, _mutations);
        _diff = new GitDiffService(workspace, _runner, _mutations);
        _rebase = new GitRebaseService(_runner, _mutations);
        _mutations.RepositoryChanged += (_, _) => RepositoryChanged?.Invoke(this, EventArgs.Empty);
        _mutations.OperationExecuted += (_, e) => OperationExecuted?.Invoke(this, e);
        workspace.RootChanged += (_, _) =>
        {
            _lastSignature = null; // リポジトリが替わったら署名を取り直す
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>
    /// リポジトリ状態が変わった可能性があるとき（更新系コマンド実行後・ワークスペース切替時・
    /// ワークスペース監視がファイル変化を検出したとき）。
    /// 失敗時も発火する（コンフリクト中断などは失敗でも作業ツリーが動くため）。
    /// UI スレッドとは限らないので購読側でディスパッチすること。
    /// </summary>
    public event EventHandler? RepositoryChanged;

    /// <summary>
    /// 変更系 git コマンド（コミット・ステージ・プッシュ・ブランチ切替など、<see cref="GitMutationExecutor"/>
    /// 経由の実行）が走ったときに発火する。照会系（status/log/diff）では発火しない。軌跡（操作ログ）が
    /// これを購読して Git 操作を記録する。成功・失敗どちらでも発火する（<see cref="GitOperationEventArgs.Success"/>
    /// で判別）。UI スレッドとは限らないので購読側でディスパッチすること。
    /// </summary>
    public event EventHandler<GitOperationEventArgs>? OperationExecuted;

    /// <summary>
    /// ライブ監視を開始する（戻り値を Dispose すると解除）。git ビュー（サイドバー Git パネル・
    /// Git/Diff ペイン）が画面に出ている間だけ呼び、その間 <see cref="PollIntervalMs"/> ごとに
    /// 作業ツリーの状態を軽くチェックして、変化したときだけ <see cref="RepositoryChanged"/> を発火する。
    /// FileSystemWatcher は使わない（ワークスペース全体の再帰監視はビルド成果物の大量変化で重く、
    /// 自前 git status の .git/index 書き戻しで誤発火もするため）。複数ビューから呼ばれても
    /// ポーリングは1つ（参照カウント）で、どのビューも見えていない間はゼロコスト。
    /// </summary>
    public IDisposable TrackLiveChanges()
    {
        if (Interlocked.Increment(ref _liveTrackers) == 1)
        {
            _lastSignature = null; // 開始直後は基準を取り直し、最初のチェックでは発火しない
            _pollTimer ??= new Timer(_ => _ = PollOnceAsync());
            _pollTimer.Change(PollIntervalMs, PollIntervalMs);
        }
        return new LiveTracker(this);
    }

    private void ReleaseLiveTracking()
    {
        if (Interlocked.Decrement(ref _liveTrackers) == 0)
            _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>作業ツリーの署名（git status の porcelain 出力）を取り、前回と違えば変化を通知する。
    /// 前回のポーリングがまだ走っていれば今回は飛ばす（多重起動しない）。状態が同じなら
    /// 出力も同じなので、git status による .git/index の書き戻しでは発火しない。
    /// <c>--no-optional-locks</c>必須：このポーリングは stage/commit と同じスレッドを介さず走るため、
    /// 素の git status がインデックスの統計キャッシュを書き戻そうとして進行中の commit と
    /// .git/index.lock を取り合い、コミットが「ステージだけ済んで失敗」することがあった。</summary>
    private async Task PollOnceAsync()
    {
        if (!_pollGate.Wait(0)) return;
        try
        {
            var root = RootPath;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            var result = await RunAsync("--no-optional-locks", "status", "--porcelain=v2", "--branch").ConfigureAwait(false);
            if (!result.Success) return;
            var prev = _lastSignature;
            _lastSignature = result.Output;
            if (prev is not null && !string.Equals(prev, result.Output, StringComparison.Ordinal))
                RepositoryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private sealed class LiveTracker : IDisposable
    {
        private GitService? _owner;
        public LiveTracker(GitService owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseLiveTracking();
    }

    public string? RootPath => _workspace.RootPath;

    // ===== 照会 =====

    /// <summary>現在の状態（ブランチ・ahead/behind・変更一覧・進行中操作）を取得する。
    /// <c>--no-optional-locks</c>の理由は <see cref="PollOnceAsync"/> 参照（進行中の commit とのロック競合回避）。</summary>
    public Task<GitStatusSnapshot> GetStatusAsync() => _status.GetStatusAsync();

    /// <summary>設定されているリモート名（git remote）。無ければ空。</summary>
    public Task<IReadOnlyList<string>> GetRemotesAsync() => _branches.GetRemotesAsync();

    /// <summary>
    /// ローカル・リモートのブランチ一覧。リモートの HEAD ポインタ（origin/HEAD）は除く。
    /// 上流との差（ahead/behind）と先頭コミット日時も併せて取り、1回の git 呼び出しで賄う。
    /// </summary>
    public Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync() => _branches.GetBranchesAsync();

    /// <summary>
    /// <c>%(upstream:track)</c> の値を分解する。取り得る形は
    /// <c>[ahead 1]</c> / <c>[behind 2]</c> / <c>[ahead 1, behind 2]</c> / <c>[gone]</c> / 空。
    /// 上流が無いブランチは空になる（この場合すべて既定値）。
    /// </summary>
    internal static (int Ahead, int Behind, bool Gone) ParseTrack(string track)
        => GitBranchService.ParseTrack(track);

    /// <summary>タグ一覧（作成日の新しい順）。</summary>
    public Task<IReadOnlyList<GitTagInfo>> GetTagsAsync() => _branches.GetTagsAsync();

    /// <summary>
    /// サブモジュール一覧（<c>git submodule status</c>）。<c>.gitmodules</c> が無い／サブモジュールが
    /// 無いリポジトリでは空リストを返す（エラーではない）。
    /// </summary>
    public Task<IReadOnlyList<GitSubmoduleInfo>> GetSubmodulesAsync() => _submodules.GetSubmodulesAsync();

    /// <summary>
    /// コミットグラフを取得する。<paramref name="branchRef"/> 指定時はそのブランチ（ref）のみ、
    /// null/空のときは全ブランチ込み（--all）。空リポジトリは空リスト。
    /// <paramref name="skip"/> は先頭から読み飛ばすコミット数（末尾スクロールの追加読み込み用ページング）。
    /// <paramref name="pathFilter"/> を指定すると、そのパス（リポジトリルート相対・ファイル／フォルダ）を
    /// 変更したコミットだけに絞る（<c>git log -- &lt;path&gt;</c>）。--graph はページごとにグラフを描き直すため、
    /// ページ境界で枝の接続は保証されない（表示上の割り切り）。
    /// </summary>
    public async Task<IReadOnlyList<GitLogRow>> GetLogAsync(
        string? branchRef = null, int limit = 300, int skip = 0, string? pathFilter = null)
        => await _history.GetLogAsync(branchRef, limit, skip, pathFilter).ConfigureAwait(false);

    /// <summary>
    /// 1ファイルの差分テキスト（unified patch）。<paramref name="contextLines"/> で前後コンテキスト行数を指定する
    /// （左右並びの全文表示では大きな値を渡してファイル全体を含める）。未追跡ファイルは全行を追加扱いの
    /// 合成パッチで返す。
    /// </summary>
    public Task<string> GetDiffTextAsync(GitChangeEntry entry, bool staged, int contextLines = 3) =>
        _diff.GetDiffTextAsync(entry, staged, contextLines);

    /// <summary>コミットの概要（メッセージ＋変更ファイル統計）。セッションペインの詳細表示用。</summary>
    public Task<string> GetCommitSummaryAsync(string hash) => _history.GetCommitSummaryAsync(hash);

    /// <summary>コミットのフルパッチ（git show）。エディタの仮想ドキュメント表示用。</summary>
    public Task<string> GetCommitPatchAsync(string hash) => _history.GetCommitPatchAsync(hash);

    /// <summary>
    /// コミット範囲の変更ファイル一覧。<paramref name="fromHash"/> が null なら
    /// <paramref name="toHash"/> 1コミットの変更（親との diff。ルートコミット対応、マージは第1親と比較）、
    /// 指定があれば両端スナップショット間の diff（from..to の到達差ではなく単純比較）。
    /// </summary>
    public Task<IReadOnlyList<GitCommitFileChange>> GetRangeChangesAsync(string? fromHash, string toHash) =>
        _history.GetRangeChangesAsync(fromHash, toHash);

    /// <summary>コミット範囲（<see cref="GetRangeChangesAsync"/> と同じ規約）の1ファイル差分テキスト。</summary>
    public async Task<string> GetRangeFileDiffAsync(
        string? fromHash, string toHash, GitCommitFileChange file, int contextLines = 3)
        => await _history.GetRangeFileDiffAsync(fromHash, toHash, file, contextLines).ConfigureAwait(false);

    /// <summary>コンフリクト中の1ステージ（1=共通祖先, 2=ours, 3=theirs）の内容。そのステージが無い
    /// （追加/削除の片方など）場合は null。</summary>
    public Task<string?> GetConflictStageContentAsync(string path, int stage) =>
        _diff.GetConflictStageContentAsync(path, stage);

    /// <summary>コンフリクトの base/ours/theirs をまとめて取得する。作業ツリーにマーカーが書かれない
    /// コンフリクト種別（削除/変更の衝突・リネーム等）でファイル全体の解決を選ばせるためのフォールバック用。</summary>
    public Task<(string? Base, string? Ours, string? Theirs)> GetConflictSidesAsync(string path) =>
        _diff.GetConflictSidesAsync(path);

    // ===== 更新系（実行後は RepositoryChanged を発火） =====

    public Task<GitCommandResult> InitAsync() => _commits.InitializeAsync();

    public Task<GitCommandResult> StageAsync(string path) => _commits.StageAsync(path);
    public Task<GitCommandResult> StageAllAsync() => _commits.StageAllAsync();
    public Task<GitCommandResult> UnstageAsync(string path) => _commits.UnstageAsync(path);
    public Task<GitCommandResult> UnstageAllAsync() => _commits.UnstageAllAsync();

    /// <summary>複数パスをまとめてステージする（git は 1 コマンドで複数パスを取れる）。空なら何もしない。</summary>
    public Task<GitCommandResult> StageAsync(IReadOnlyCollection<string> paths) => _commits.StageAsync(paths);

    /// <summary>複数パスをまとめてアンステージする。空なら何もしない。</summary>
    public Task<GitCommandResult> UnstageAsync(IReadOnlyCollection<string> paths) => _commits.UnstageAsync(paths);

    /// <summary>変更を破棄する。未追跡は削除（git clean）、追跡済みは作業ツリーを復元する。破壊的。</summary>
    public Task<GitCommandResult> DiscardAsync(GitChangeEntry entry) => _commits.DiscardAsync(entry);

    /// <summary>複数の変更をまとめて破棄する。未追跡（git clean）と追跡済み（git restore）でコマンドが
    /// 違うため分けて実行し、結果を1つに集約して返す。いずれかが失敗したら以降は実行しない。破壊的。</summary>
    public Task<GitCommandResult> DiscardAsync(IReadOnlyCollection<GitChangeEntry> entries) =>
        _commits.DiscardAsync(entries);

    /// <summary>
    /// 縮約パッチを作業ツリーへ逆適用して、選んだ行の変更だけを破棄する（<c>git apply --reverse --recount</c>）。
    /// パッチは一時ファイルへ LF・UTF-8(BOMなし) で書き出して渡す。<paramref name="patch"/> は
    /// <see cref="sk0ya.Loomo.Core.Diff.UnifiedPatchEditor.BuildReverseDiscardPatch"/> が組み立てたもの。破壊的。
    /// </summary>
    public Task<GitCommandResult> ApplyReverseDiscardPatchAsync(string patch) =>
        _commits.ApplyReverseDiscardPatchAsync(patch);

    /// <summary><paramref name="sign"/> が true なら <c>-S</c>（GPG署名）を付けてコミットする。
    /// 署名鍵未設定・gpg 未インストール等の失敗は git のエラー出力として <see cref="GitCommandResult"/>
    /// に返るだけで、ここでは特別扱いしない（呼び出し側が result.Message を表示する既存方針のまま）。</summary>
    public Task<GitCommandResult> CommitAsync(string message, bool amend = false, bool sign = false) =>
        _commits.CommitAsync(message, amend, sign);

    public Task<GitCommandResult> FetchAsync() => _branches.FetchAsync();
    public Task<GitCommandResult> PullAsync() => _branches.PullAsync();

    /// <summary>push。上流未設定なら -u origin HEAD で自動再試行する。</summary>
    public Task<GitCommandResult> PushAsync() => _branches.PushAsync();

    public Task<GitCommandResult> CheckoutAsync(string branch) => _branches.CheckoutAsync(branch);

    /// <summary>リモートブランチから追跡ローカルブランチを作ってチェックアウトする。</summary>
    public Task<GitCommandResult> CheckoutTrackAsync(string remoteBranch) =>
        _branches.CheckoutTrackAsync(remoteBranch);
    public Task<GitCommandResult> CheckoutCommitAsync(string hash) => _branches.CheckoutCommitAsync(hash);

    public Task<GitCommandResult> CreateBranchAsync(string name, string? startPoint = null) =>
        _branches.CreateBranchAsync(name, startPoint);

    public Task<GitCommandResult> DeleteBranchAsync(string name, bool force = false) =>
        _branches.DeleteBranchAsync(name, force);

    // ===== タグ =====

    /// <summary>タグを作成する。<paramref name="message"/> があれば注釈付き（-a -m）、無ければ軽量タグ。
    /// <paramref name="target"/> 省略時は HEAD。</summary>
    public Task<GitCommandResult> CreateTagAsync(string name, string? target = null, string? message = null)
        => _branches.CreateTagAsync(name, target, message);

    public Task<GitCommandResult> DeleteTagAsync(string name) => _branches.DeleteTagAsync(name);
    public Task<GitCommandResult> PushTagAsync(string name) => _branches.PushTagAsync(name);
    public Task<GitCommandResult> PushAllTagsAsync() => _branches.PushAllTagsAsync();

    // ===== サブモジュール =====

    /// <summary>サブモジュールを初期化する（<c>git submodule init</c>）。<paramref name="path"/> 省略時は全件。</summary>
    public Task<GitCommandResult> SubmoduleInitAsync(string? path = null) => string.IsNullOrEmpty(path)
        ? _submodules.InitializeAsync()
        : _submodules.InitializeAsync(path);

    /// <summary>
    /// サブモジュールを取得・更新する。既定は <c>git submodule update --init --recursive</c>
    /// （未初期化のものも一緒に初期化し、ネストしたサブモジュールも辿る）。
    /// <paramref name="path"/> 省略時は全サブモジュールが対象。
    /// </summary>
    public Task<GitCommandResult> SubmoduleUpdateAsync(string? path = null, bool init = true, bool recursive = true)
        => _submodules.UpdateAsync(path, init, recursive);

    /// <summary>
    /// サブモジュールの登録 URL の変更を作業ツリーの <c>.git/config</c> へ反映する
    /// （<c>git submodule sync --recursive</c>）。URL 変更後に <see cref="SubmoduleUpdateAsync"/> と
    /// セットで使う。
    /// </summary>
    public Task<GitCommandResult> SubmoduleSyncAsync() => _submodules.SynchronizeAsync();

    /// <summary>
    /// ブランチをマージする。<paramref name="strategy"/> で戦略を切り替える
    /// （<see cref="GitMergeStrategy.Squash"/> はステージするだけでコミットはしない。呼び出し側で
    /// 別途 <see cref="CommitAsync"/> を呼ぶこと）。コンフリクト時の扱いは戦略によらず同じで、
    /// 既存の <c>MergeInProgress</c> 検出・解決 UI がそのまま機能する。
    /// </summary>
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

    // ===== スタッシュ =====

    /// <summary>現在の変更を退避する。<paramref name="includeUntracked"/> で未追跡ファイルも含める
    /// （-u）。退避するものが無ければ git が「No local changes to save」を返す。</summary>
    public Task<GitCommandResult> StashPushAsync(string? message, bool includeUntracked) =>
        _stashes.PushAsync(message, includeUntracked);

    /// <summary>スタッシュ一覧（新しい＝stash@{0} が先頭）。</summary>
    public Task<IReadOnlyList<GitStashEntry>> GetStashesAsync() => _stashes.GetStashesAsync();

    /// <summary>スタッシュを作業ツリーへ復元する（退避は残す）。</summary>
    public Task<GitCommandResult> StashApplyAsync(string stashRef) => _stashes.ApplyAsync(stashRef);
    /// <summary>スタッシュを作業ツリーへ復元し、その退避を削除する。</summary>
    public Task<GitCommandResult> StashPopAsync(string stashRef) => _stashes.PopAsync(stashRef);
    /// <summary>スタッシュを削除する（復元しない・破壊的）。</summary>
    public Task<GitCommandResult> StashDropAsync(string stashRef) => _stashes.DropAsync(stashRef);

    // ===== ハンク単位のステージ =====

    /// <summary>
    /// パッチ（最小ハンクパッチ）をインデックスへ適用する。<paramref name="reverse"/> が true なら逆適用
    /// （ステージ済みからのアンステージ）。git は末尾改行に厳しいので LF 固定＋末尾改行を保証して渡す。
    /// パッチは git ディレクトリ内の一時ファイル経由で渡す。
    /// </summary>
    public Task<GitCommandResult> ApplyCachedPatchAsync(string patch, bool reverse) =>
        _diff.ApplyCachedPatchAsync(patch, reverse);

    public Task<GitCommandResult> ResetAsync(string hash, GitResetMode mode) => _rebase.ResetAsync(hash, mode);

    /// <summary>コミットメッセージ全文を取得する（末尾の改行は除く）。</summary>
    public Task<string> GetCommitMessageAsync(string hash) => _rebase.GetCommitMessageAsync(hash);

    /// <summary>
    /// 現在のブランチ上のコミットメッセージを対話的リベースの reword で変更する。
    /// 対象より後のコミットも再作成される。マージを含む範囲は履歴構造を壊さないため拒否する。
    /// </summary>
    public Task<GitCommandResult> RewriteCommitMessageAsync(string hash, string message) =>
        _rebase.RewriteCommitMessageAsync(hash, message);

    /// <summary>
    /// 選択した連続するコミット群を1つにまとめる（squash）。<paramref name="hashes"/> は表示順・選択順に
    /// 依存せず、内部で古い→新しいに整列する。範囲は現在のブランチ上の線形・連続したコミットであること
    /// （途中に未選択コミットやマージ・分岐があれば失敗を返し、何も書き換えない）。<paramref name="commitMessage"/>
    /// 指定時は todo 内の exec でスカッシュ後のメッセージを確定する。未指定時は squash の既定どおり
    /// 各コミットのメッセージを連結する。上に積まれたコミットは pick で再適用する。
    /// コンフリクトが出た場合は通常のリベース進行中として続行・中止できる。
    /// </summary>
    public Task<GitCommandResult> SquashAsync(IReadOnlyList<string> hashes, string? commitMessage = null) =>
        _rebase.SquashAsync(hashes, commitMessage);

    // ===== インタラクティブリベース =====

    /// <summary>
    /// <paramref name="fromHash"/>..HEAD を対話的リベースの編集対象候補として返す（実行順＝古い→新しい、
    /// 既定アクションはすべて Pick）。現在のブランチの主系列でない、あるいはマージを含む範囲は
    /// <c>Entries</c> を空にし <c>Error</c> へ理由を入れて返す（<see cref="RewriteCommitMessageCoreAsync"/>
    /// と同じ検証）。
    /// </summary>
    public async Task<(IReadOnlyList<RebasePlanEntry> Entries, string? Error)> GetRebaseCandidatesAsync(string fromHash)
        => await _rebase.GetCandidatesAsync(fromHash).ConfigureAwait(false);

    /// <summary>
    /// <paramref name="plan"/>（順序・アクション）でスクリプト化した対話的リベースを実行する。
    /// <paramref name="newMessages"/> は Reword エントリの新しいメッセージ（ハッシュ→本文）。
    /// reword は git の native reword（都度停止）ではなく、各コミットを pick したうえで
    /// <c>exec git commit --amend -F &lt;専用ファイル&gt;</c> を差し込むことで実現する
    /// （GIT_EDITOR は常に固定値のため、複数の reword を区別できない）。
    /// コンフリクトは通常のリベース進行中として続行・スキップ・中止できる。
    /// </summary>
    public Task<GitCommandResult> InteractiveRebaseAsync(
        string fromHash, IReadOnlyList<RebasePlanEntry> plan, IReadOnlyDictionary<string, string> newMessages)
        => _rebase.InteractiveRebaseAsync(fromHash, plan, newMessages);

    /// <summary>git を起動し、終了まで待って stdout/stderr を返す。タイムアウト時はプロセスツリーごと kill。</summary>
    public Task<GitCommandResult> RunAsync(params string[] args) => _runner.RunAsync(args);

}
