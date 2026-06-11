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

    private readonly IWorkspaceService _workspace;

    public GitService(IWorkspaceService workspace)
    {
        _workspace = workspace;
        workspace.RootChanged += (_, _) => RepositoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// リポジトリ状態が変わった可能性があるとき（更新系コマンド実行後・ワークスペース切替時）。
    /// 失敗時も発火する（コンフリクト中断などは失敗でも作業ツリーが動くため）。
    /// UI スレッドとは限らないので購読側でディスパッチすること。
    /// </summary>
    public event EventHandler? RepositoryChanged;

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

    /// <summary>全ブランチ込みのコミットグラフを取得する。空リポジトリは空リスト。</summary>
    public async Task<IReadOnlyList<GitLogRow>> GetLogAsync(int limit = 300)
    {
        var result = await RunAsync(
            "log", "--graph", "--all", $"-n{limit}",
            "--date=format:%Y-%m-%d %H:%M",
            $"--pretty=format:{GitLogParser.PrettyFormat}").ConfigureAwait(false);
        if (!result.Success)
            return Array.Empty<GitLogRow>();
        return GitLogParser.Parse(result.Output);
    }

    /// <summary>1ファイルの差分テキスト。未追跡ファイルは内容そのものを返す。</summary>
    public async Task<string> GetDiffTextAsync(GitChangeEntry entry, bool staged)
    {
        if (entry.IsUntracked)
        {
            var root = RootPath;
            if (root is null) return "";
            var full = Path.Combine(root, entry.Path);
            try
            {
                var content = File.Exists(full) ? await File.ReadAllTextAsync(full).ConfigureAwait(false) : "";
                return $"# 未追跡ファイル: {entry.Path}\n{content}";
            }
            catch (Exception ex)
            {
                return $"# 読み取り失敗: {ex.Message}";
            }
        }

        var args = staged
            ? new[] { "diff", "--cached", "--", entry.Path }
            : new[] { "diff", "--", entry.Path };
        var result = await RunAsync(args).ConfigureAwait(false);
        return result.Success ? result.Output : result.Message;
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

    // ===== 更新系（実行後は RepositoryChanged を発火） =====

    public Task<GitCommandResult> InitAsync() => MutateAsync("init");

    public Task<GitCommandResult> StageAsync(string path) => MutateAsync("add", "-A", "--", path);
    public Task<GitCommandResult> StageAllAsync() => MutateAsync("add", "-A");
    public Task<GitCommandResult> UnstageAsync(string path) => MutateAsync("restore", "--staged", "--", path);
    public Task<GitCommandResult> UnstageAllAsync() => MutateAsync("restore", "--staged", "--", ".");

    /// <summary>変更を破棄する。未追跡は削除（git clean）、追跡済みは作業ツリーを復元する。破壊的。</summary>
    public Task<GitCommandResult> DiscardAsync(GitChangeEntry entry) => entry.IsUntracked
        ? MutateAsync("clean", "-fd", "--", entry.Path)
        : MutateAsync("restore", "--", entry.Path);

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
    public Task<GitCommandResult> RebaseContinueAsync() => MutateAsync("rebase", "--continue");
    public Task<GitCommandResult> RebaseSkipAsync() => MutateAsync("rebase", "--skip");
    public Task<GitCommandResult> RebaseAbortAsync() => MutateAsync("rebase", "--abort");

    public Task<GitCommandResult> CherryPickAsync(string hash) => MutateAsync("cherry-pick", hash);
    public Task<GitCommandResult> CherryPickContinueAsync() => MutateAsync("cherry-pick", "--continue");
    public Task<GitCommandResult> CherryPickSkipAsync() => MutateAsync("cherry-pick", "--skip");
    public Task<GitCommandResult> CherryPickAbortAsync() => MutateAsync("cherry-pick", "--abort");

    public Task<GitCommandResult> RevertAsync(string hash) => MutateAsync("revert", "--no-edit", hash);

    public Task<GitCommandResult> ResetAsync(string hash, GitResetMode mode) =>
        MutateAsync("reset", $"--{mode.ToString().ToLowerInvariant()}", hash);

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
    public async Task<GitCommandResult> RunAsync(params string[] args)
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
