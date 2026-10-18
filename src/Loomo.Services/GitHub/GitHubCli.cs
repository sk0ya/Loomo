using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Processes;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.Services.GitHub;

/// <summary>
/// GitHub の PR／Issue を <c>gh</c>（GitHub CLI）越しに扱う（§24.27）。API を自前で叩かず gh に任せるのは、
/// 認証（<c>gh auth login</c> のトークン・SSO・Enterprise）をユーザーが既に持っている形のまま使えるから——
/// Loomo が資格情報を預からない。リポジトリも gh がリモートから決めるので、作業フォルダーを渡すだけでよい。
/// <para>非対話で動かす（標準入力は閉じ、プロンプト・ページャー・更新通知を切る）。出力は専用スレッドで読む
/// （<see cref="ChildProcessIo"/>）。</para>
/// </summary>
public sealed class GitHubCli
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>gh を入れるコマンド（可視ターミナルで打つ）。</summary>
    public const string InstallCommand = "winget install --id GitHub.cli -e";

    /// <summary>gh が PATH にあるか。</summary>
    public static bool IsInstalled => ExecutableResolver.IsOnPath("gh");

    public async Task<IReadOnlyList<GitHubPullRequest>> ListPullRequestsAsync(string folder)
        => GitHubJson.ParsePullRequests(Ok(await RunAsync(folder,
            "pr", "list", "--state", "open", "--limit", "50", "--json", GitHubJson.PullRequestListFields)));

    public async Task<GitHubPullRequestDetail> GetPullRequestAsync(string folder, int number)
        => GitHubJson.ParsePullRequestDetail(Ok(await RunAsync(folder,
            "pr", "view", Num(number), "--json", GitHubJson.PullRequestViewFields)));

    public async Task<IReadOnlyList<GitHubIssue>> ListIssuesAsync(string folder)
        => GitHubJson.ParseIssues(Ok(await RunAsync(folder,
            "issue", "list", "--state", "open", "--limit", "50", "--json", GitHubJson.IssueListFields)));

    public async Task<GitHubIssueDetail> GetIssueAsync(string folder, int number)
        => GitHubJson.ParseIssueDetail(Ok(await RunAsync(folder,
            "issue", "view", Num(number), "--comments", "--json", GitHubJson.IssueViewFields)));

    /// <summary>今の作業ツリーで PR のブランチをチェックアウトする（<c>gh pr checkout</c>。フォークの PR も通る）。</summary>
    public Task<GitCommandResult> CheckoutAsync(string folder, int number)
        => RunAsync(folder, "pr", "checkout", Num(number));

    public Task<GitCommandResult> ReviewAsync(string folder, int number, GitHubReviewKind kind, string body)
    {
        var args = new List<string> { "pr", "review", Num(number), kind switch
        {
            GitHubReviewKind.Approve => "--approve",
            GitHubReviewKind.RequestChanges => "--request-changes",
            _ => "--comment",
        } };
        if (body.Trim().Length > 0)
        {
            args.Add("--body");
            args.Add(body);
        }
        return RunAsync(folder, args.ToArray());
    }

    public Task<GitCommandResult> CommentAsync(string folder, int number, bool isIssue, string body)
        => RunAsync(folder, isIssue ? "issue" : "pr", "comment", Num(number), "--body", body);

    /// <summary>
    /// gh を1回動かす。見つからない・時間切れも <see cref="GitCommandResult"/> の失敗として返す（例外にしない）。
    /// </summary>
    public async Task<GitCommandResult> RunAsync(string folder, params string[] args)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return new GitCommandResult(-1, "", "ワークスペースフォルダが開かれていません。");

        var startInfo = new ProcessStartInfo
        {
            FileName = "gh",
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.EnvironmentVariables["GH_PROMPT_DISABLED"] = "1";
        startInfo.EnvironmentVariables["GH_NO_UPDATE_NOTIFIER"] = "1";
        startInfo.EnvironmentVariables["GH_PAGER"] = "";
        startInfo.EnvironmentVariables["NO_COLOR"] = "1";
        startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return new GitCommandResult(-1, "", "gh を起動できませんでした。");
            process.StandardInput.Close();
            var stdout = ChildProcessIo.ReadToEndAsync(process.StandardOutput, "gh:stdout");
            var stderr = ChildProcessIo.ReadToEndAsync(process.StandardError, "gh:stderr");
            using var timeout = new CancellationTokenSource(Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new GitCommandResult(-1, "", $"gh がタイムアウトしました（{Timeout.TotalSeconds:0}秒）。");
            }
            return new GitCommandResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (Win32Exception)
        {
            return new GitCommandResult(-1, "", "gh（GitHub CLI）が見つかりません。");
        }
    }

    private static string Num(int number) => number.ToString(CultureInfo.InvariantCulture);

    /// <summary>失敗なら gh の言葉（未ログイン・GitHub のリポジトリではない 等）を理由にして投げる。</summary>
    private static string Ok(GitCommandResult result)
        => result.Success ? result.Output : throw new GitHubCliException(result.Message);
}

/// <summary>gh の失敗。<see cref="Exception.Message"/> は gh の出した理由。</summary>
public sealed class GitHubCliException(string message) : Exception(message);
