using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private const int TimeoutMs = 120_000;
    /// <summary>ライブ監視のポーリング間隔。見えている間だけ、この間隔で軽く状態を見る。</summary>
    private const int PollIntervalMs = 1500;

    private readonly IWorkspaceService _workspace;
    // ライブ監視：FileSystemWatcher は使わず、git ビューが見えている間だけ軽量ポーリングする。
    private Timer? _pollTimer;
    private int _liveTrackers;
    private string? _lastSignature;
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    public GitService(IWorkspaceService workspace)
    {
        _workspace = workspace;
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
    /// 出力も同じなので、git status による .git/index の書き戻しでは発火しない。</summary>
    private async Task PollOnceAsync()
    {
        if (!_pollGate.Wait(0)) return;
        try
        {
            var root = RootPath;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            var result = await RunAsync("status", "--porcelain=v2", "--branch").ConfigureAwait(false);
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

    /// <summary>現在の状態（ブランチ・ahead/behind・変更一覧・進行中操作）を取得する。</summary>
    public async Task<GitStatusSnapshot> GetStatusAsync()
    {
        var result = await RunAsync("status", "--porcelain=v2", "--branch").ConfigureAwait(false);
        if (!result.Success)
            return new GitStatusSnapshot { IsRepository = false };

        var snapshot = GitStatusParser.Parse(result.Output);

        // 進行中操作（rebase/merge/cherry-pick）は .git 配下のマーカーで検出する
        var gitDir = await GetGitDirAsync().ConfigureAwait(false);
        if (gitDir is null)
            return snapshot;
        return snapshot with
        {
            RebaseInProgress = Directory.Exists(Path.Combine(gitDir, "rebase-merge"))
                || Directory.Exists(Path.Combine(gitDir, "rebase-apply")),
            MergeInProgress = File.Exists(Path.Combine(gitDir, "MERGE_HEAD")),
            CherryPickInProgress = File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")),
        };
    }

    /// <summary>ローカル・リモートのブランチ一覧。リモートの HEAD ポインタ（origin/HEAD）は除く。</summary>
    public async Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync()
    {
        var result = await RunAsync(
            "branch", "-a", "--format=%(refname)\t%(HEAD)\t%(upstream:short)").ConfigureAwait(false);
        if (!result.Success)
            return Array.Empty<GitBranchInfo>();

        var branches = new List<GitBranchInfo>();
        foreach (var line in result.Output.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.Length == 0) continue;
            var parts = l.Split('\t');
            if (parts.Length < 2) continue;

            var refName = parts[0];
            var upstream = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;
            if (refName.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                branches.Add(new GitBranchInfo(
                    refName["refs/heads/".Length..], parts[1] == "*", IsRemote: false, upstream));
            }
            else if (refName.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                var name = refName["refs/remotes/".Length..];
                if (name.EndsWith("/HEAD", StringComparison.Ordinal))
                    continue;
                branches.Add(new GitBranchInfo(name, IsCurrent: false, IsRemote: true, upstream));
            }
        }
        return branches;
    }

    /// <summary>
    /// コミットグラフを取得する。<paramref name="branchRef"/> 指定時はそのブランチ（ref）のみ、
    /// null/空のときは全ブランチ込み（--all）。空リポジトリは空リスト。
    /// </summary>
    public async Task<IReadOnlyList<GitLogRow>> GetLogAsync(string? branchRef = null, int limit = 300)
    {
        var revArg = string.IsNullOrWhiteSpace(branchRef) ? "--all" : branchRef;
        var result = await RunAsync(
            "log", "--graph", revArg, $"-n{limit}",
            "--date=format:%Y-%m-%d %H:%M",
            $"--pretty=format:{GitLogParser.PrettyFormat}").ConfigureAwait(false);
        if (!result.Success)
            return Array.Empty<GitLogRow>();
        return GitLogParser.Parse(result.Output);
    }

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
    public async Task<string> GetCommitSummaryAsync(string hash)
    {
        // --stat は diff 出力形式の指定なので、既定のパッチ表示は付かず統計のみになる
        var result = await RunAsync("show", "--stat", "--format=fuller", hash).ConfigureAwait(false);
        return result.Success ? result.Output : result.Message;
    }

    /// <summary>コミットのフルパッチ（git show）。エディタの仮想ドキュメント表示用。</summary>
    public async Task<string> GetCommitPatchAsync(string hash)
    {
        var result = await RunAsync("show", hash).ConfigureAwait(false);
        return result.Success ? result.Output : result.Message;
    }

    /// <summary>
    /// コミット範囲の変更ファイル一覧。<paramref name="fromHash"/> が null なら
    /// <paramref name="toHash"/> 1コミットの変更（親との diff。ルートコミット対応、マージは第1親と比較）、
    /// 指定があれば両端スナップショット間の diff（from..to の到達差ではなく単純比較）。
    /// </summary>
    public async Task<IReadOnlyList<GitCommitFileChange>> GetRangeChangesAsync(string? fromHash, string toHash)
    {
        var result = fromHash is null
            ? await RunAsync("diff-tree", "--root", "-r", "-m", "--first-parent",
                "--no-commit-id", "--name-status", toHash).ConfigureAwait(false)
            : await RunAsync("diff", "--name-status", fromHash, toHash).ConfigureAwait(false);
        if (!result.Success)
            return Array.Empty<GitCommitFileChange>();

        var list = new List<GitCommitFileChange>();
        foreach (var line in result.Output.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.Length == 0) continue;
            var parts = l.Split('\t');
            if (parts.Length < 2 || parts[0].Length == 0) continue;
            var status = parts[0][0]; // "R100" などのスコアは落とす
            var (path, orig) = parts.Length >= 3 ? (parts[2], parts[1]) : (parts[1], (string?)null);
            list.Add(new GitCommitFileChange(status, path, orig));
        }
        return list;
    }

    /// <summary>コミット範囲（<see cref="GetRangeChangesAsync"/> と同じ規約）の1ファイル差分テキスト。</summary>
    public async Task<string> GetRangeFileDiffAsync(
        string? fromHash, string toHash, GitCommitFileChange file, int contextLines = 3)
    {
        var unified = $"--unified={contextLines}";
        var args = new List<string>();
        if (fromHash is null)
            args.AddRange(new[]
                { "diff-tree", "--root", "-p", unified, "-m", "--first-parent", "--no-commit-id", toHash });
        else
            args.AddRange(new[] { "diff", unified, fromHash, toHash });
        args.Add("--");
        if (file.OrigPath is not null)
            args.Add(file.OrigPath);
        args.Add(file.Path);
        var result = await RunAsync(args.ToArray()).ConfigureAwait(false);
        return result.Success ? result.Output : result.Message;
    }

    // ===== 更新系（実行後は RepositoryChanged を発火） =====

    public Task<GitCommandResult> InitAsync() => MutateAsync("init");

    public Task<GitCommandResult> StageAsync(string path) => MutateAsync("add", "-A", "--", path);
    public Task<GitCommandResult> StageAllAsync() => MutateAsync("add", "-A");
    public Task<GitCommandResult> UnstageAsync(string path) => MutateAsync("restore", "--staged", "--", path);
    public Task<GitCommandResult> UnstageAllAsync() => MutateAsync("restore", "--staged", "--", ".");

    /// <summary>複数パスをまとめてステージする（git は 1 コマンドで複数パスを取れる）。空なら何もしない。</summary>
    public Task<GitCommandResult> StageAsync(IReadOnlyCollection<string> paths) => paths.Count == 0
        ? Task.FromResult(new GitCommandResult(0, "", ""))
        : MutateAsync(new[] { "add", "-A", "--" }.Concat(paths).ToArray());

    /// <summary>複数パスをまとめてアンステージする。空なら何もしない。</summary>
    public Task<GitCommandResult> UnstageAsync(IReadOnlyCollection<string> paths) => paths.Count == 0
        ? Task.FromResult(new GitCommandResult(0, "", ""))
        : MutateAsync(new[] { "restore", "--staged", "--" }.Concat(paths).ToArray());

    /// <summary>変更を破棄する。未追跡は削除（git clean）、追跡済みは作業ツリーを復元する。破壊的。</summary>
    public Task<GitCommandResult> DiscardAsync(GitChangeEntry entry) => entry.IsUntracked
        ? MutateAsync("clean", "-fd", "--", entry.Path)
        : MutateAsync("restore", "--", entry.Path);

    /// <summary>複数の変更をまとめて破棄する。未追跡（git clean）と追跡済み（git restore）でコマンドが
    /// 違うため分けて実行し、結果を1つに集約して返す。いずれかが失敗したら以降は実行しない。破壊的。</summary>
    public async Task<GitCommandResult> DiscardAsync(IReadOnlyCollection<GitChangeEntry> entries)
    {
        var untracked = entries.Where(e => e.IsUntracked).Select(e => e.Path).ToArray();
        var tracked = entries.Where(e => !e.IsUntracked).Select(e => e.Path).ToArray();
        GitCommandResult? last = null;
        if (untracked.Length > 0)
            last = await MutateAsync(new[] { "clean", "-fd", "--" }.Concat(untracked).ToArray()).ConfigureAwait(false);
        if (tracked.Length > 0 && (last is null || last.Success))
            last = await MutateAsync(new[] { "restore", "--" }.Concat(tracked).ToArray()).ConfigureAwait(false);
        return last ?? new GitCommandResult(0, "", "");
    }

    /// <summary>
    /// 縮約パッチを作業ツリーへ逆適用して、選んだ行の変更だけを破棄する（<c>git apply --reverse --recount</c>）。
    /// パッチは一時ファイルへ LF・UTF-8(BOMなし) で書き出して渡す。<paramref name="patch"/> は
    /// <see cref="sk0ya.Loomo.Core.Diff.UnifiedPatchEditor.BuildReverseDiscardPatch"/> が組み立てたもの。破壊的。
    /// </summary>
    public async Task<GitCommandResult> ApplyReverseDiscardPatchAsync(string patch)
    {
        var root = RootPath;
        if (string.IsNullOrEmpty(root))
            return new GitCommandResult(-1, "", "ワークスペースフォルダが開かれていません。");

        var temp = Path.Combine(Path.GetTempPath(), $"loomo-discard-{Guid.NewGuid():N}.patch");
        try
        {
            await File.WriteAllTextAsync(temp, patch.Replace("\r\n", "\n"), new UTF8Encoding(false))
                .ConfigureAwait(false);
            return await RunAsync("apply", "--reverse", "--recount", "--whitespace=nowarn", temp)
                .ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* 一時ファイルの後始末は失敗しても無視 */ }
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task<GitCommandResult> CommitAsync(string message, bool amend = false) => amend
        ? MutateAsync("commit", "--amend", "-m", message)
        : MutateAsync("commit", "-m", message);

    public Task<GitCommandResult> FetchAsync() => MutateAsync("fetch", "--all", "--prune");
    public Task<GitCommandResult> PullAsync() => MutateAsync("pull");

    /// <summary>push。上流未設定なら -u origin HEAD で自動再試行する。</summary>
    public async Task<GitCommandResult> PushAsync()
    {
        var result = await MutateAsync("push").ConfigureAwait(false);
        if (!result.Success && result.Error.Contains("no upstream", StringComparison.OrdinalIgnoreCase))
            result = await MutateAsync("push", "-u", "origin", "HEAD").ConfigureAwait(false);
        return result;
    }

    public Task<GitCommandResult> CheckoutAsync(string branch) => MutateAsync("checkout", branch);

    /// <summary>リモートブランチから追跡ローカルブランチを作ってチェックアウトする。</summary>
    public Task<GitCommandResult> CheckoutTrackAsync(string remoteBranch) =>
        MutateAsync("checkout", "--track", remoteBranch);
    public Task<GitCommandResult> CheckoutCommitAsync(string hash) => MutateAsync("checkout", "--detach", hash);

    public Task<GitCommandResult> CreateBranchAsync(string name, string? startPoint = null) =>
        startPoint is null
            ? MutateAsync("switch", "-c", name)
            : MutateAsync("switch", "-c", name, startPoint);

    public Task<GitCommandResult> DeleteBranchAsync(string name, bool force = false) =>
        MutateAsync("branch", force ? "-D" : "-d", name);

    public Task<GitCommandResult> MergeAsync(string branch) => MutateAsync("merge", "--no-edit", branch);
    public Task<GitCommandResult> MergeContinueAsync() => MutateAsync("merge", "--continue");
    public Task<GitCommandResult> MergeAbortAsync() => MutateAsync("merge", "--abort");

    public Task<GitCommandResult> RebaseAsync(string onto) => MutateAsync("rebase", onto);
    public async Task<GitCommandResult> RebaseContinueAsync()
    {
        var result = await MutateAsync("rebase", "--continue").ConfigureAwait(false);
        if (result.Success)
            await DeleteRebaseMessageFileAsync().ConfigureAwait(false);
        return result;
    }
    public Task<GitCommandResult> RebaseSkipAsync() => MutateAsync("rebase", "--skip");
    public async Task<GitCommandResult> RebaseAbortAsync()
    {
        var result = await MutateAsync("rebase", "--abort").ConfigureAwait(false);
        await DeleteRebaseMessageFileAsync().ConfigureAwait(false);
        return result;
    }

    public Task<GitCommandResult> CherryPickAsync(string hash) => MutateAsync("cherry-pick", hash);
    public Task<GitCommandResult> CherryPickContinueAsync() => MutateAsync("cherry-pick", "--continue");
    public Task<GitCommandResult> CherryPickSkipAsync() => MutateAsync("cherry-pick", "--skip");
    public Task<GitCommandResult> CherryPickAbortAsync() => MutateAsync("cherry-pick", "--abort");

    public Task<GitCommandResult> RevertAsync(string hash) => MutateAsync("revert", "--no-edit", hash);

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
        var gitDir = await GetGitDirAsync().ConfigureAwait(false);
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

        var gitDir = await GetGitDirAsync().ConfigureAwait(false);
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
            return await RunCoreAsync(env, "rebase", "-i", baseArg).ConfigureAwait(false);
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

        // 選択コミット自身を topo 順（新しい→古い）に並べ、両端（先端＝newest／根＝oldest）を取る。
        var ordered = await RunAsync(
            new[] { "rev-list", "--no-walk=sorted", "--topo-order" }.Concat(hashes).ToArray())
            .ConfigureAwait(false);
        if (!ordered.Success)
            return ordered;
        var sel = ordered.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (sel.Count < 2)
            return new GitCommandResult(-1, "", "スカッシュには2件以上のコミットを選択してください。");
        var newest = sel[0];
        var oldest = sel[^1];

        // 先端は現在の HEAD に到達可能（同一含む）でなければならない（別ブランチのコミットは対象外）。
        var onHead = await RunAsync("merge-base", "--is-ancestor", newest, "HEAD").ConfigureAwait(false);
        if (!onHead.Success)
            return new GitCommandResult(-1, "",
                "現在のブランチに含まれる連続したコミットのみスカッシュできます。");

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

        // 先端より上に積まれているコミット（newest..HEAD）。これらは pick で再適用する。
        var aboveResult = await RunAsync("rev-list", "--reverse", $"{newest}..HEAD").ConfigureAwait(false);
        if (!aboveResult.Success)
            return aboveResult;
        var above = aboveResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        var gitDir = await GetGitDirAsync().ConfigureAwait(false);
        if (gitDir is null)
            return new GitCommandResult(-1, "", "git ディレクトリを特定できませんでした。");
        var todoPath = Path.Combine(gitDir, "loomo-squash-todo.txt");
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

        var keepMessageForContinue = false;
        try
        {
            if (commitMessage is not null)
                await File.WriteAllTextAsync(messagePath, commitMessage.TrimEnd() + Environment.NewLine).ConfigureAwait(false);
            await File.WriteAllTextAsync(todoPath, todo.ToString()).ConfigureAwait(false);

            // GIT_SEQUENCE_EDITOR を「todo を我々の内容で上書きする」コマンドに差し替える
            // （git は値の後ろに todo ファイルのパスを付けて呼ぶ。git-for-windows の cp を使う）。
            var env = new Dictionary<string, string>
            {
                ["GIT_SEQUENCE_EDITOR"] = $"cp '{ToMsysPath(todoPath)}'",
            };
            var baseArg = hasParent ? $"{oldest}^" : "--root";
            var result = await RunCoreAsync(env, "rebase", "-i", baseArg).ConfigureAwait(false);
            keepMessageForContinue = !result.Success && IsRebaseDirectoryPresent(gitDir);
            return result;
        }
        finally
        {
            TryDelete(todoPath);
            if (!keepMessageForContinue)
                TryDelete(messagePath);
        }
    }

    private static bool IsRebaseDirectoryPresent(string gitDir) =>
        Directory.Exists(Path.Combine(gitDir, "rebase-merge")) ||
        Directory.Exists(Path.Combine(gitDir, "rebase-apply"));

    private async Task DeleteRebaseMessageFileAsync()
    {
        var gitDir = await GetGitDirAsync().ConfigureAwait(false);
        if (gitDir is not null)
            TryDelete(Path.Combine(gitDir, "loomo-squash-message.txt"));
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
    {
        try
        {
            return await RunAsync(args).ConfigureAwait(false);
        }
        finally
        {
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>git を起動し、終了まで待って stdout/stderr を返す。タイムアウト時はプロセスツリーごと kill。</summary>
    public Task<GitCommandResult> RunAsync(params string[] args) => RunCoreAsync(null, args);

    /// <summary>
    /// <see cref="RunAsync"/> の実体。<paramref name="extraEnv"/> を指定すると既定の環境変数
    /// （GIT_EDITOR / GIT_SEQUENCE_EDITOR 等）を上書きできる（スカッシュの todo 差し替えに使う）。
    /// </summary>
    private async Task<GitCommandResult> RunCoreAsync(
        IReadOnlyDictionary<string, string>? extraEnv, params string[] args)
    {
        var root = RootPath;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return new GitCommandResult(-1, "", "ワークスペースフォルダが開かれていません。");

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // 非対話に固定：認証は失敗で返し、メッセージ編集は既定のまま確定する
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        psi.EnvironmentVariables["GIT_EDITOR"] = "true";
        psi.EnvironmentVariables["GIT_SEQUENCE_EDITOR"] = "true";
        if (extraEnv is not null)
            foreach (var (key, value) in extraEnv)
                psi.EnvironmentVariables[key] = value;
        // 出力を機械可読に固定（パスの \xxx エスケープと色を無効化）
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("color.ui=false");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return new GitCommandResult(-1, "", "git を起動できませんでした。");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeoutMs);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 既に終了 */ }
                return new GitCommandResult(-1, "", $"git がタイムアウトしました（{TimeoutMs / 1000}秒）。");
            }
            return new GitCommandResult(
                process.ExitCode,
                await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new GitCommandResult(-1, "",
                "git コマンドが見つかりません。Git for Windows をインストールして PATH を通してください。");
        }
    }

    /// <summary>.git ディレクトリの絶対パス（リポジトリ外なら null）。</summary>
    private async Task<string?> GetGitDirAsync()
    {
        var result = await RunAsync("rev-parse", "--git-dir").ConfigureAwait(false);
        if (!result.Success)
            return null;
        var dir = result.Output.Trim();
        if (dir.Length == 0)
            return null;
        return Path.IsPathRooted(dir) ? dir : Path.Combine(RootPath ?? "", dir);
    }
}
