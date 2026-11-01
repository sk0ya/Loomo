using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Processes;

namespace sk0ya.Loomo.Services;

/// <summary>
/// git CLI のプロセス起動と入出力を一元管理する。
/// </summary>
public sealed class GitCommandRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private readonly GitRootState _rootState;
    private readonly TimeSpan _timeout;
    private readonly GitAskPassServer? _askPass;

    /// <param name="askPass">資格情報の問いを部屋の中で受ける口（§24.101）。null なら今まで通り、対話が要る操作はその場で失敗する。</param>
    public GitCommandRunner(GitRootState rootState, GitAskPassServer? askPass = null)
        : this(rootState, DefaultTimeout, askPass)
    {
    }

    internal GitCommandRunner(GitRootState rootState, TimeSpan timeout, GitAskPassServer? askPass = null)
    {
        _rootState = rootState;
        _timeout = timeout;
        _askPass = askPass;
    }

    public Task<GitCommandResult> RunAsync(params string[] args) =>
        RunAsync(null, CancellationToken.None, args);

    /// <summary>
    /// 中止できる呼び出しの時間制限。<b>中止できるなら時間では刈らない</b>——止める手段を人が持っているのに
    /// 既定の 120 秒で打ち切ると、大きなリポジトリ（20年ものの作業ツリーを丸ごと固める等）では正しく
    /// 待っている途中で失敗になり、しかも「遅い」と「失敗」の見分けがつかない。中止できない呼び出しは従来どおり。
    /// </summary>
    internal static TimeSpan? TimeoutFor(CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled ? Timeout.InfiniteTimeSpan : null;

    /// <summary>中止できる照会（時間制限は <see cref="TimeoutFor"/>）。中止されたら
    /// <see cref="OperationCanceledException"/>（git は木ごと止める）。</summary>
    internal Task<GitCommandResult> RunAsync(CancellationToken cancellationToken, params string[] args)
    {
        var root = _rootState.CurrentRoot;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return Task.FromResult(
                new GitCommandResult(-1, "", "ワークスペースフォルダが開かれていません。"));
        return RunInAsync(root, null, TimeoutFor(cancellationToken), cancellationToken, args);
    }

    internal async Task<string?> GetGitDirectoryAsync()
    {
        var result = await RunAsync("rev-parse", "--git-dir").ConfigureAwait(false);
        if (!result.Success)
            return null;
        var directory = result.Output.Trim();
        if (directory.Length == 0)
            return null;
        return Path.IsPathRooted(directory)
            ? directory
            : Path.Combine(_rootState.CurrentRoot ?? "", directory);
    }

    internal Task<GitCommandResult> RunAsync(
        IReadOnlyDictionary<string, string>? extraEnvironment,
        params string[] args) => RunAsync(extraEnvironment, CancellationToken.None, args);

    internal Task<GitCommandResult> RunAsync(
        IReadOnlyDictionary<string, string>? extraEnvironment,
        CancellationToken cancellationToken,
        params string[] args)
    {
        var root = _rootState.CurrentRoot;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return Task.FromResult(
                new GitCommandResult(-1, "", "ワークスペースフォルダが開かれていません。"));
        return RunInAsync(root, extraEnvironment, null, cancellationToken, args);
    }

    /// <summary>
    /// 作業ディレクトリを明示して git を実行する。<b>まだリポジトリでない場所</b>で走らせる操作
    /// （clone）のための入口で、リポジトリ内の操作は <see cref="RunAsync(string[])"/> を使う。
    /// <paramref name="timeout"/> は既定（<see cref="DefaultTimeout"/>）の上書き——clone は
    /// 大きなリポジトリだと分単位かかるので、2分で刈られると「途中まで落として失敗」になる。
    /// </summary>
    internal async Task<GitCommandResult> RunInAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string>? extraEnvironment,
        TimeSpan? timeout,
        CancellationToken cancellationToken,
        params string[] args)
    {
        if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
            return new GitCommandResult(-1, "", $"フォルダーがありません: {workingDirectory}");

        var startInfo = CreateStartInfo(workingDirectory, extraEnvironment, args);
        var limit = timeout ?? _timeout;
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return new GitCommandResult(-1, "", "git を起動できませんでした。");

            // 出力はプールではなく専用スレッドで読む（理由は ChildProcessIo）。打ち切りは下の
            // TryKill が担う——パイプが閉じれば読みは EOF で終わる。
            var stdout = ChildProcessIo.ReadToEndAsync(process.StandardOutput, "git:stdout");
            var stderr = ChildProcessIo.ReadToEndAsync(process.StandardError, "git:stderr");
            using var timeoutSource = limit == Timeout.InfiniteTimeSpan
                ? new CancellationTokenSource()
                : new CancellationTokenSource(limit);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                TryKill(process);
                return new GitCommandResult(-1, "",
                    $"git がタイムアウトしました（{limit.TotalSeconds:0}秒）。");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            return new GitCommandResult(
                process.ExitCode,
                await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false));
        }
        catch (Win32Exception)
        {
            return new GitCommandResult(-1, "",
                "git コマンドが見つかりません。Git for Windows をインストールして PATH を通してください。");
        }
    }

    private ProcessStartInfo CreateStartInfo(
        string root,
        IReadOnlyDictionary<string, string>? extraEnvironment,
        IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
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
        startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.EnvironmentVariables["GIT_EDITOR"] = "true";
        startInfo.EnvironmentVariables["GIT_SEQUENCE_EDITOR"] = "true";
        // 資格情報の問いは Loomo の入力欄へ（§24.101）。呼び出し側の環境が後から上書きできる——裏の取得は
        // 空の値で消して、問わずに失敗させる。
        if (_askPass?.EnvironmentFor(AskPassContext(args)) is { } askPass)
            foreach (var (key, value) in askPass)
                startInfo.EnvironmentVariables[key] = value;
        if (extraEnvironment is not null)
            foreach (var (key, value) in extraEnvironment)
            {
                if (value.Length == 0)
                    startInfo.EnvironmentVariables.Remove(key);
                else
                    startInfo.EnvironmentVariables[key] = value;
            }

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("color.ui=false");
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        return startInfo;
    }

    /// <summary>入力欄に出す「どの操作か」。サブコマンドと、その後ろの名前（リモート・ブランチ）まで——
    /// URL に資格情報が埋まっていることがあるので、<c>://</c> を含む引数は出さない。</summary>
    internal static string AskPassContext(IReadOnlyList<string> args)
    {
        var parts = new List<string> { "git" };
        foreach (var arg in args)
        {
            if (parts.Count >= 4) break;
            if (arg.StartsWith('-')) continue;
            if (arg.Contains("://", StringComparison.Ordinal) || arg.Contains('@')) continue;
            parts.Add(arg);
        }
        return string.Join(' ', parts);
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* 既に終了 */ }
    }
}
