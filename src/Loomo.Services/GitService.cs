using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    /// 変更系 git コマンド（コミット・ステージ・プッシュ・ブランチ切替など、<see cref="MutateAsync"/>
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
    public async Task<string> GetDiffTextAsync(GitChangeEntry entry, bool staged, int contextLines = 3)
    {
        if (entry.IsUntracked)
        {
            var root = RootPath;
            if (root is null) return "";
            var full = Path.Combine(root, entry.Path);
            try
            {
                var content = File.Exists(full) ? await File.ReadAllTextAsync(full).ConfigureAwait(false) : "";
                return BuildUntrackedPatch(entry.Path, content);
            }
            catch (Exception ex)
            {
                return $"# 読み取り失敗: {ex.Message}";
            }
        }

        var unified = $"--unified={contextLines}";
        var args = staged
            ? new[] { "diff", "--cached", unified, "--", entry.Path }
            : new[] { "diff", unified, "--", entry.Path };
        var result = await RunAsync(args).ConfigureAwait(false);
        return result.Success ? result.Output : result.Message;
    }

    /// <summary>未追跡ファイルを「全行追加」の unified パッチ風テキストに整形する（差分表示で緑＋行番号にする）。</summary>
    private static string BuildUntrackedPatch(string path, string content)
    {
        var sb = new StringBuilder();
        sb.Append("# 未追跡ファイル: ").Append(path).Append('\n');
        if (content.Length == 0)
            return sb.ToString().TrimEnd('\n');

        var lines = content.Replace("\r\n", "\n").Split('\n');
        var count = lines.Length;
        if (count > 0 && lines[^1].Length == 0) count--; // 末尾改行ぶんの空要素は1行に数えない
        sb.Append("@@ -0,0 +1,").Append(count).Append(" @@\n");
        for (var i = 0; i < count; i++)
            sb.Append('+').Append(lines[i]).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

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
    public async Task<string?> GetConflictStageContentAsync(string path, int stage)
    {
        var result = await RunAsync("show", $":{stage}:{path}").ConfigureAwait(false);
        return result.Success ? result.Output : null;
    }

    /// <summary>コンフリクトの base/ours/theirs をまとめて取得する。作業ツリーにマーカーが書かれない
    /// コンフリクト種別（削除/変更の衝突・リネーム等）でファイル全体の解決を選ばせるためのフォールバック用。</summary>
    public async Task<(string? Base, string? Ours, string? Theirs)> GetConflictSidesAsync(string path)
    {
        var baseContent = await GetConflictStageContentAsync(path, 1).ConfigureAwait(false);
        var ours = await GetConflictStageContentAsync(path, 2).ConfigureAwait(false);
        var theirs = await GetConflictStageContentAsync(path, 3).ConfigureAwait(false);
        return (baseContent, ours, theirs);
    }

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

    public Task<GitCommandResult> RebaseAsync(string onto) => MutateAsync("rebase", onto);
    public async Task<GitCommandResult> RebaseContinueAsync()
    {
        var result = await MutateAsync("rebase", "--continue").ConfigureAwait(false);
        if (result.Success)
            await DeleteScriptedRebaseArtifactsAsync().ConfigureAwait(false);
        return result;
    }
    public Task<GitCommandResult> RebaseSkipAsync() => MutateAsync("rebase", "--skip");
    public async Task<GitCommandResult> RebaseAbortAsync()
    {
        var result = await MutateAsync("rebase", "--abort").ConfigureAwait(false);
        await DeleteScriptedRebaseArtifactsAsync().ConfigureAwait(false);
        return result;
    }

    public Task<GitCommandResult> CherryPickAsync(string hash) => _merge.CherryPickAsync(hash);
    public Task<GitCommandResult> CherryPickContinueAsync() => _merge.ContinueCherryPickAsync();
    public Task<GitCommandResult> CherryPickSkipAsync() => _merge.SkipCherryPickAsync();
    public Task<GitCommandResult> CherryPickAbortAsync() => _merge.AbortCherryPickAsync();

    public Task<GitCommandResult> RevertAsync(string hash) => _merge.RevertAsync(hash);

    // ===== スタッシュ =====

    /// <summary>現在の変更を退避する。<paramref name="includeUntracked"/> で未追跡ファイルも含める
    /// （-u）。退避するものが無ければ git が「No local changes to save」を返す。</summary>
    public Task<GitCommandResult> StashPushAsync(string? message, bool includeUntracked)
    {
        var args = new List<string> { "stash", "push" };
        if (includeUntracked)
            args.Add("-u");
        if (!string.IsNullOrWhiteSpace(message))
        {
            args.Add("-m");
            args.Add(message.Trim());
        }
        return MutateAsync(args.ToArray());
    }

    /// <summary>スタッシュ一覧（新しい＝stash@{0} が先頭）。</summary>
    public async Task<IReadOnlyList<GitStashEntry>> GetStashesAsync()
    {
        // %gd=参照（stash@{n}）, %x09=タブ, %gs=説明。
        var result = await RunAsync("stash", "list", "--format=%gd%x09%gs").ConfigureAwait(false);
        if (!result.Success)
            return Array.Empty<GitStashEntry>();

        var list = new List<GitStashEntry>();
        foreach (var line in result.Output.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.Length == 0) continue;
            var tab = l.IndexOf('\t');
            if (tab < 0)
                list.Add(new GitStashEntry(l, ""));
            else
                list.Add(new GitStashEntry(l[..tab], l[(tab + 1)..]));
        }
        return list;
    }

    /// <summary>スタッシュを作業ツリーへ復元する（退避は残す）。</summary>
    public Task<GitCommandResult> StashApplyAsync(string stashRef) => MutateAsync("stash", "apply", stashRef);
    /// <summary>スタッシュを作業ツリーへ復元し、その退避を削除する。</summary>
    public Task<GitCommandResult> StashPopAsync(string stashRef) => MutateAsync("stash", "pop", stashRef);
    /// <summary>スタッシュを削除する（復元しない・破壊的）。</summary>
    public Task<GitCommandResult> StashDropAsync(string stashRef) => MutateAsync("stash", "drop", stashRef);

    // ===== ハンク単位のステージ =====

    /// <summary>
    /// パッチ（最小ハンクパッチ）をインデックスへ適用する。<paramref name="reverse"/> が true なら逆適用
    /// （ステージ済みからのアンステージ）。git は末尾改行に厳しいので LF 固定＋末尾改行を保証して渡す。
    /// パッチは git ディレクトリ内の一時ファイル経由で渡す。
    /// </summary>
    public async Task<GitCommandResult> ApplyCachedPatchAsync(string patch, bool reverse)
    {
        var gitDir = await _runner.GetGitDirectoryAsync().ConfigureAwait(false);
        if (gitDir is null)
            return new GitCommandResult(-1, "", "git ディレクトリを特定できませんでした。");

        var patchPath = Path.Combine(gitDir, "loomo-hunk.patch");
        try
        {
            var normalized = patch.Replace("\r\n", "\n");
            if (!normalized.EndsWith('\n'))
                normalized += "\n";
            // BOM 無し UTF-8・LF で書く（git apply はパッチ書式に厳密）。
            await File.WriteAllTextAsync(patchPath, normalized, new UTF8Encoding(false)).ConfigureAwait(false);

            var args = new List<string> { "apply", "--cached", "--whitespace=nowarn" };
            if (reverse)
                args.Add("-R");
            args.Add(patchPath);
            return await MutateAsync(args.ToArray()).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(patchPath);
        }
    }

    public Task<GitCommandResult> ResetAsync(string hash, GitResetMode mode) =>
        MutateAsync("reset", $"--{mode.ToString().ToLowerInvariant()}", hash);

    /// <summary>コミットメッセージ全文を取得する（末尾の改行は除く）。</summary>
    public async Task<string> GetCommitMessageAsync(string hash)
    {
        var result = await RunAsync("show", "-s", "--format=%B", hash).ConfigureAwait(false);
        return result.Success ? result.Output.TrimEnd('\r', '\n') : "";
    }

    /// <summary>
    /// 現在のブランチ上のコミットメッセージを対話的リベースの reword で変更する。
    /// 対象より後のコミットも再作成される。マージを含む範囲は履歴構造を壊さないため拒否する。
    /// </summary>
    public async Task<GitCommandResult> RewriteCommitMessageAsync(string hash, string message)
    {
        try
        {
            return await RewriteCommitMessageCoreAsync(hash, message).ConfigureAwait(false);
        }
        finally
        {
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<GitCommandResult> RewriteCommitMessageCoreAsync(string hash, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new GitCommandResult(-1, "", "コミットメッセージを入力してください。");

        var onHead = await RunAsync("merge-base", "--is-ancestor", hash, "HEAD").ConfigureAwait(false);
        if (!onHead.Success)
            return new GitCommandResult(-1, "", "現在のブランチに含まれるコミットのみ修正できます。");

        var hasParent = (await RunAsync("rev-parse", "--verify", "--quiet", $"{hash}^").ConfigureAwait(false)).Success;
        var range = hasParent ? $"{hash}^..HEAD" : "HEAD";
        var chainResult = await RunAsync("rev-list", "--reverse", "--first-parent", range).ConfigureAwait(false);
        if (!chainResult.Success)
            return chainResult;
        var chain = SplitLines(chainResult.Output);
        if (chain.Count == 0 || !string.Equals(chain[0], hash, StringComparison.OrdinalIgnoreCase))
            return new GitCommandResult(-1, "", "現在のブランチの主系列にあるコミットのみ修正できます。");

        var merges = await RunAsync("rev-list", "--min-parents=2", range).ConfigureAwait(false);
        if (!merges.Success)
            return merges;
        if (SplitLines(merges.Output).Count > 0)
            return new GitCommandResult(-1, "", "対象から HEAD までにマージコミットがあるため、メッセージを修正できません。");

        var todo = new StringBuilder();
        todo.Append("reword ").Append(chain[0]).Append('\n');
        foreach (var commit in chain.Skip(1))
            todo.Append("pick ").Append(commit).Append('\n');

        var gitDir = await _runner.GetGitDirectoryAsync().ConfigureAwait(false);
        if (gitDir is null)
            return new GitCommandResult(-1, "", "git ディレクトリを特定できませんでした。");
        var todoPath = Path.Combine(gitDir, "loomo-reword-todo.txt");
        var messagePath = Path.Combine(gitDir, "loomo-reword-message.txt");
        try
        {
            await File.WriteAllTextAsync(todoPath, todo.ToString()).ConfigureAwait(false);
            await File.WriteAllTextAsync(messagePath, message.TrimEnd() + Environment.NewLine).ConfigureAwait(false);
            var env = new Dictionary<string, string>
            {
                ["GIT_SEQUENCE_EDITOR"] = $"cp '{ToMsysPath(todoPath)}'",
                ["GIT_EDITOR"] = $"cp '{ToMsysPath(messagePath)}'",
            };
            var baseArg = hasParent ? $"{hash}^" : "--root";
            return await _runner.RunAsync(env, "rebase", "-i", baseArg).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(todoPath);
            TryDelete(messagePath);
        }
    }

    /// <summary>
    /// 選択した連続するコミット群を1つにまとめる（squash）。<paramref name="hashes"/> は表示順・選択順に
    /// 依存せず、内部で古い→新しいに整列する。範囲は現在のブランチ上の線形・連続したコミットであること
    /// （途中に未選択コミットやマージ・分岐があれば失敗を返し、何も書き換えない）。<paramref name="commitMessage"/>
    /// 指定時は todo 内の exec でスカッシュ後のメッセージを確定する。未指定時は squash の既定どおり
    /// 各コミットのメッセージを連結する。上に積まれたコミットは pick で再適用する。
    /// コンフリクトが出た場合は通常のリベース進行中として続行・中止できる。
    /// </summary>
    public async Task<GitCommandResult> SquashAsync(IReadOnlyList<string> hashes, string? commitMessage = null)
    {
        try
        {
            return await SquashCoreAsync(hashes, commitMessage).ConfigureAwait(false);
        }
        finally
        {
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<GitCommandResult> SquashCoreAsync(IReadOnlyList<string> hashes, string? commitMessage)
    {
        if (hashes.Count < 2)
            return new GitCommandResult(-1, "", "スカッシュには2件以上のコミットを選択してください。");
        if (commitMessage is not null && string.IsNullOrWhiteSpace(commitMessage))
            return new GitCommandResult(-1, "", "コミットメッセージを入力してください。");

        // コミット日時は親子関係と逆転し得るため、新旧判定には使わない。
        // HEAD の主系列を古い順にたどり、その中で選択されたコミットを履歴上の順番に並べる。
        var resolved = new List<string>(hashes.Count);
        foreach (var hash in hashes)
        {
            var result = await RunAsync("rev-parse", "--verify", $"{hash}^{{commit}}").ConfigureAwait(false);
            if (!result.Success)
                return result;
            resolved.Add(result.Output.Trim());
        }
        var selected = resolved.ToHashSet(StringComparer.Ordinal);
        if (selected.Count < 2)
            return new GitCommandResult(-1, "", "スカッシュには2件以上のコミットを選択してください。");

        var historyResult = await RunAsync("rev-list", "--reverse", "--first-parent", "HEAD").ConfigureAwait(false);
        if (!historyResult.Success)
            return historyResult;
        var sel = historyResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(selected.Contains).ToList();
        if (sel.Count != selected.Count)
            return new GitCommandResult(-1, "",
                "現在のブランチに含まれる連続したコミットのみスカッシュできます。");
        var oldest = sel[0];
        var newest = sel[^1];

        // 根に親があるか（最初のコミットを含むなら --root リベース）。
        var hasParent = (await RunAsync("rev-parse", "--verify", "--quiet", $"{oldest}^").ConfigureAwait(false))
            .Success;

        // oldest→newest の線形な並びを取り、選択集合と完全一致するか（連続・線形）を検証する。
        var chainResult = hasParent
            ? await RunAsync("rev-list", "--reverse", $"{oldest}^..{newest}").ConfigureAwait(false)
            : await RunAsync("rev-list", "--reverse", newest).ConfigureAwait(false);
        if (!chainResult.Success)
            return chainResult;
        var chain = chainResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (chain.Count != sel.Count || !chain.ToHashSet().SetEquals(sel))
            return new GitCommandResult(-1, "",
                "連続したコミットを選択してください（範囲の途中に選択していないコミットやマージがあります）。");

        // rebase で作り直す範囲全体にマージがある場合、通常の pick では履歴構造を維持できない。
        var rewriteRange = hasParent ? $"{oldest}^..HEAD" : "HEAD";
        var merges = await RunAsync("rev-list", "--min-parents=2", rewriteRange).ConfigureAwait(false);
        if (!merges.Success)
            return merges;
        if (SplitLines(merges.Output).Count > 0)
            return new GitCommandResult(-1, "",
                "選択範囲から HEAD までにマージコミットがあるため、スカッシュできません。");

        // 先端より上に積まれているコミット（newest..HEAD）。これらは pick で再適用する。
        var aboveResult = await RunAsync("rev-list", "--reverse", "--first-parent", $"{newest}..HEAD").ConfigureAwait(false);
        if (!aboveResult.Success)
            return aboveResult;
        var above = aboveResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        var gitDir = await _runner.GetGitDirectoryAsync().ConfigureAwait(false);
        if (gitDir is null)
            return new GitCommandResult(-1, "", "git ディレクトリを特定できませんでした。");
        var messagePath = Path.Combine(gitDir, "loomo-squash-message.txt");

        // カスタムメッセージ時は fixup で編集画面を出さず、todo の exec で確実に適用する。
        // exec はコンフリクト解消後の rebase --continue でも残るため、環境変数だけに頼るより堅牢。
        var todo = new StringBuilder();
        todo.Append("pick ").Append(chain[0]).Append('\n');
        for (var i = 1; i < chain.Count; i++)
            todo.Append(commitMessage is null ? "squash " : "fixup ").Append(chain[i]).Append('\n');
        if (commitMessage is not null)
            todo.Append("exec git commit --amend -F '").Append(ToMsysPath(messagePath)).Append("'\n");
        foreach (var c in above)
            todo.Append("pick ").Append(c).Append('\n');

        var baseArg = hasParent ? $"{oldest}^" : "--root";
        var extraFiles = commitMessage is null
            ? null
            : new[] { ("loomo-squash-message.txt", commitMessage.TrimEnd() + Environment.NewLine) };
        return await RunScriptedRebaseAsync("loomo-squash-todo.txt", todo.ToString(), baseArg, extraFiles)
            .ConfigureAwait(false);
    }

    private static bool IsRebaseDirectoryPresent(string gitDir) =>
        Directory.Exists(Path.Combine(gitDir, "rebase-merge")) ||
        Directory.Exists(Path.Combine(gitDir, "rebase-apply"));

    /// <summary>
    /// todo（＋任意の追加ファイル。squash/reword の確定メッセージなど）を git ディレクトリへ書き、
    /// GIT_SEQUENCE_EDITOR をそれへ差し替えてから <c>git rebase -i baseArg</c> を実行する共通処理。
    /// 失敗時にリベースが一時停止中（コンフリクト等）なら追加ファイルは残す
    /// （未実行の exec がのちの rebase --continue でも必要なため）。
    /// </summary>
    private async Task<GitCommandResult> RunScriptedRebaseAsync(
        string todoFileName, string todoText, string baseArg,
        IReadOnlyList<(string FileName, string Content)>? extraFiles = null)
    {
        var gitDir = await _runner.GetGitDirectoryAsync().ConfigureAwait(false);
        if (gitDir is null)
            return new GitCommandResult(-1, "", "git ディレクトリを特定できませんでした。");

        var todoPath = Path.Combine(gitDir, todoFileName);
        var extraPaths = (extraFiles ?? Array.Empty<(string FileName, string Content)>())
            .Select(f => (Path: Path.Combine(gitDir, f.FileName), f.Content)).ToList();

        var keepExtraFiles = false;
        try
        {
            await File.WriteAllTextAsync(todoPath, todoText).ConfigureAwait(false);
            foreach (var (path, content) in extraPaths)
                await File.WriteAllTextAsync(path, content).ConfigureAwait(false);

            // GIT_SEQUENCE_EDITOR を「todo を我々の内容で上書きする」コマンドに差し替える
            // （git は値の後ろに todo ファイルのパスを付けて呼ぶ。git-for-windows の cp を使う）。
            var env = new Dictionary<string, string>
            {
                ["GIT_SEQUENCE_EDITOR"] = $"cp '{ToMsysPath(todoPath)}'",
            };
            var result = await _runner.RunAsync(env, "rebase", "-i", baseArg).ConfigureAwait(false);
            keepExtraFiles = !result.Success && IsRebaseDirectoryPresent(gitDir);
            return result;
        }
        finally
        {
            TryDelete(todoPath);
            if (!keepExtraFiles)
                foreach (var (path, _) in extraPaths)
                    TryDelete(path);
        }
    }

    private async Task DeleteScriptedRebaseArtifactsAsync()
    {
        var gitDir = await _runner.GetGitDirectoryAsync().ConfigureAwait(false);
        if (gitDir is null)
            return;
        TryDelete(Path.Combine(gitDir, "loomo-squash-message.txt"));
        try
        {
            foreach (var file in Directory.EnumerateFiles(gitDir, "loomo-rebase-msg-*.txt"))
                TryDelete(file);
        }
        catch { /* 列挙失敗（ディレクトリ消失等）は無視 */ }
    }

    // ===== インタラクティブリベース =====

    /// <summary>
    /// <paramref name="fromHash"/>..HEAD を対話的リベースの編集対象候補として返す（実行順＝古い→新しい、
    /// 既定アクションはすべて Pick）。現在のブランチの主系列でない、あるいはマージを含む範囲は
    /// <c>Entries</c> を空にし <c>Error</c> へ理由を入れて返す（<see cref="RewriteCommitMessageCoreAsync"/>
    /// と同じ検証）。
    /// </summary>
    public async Task<(IReadOnlyList<RebasePlanEntry> Entries, string? Error)> GetRebaseCandidatesAsync(string fromHash)
    {
        var onHead = await RunAsync("merge-base", "--is-ancestor", fromHash, "HEAD").ConfigureAwait(false);
        if (!onHead.Success)
            return (Array.Empty<RebasePlanEntry>(), "現在のブランチに含まれるコミットのみ対象にできます。");

        var hasParent = (await RunAsync("rev-parse", "--verify", "--quiet", $"{fromHash}^").ConfigureAwait(false)).Success;
        var range = hasParent ? $"{fromHash}^..HEAD" : "HEAD";

        var chainResult = await RunAsync("rev-list", "--reverse", "--first-parent", range).ConfigureAwait(false);
        if (!chainResult.Success)
            return (Array.Empty<RebasePlanEntry>(), chainResult.Message);
        var chain = SplitLines(chainResult.Output);
        if (chain.Count == 0 || !string.Equals(chain[0], fromHash, StringComparison.OrdinalIgnoreCase))
            return (Array.Empty<RebasePlanEntry>(), "現在のブランチの主系列にあるコミットのみ対象にできます。");

        var merges = await RunAsync("rev-list", "--min-parents=2", range).ConfigureAwait(false);
        if (!merges.Success)
            return (Array.Empty<RebasePlanEntry>(), merges.Message);
        if (SplitLines(merges.Output).Count > 0)
            return (Array.Empty<RebasePlanEntry>(),
                "対象から HEAD までにマージコミットがあるため、インタラクティブリベースできません。");

        var detail = await RunAsync("log", "--reverse", "--first-parent",
            "--pretty=format:%H%x1f%h%x1f%s", range).ConfigureAwait(false);
        if (!detail.Success)
            return (Array.Empty<RebasePlanEntry>(), detail.Message);

        var entries = new List<RebasePlanEntry>();
        foreach (var line in detail.Output.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.Length == 0) continue;
            var parts = l.Split('\x1f');
            if (parts.Length < 3) continue;
            entries.Add(new RebasePlanEntry(parts[0], parts[1], parts[2], RebaseAction.Pick));
        }
        return (entries, null);
    }

    /// <summary>
    /// <paramref name="plan"/>（順序・アクション）でスクリプト化した対話的リベースを実行する。
    /// <paramref name="newMessages"/> は Reword エントリの新しいメッセージ（ハッシュ→本文）。
    /// reword は git の native reword（都度停止）ではなく、各コミットを pick したうえで
    /// <c>exec git commit --amend -F &lt;専用ファイル&gt;</c> を差し込むことで実現する
    /// （GIT_EDITOR は常に固定値のため、複数の reword を区別できない）。
    /// コンフリクトは通常のリベース進行中として続行・スキップ・中止できる。
    /// </summary>
    public async Task<GitCommandResult> InteractiveRebaseAsync(
        string fromHash, IReadOnlyList<RebasePlanEntry> plan, IReadOnlyDictionary<string, string> newMessages)
    {
        try
        {
            return await InteractiveRebaseCoreAsync(fromHash, plan, newMessages).ConfigureAwait(false);
        }
        finally
        {
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<GitCommandResult> InteractiveRebaseCoreAsync(
        string fromHash, IReadOnlyList<RebasePlanEntry> plan, IReadOnlyDictionary<string, string> newMessages)
    {
        if (plan.Count == 0)
            return new GitCommandResult(-1, "", "リベース対象がありません。");

        // ダイアログ表示後にリポジトリ状態が変わっていないか再検証する。
        var onHead = await RunAsync("merge-base", "--is-ancestor", fromHash, "HEAD").ConfigureAwait(false);
        if (!onHead.Success)
            return new GitCommandResult(-1, "", "現在のブランチに含まれるコミットのみ対象にできます。");

        var hasParent = (await RunAsync("rev-parse", "--verify", "--quiet", $"{fromHash}^").ConfigureAwait(false)).Success;
        var range = hasParent ? $"{fromHash}^..HEAD" : "HEAD";
        var chainResult = await RunAsync("rev-list", "--reverse", "--first-parent", range).ConfigureAwait(false);
        if (!chainResult.Success)
            return chainResult;
        var chain = SplitLines(chainResult.Output);
        var planHashes = plan.Select(p => p.Hash).ToList();
        if (chain.Count != planHashes.Count || !chain.ToHashSet().SetEquals(planHashes))
            return new GitCommandResult(-1, "",
                "対象のコミット構成が変わったため実行できません。一覧を開き直してください。");

        var merges = await RunAsync("rev-list", "--min-parents=2", range).ConfigureAwait(false);
        if (!merges.Success)
            return merges;
        if (SplitLines(merges.Output).Count > 0)
            return new GitCommandResult(-1, "",
                "対象から HEAD までにマージコミットがあるため、インタラクティブリベースできません。");

        var firstNonDrop = plan.FirstOrDefault(p => p.Action != RebaseAction.Drop);
        if (firstNonDrop is null)
            return new GitCommandResult(-1, "", "少なくとも1件は pick / reword / edit にしてください。");
        if (firstNonDrop.Action is RebaseAction.Squash or RebaseAction.Fixup)
            return new GitCommandResult(-1, "", "先頭のコミットは pick / reword / edit のいずれかにしてください。");

        foreach (var entry in plan)
            if (entry.Action == RebaseAction.Reword && !newMessages.ContainsKey(entry.Hash))
                return new GitCommandResult(-1, "", $"{entry.ShortHash} の新しいメッセージが入力されていません。");

        var gitDir = await _runner.GetGitDirectoryAsync().ConfigureAwait(false);
        if (gitDir is null)
            return new GitCommandResult(-1, "", "git ディレクトリを特定できませんでした。");

        var extraFiles = new List<(string FileName, string Content)>();
        var todo = new StringBuilder();
        foreach (var entry in plan)
        {
            switch (entry.Action)
            {
                case RebaseAction.Drop:
                    todo.Append("drop ").Append(entry.Hash).Append('\n');
                    break;
                case RebaseAction.Squash:
                    todo.Append("squash ").Append(entry.Hash).Append('\n');
                    break;
                case RebaseAction.Fixup:
                    todo.Append("fixup ").Append(entry.Hash).Append('\n');
                    break;
                case RebaseAction.Edit:
                    todo.Append("edit ").Append(entry.Hash).Append('\n');
                    break;
                case RebaseAction.Reword:
                    var fileName = $"loomo-rebase-msg-{entry.Hash}.txt";
                    todo.Append("pick ").Append(entry.Hash).Append('\n');
                    todo.Append("exec git commit --amend -F '")
                        .Append(ToMsysPath(Path.Combine(gitDir, fileName))).Append("'\n");
                    extraFiles.Add((fileName, newMessages[entry.Hash].TrimEnd() + Environment.NewLine));
                    break;
                default:
                    todo.Append("pick ").Append(entry.Hash).Append('\n');
                    break;
            }
        }

        var baseArg = hasParent ? $"{fromHash}^" : "--root";
        return await RunScriptedRebaseAsync("loomo-rebase-todo.txt", todo.ToString(), baseArg, extraFiles)
            .ConfigureAwait(false);
    }

    private static List<string> SplitLines(string value) => value
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .ToList();

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 後始末の失敗は無視 */ }
    }

    /// <summary>Windows パスを git-for-windows の sh が解釈できる msys 形式へ（例: C:\a → /c/a）。</summary>
    private static string ToMsysPath(string path)
    {
        var p = path.Replace('\\', '/');
        if (p.Length >= 2 && p[1] == ':')
            p = "/" + char.ToLowerInvariant(p[0]) + p[2..];
        return p;
    }

    // ===== 実行基盤 =====

    private async Task<GitCommandResult> MutateAsync(params string[] args)
        => await _mutations.ExecuteAsync(args).ConfigureAwait(false);

    /// <summary>git を起動し、終了まで待って stdout/stderr を返す。タイムアウト時はプロセスツリーごと kill。</summary>
    public Task<GitCommandResult> RunAsync(params string[] args) => _runner.RunAsync(args);

}
