using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Services;

/// <summary>
/// git CLI のプロセス起動と入出力を一元管理する。
/// </summary>
public sealed class GitCommandRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private readonly IWorkspaceService _workspace;
    private readonly TimeSpan _timeout;

    public GitCommandRunner(IWorkspaceService workspace)
        : this(workspace, DefaultTimeout)
    {
    }

    internal GitCommandRunner(IWorkspaceService workspace, TimeSpan timeout)
    {
        _workspace = workspace;
        _timeout = timeout;
    }

    public Task<GitCommandResult> RunAsync(params string[] args) =>
        RunAsync(null, CancellationToken.None, args);

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
            : Path.Combine(_workspace.RootPath ?? "", directory);
    }

    internal Task<GitCommandResult> RunAsync(
        IReadOnlyDictionary<string, string>? extraEnvironment,
        params string[] args) => RunAsync(extraEnvironment, CancellationToken.None, args);

    internal async Task<GitCommandResult> RunAsync(
        IReadOnlyDictionary<string, string>? extraEnvironment,
        CancellationToken cancellationToken,
        params string[] args)
    {
        var root = _workspace.RootPath;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return new GitCommandResult(-1, "", "ワークスペースフォルダが開かれていません。");

        var startInfo = CreateStartInfo(root, extraEnvironment, args);
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return new GitCommandResult(-1, "", "git を起動できませんでした。");

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = new CancellationTokenSource(_timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                TryKill(process);
                return new GitCommandResult(-1, "",
                    $"git がタイムアウトしました（{_timeout.TotalSeconds:0}秒）。");
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

    private static ProcessStartInfo CreateStartInfo(
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
        if (extraEnvironment is not null)
            foreach (var (key, value) in extraEnvironment)
                startInfo.EnvironmentVariables[key] = value;

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("color.ui=false");
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* 既に終了 */ }
    }
}
