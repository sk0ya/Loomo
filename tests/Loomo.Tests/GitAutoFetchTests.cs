using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>裏での取得（§24.92）：いつ取りに行くかの判定と、実 git での取得。</summary>
public sealed class GitAutoFetchTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Ten = TimeSpan.FromMinutes(10);

    [Fact]
    public void 一度も取得していなければ取りに行く()
        => Assert.True(GitAutoFetchPolicy.ShouldFetch(Now, null, null, 0, Ten, justOpened: false));

    [Fact]
    public void 間隔より前に取得していれば取りに行かず_過ぎたら取りに行く()
    {
        Assert.False(GitAutoFetchPolicy.ShouldFetch(Now, Now.AddMinutes(-9), null, 0, Ten, justOpened: false));
        Assert.True(GitAutoFetchPolicy.ShouldFetch(Now, Now.AddMinutes(-10), null, 0, Ten, justOpened: false));
    }

    [Fact]
    public void 部屋を開いた直後は1分より古ければ取りに行く_ターミナルで今取ったばかりなら取り直さない()
    {
        Assert.True(GitAutoFetchPolicy.ShouldFetch(Now, Now.AddMinutes(-2), null, 0, Ten, justOpened: true));
        Assert.False(GitAutoFetchPolicy.ShouldFetch(Now, Now.AddSeconds(-30), null, 0, Ten, justOpened: true));
    }

    [Fact]
    public void 失敗が続けば間隔を広げ_上限でやめる()
    {
        // 1 回失敗：次は間隔の 2 倍まで待つ。
        Assert.False(GitAutoFetchPolicy.ShouldFetch(Now, null, Now.AddMinutes(-15), 1, Ten, justOpened: false));
        Assert.True(GitAutoFetchPolicy.ShouldFetch(Now, null, Now.AddMinutes(-20), 1, Ten, justOpened: false));
        // 開いた直後でも、失敗の待ちは守る（切替を繰り返して認証エラーを連打しない）。
        Assert.False(GitAutoFetchPolicy.ShouldFetch(Now, null, Now.AddMinutes(-1), 1, Ten, justOpened: true));
        Assert.False(GitAutoFetchPolicy.ShouldFetch(Now, null, Now.AddDays(-1), GitAutoFetchPolicy.MaxConsecutiveFailures, Ten, justOpened: false));
    }

    [Fact]
    public void 間隔は1分より短くしない()
        => Assert.Equal(TimeSpan.FromMinutes(1), GitAutoFetchPolicy.Interval(0));

    [Fact]
    public void ボタンに添える一言は最後に取得した時刻と自動の様子()
    {
        Assert.Contains("3 分前", GitAutoFetchPolicy.Describe(Now.AddMinutes(-3), Now, enabled: true, 10));
        Assert.Contains("10 分ごと", GitAutoFetchPolicy.Describe(Now.AddMinutes(-3), Now, enabled: true, 10));
        Assert.Contains("まだ取得していません", GitAutoFetchPolicy.Describe(null, Now, enabled: true, 10));
        Assert.Contains("切っています", GitAutoFetchPolicy.Describe(null, Now, enabled: false, 10));
    }

    // ---- 実 git：ローカルの bare リポジトリを遠隔に見立てる ----

    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-autofetch-tests", Guid.NewGuid().ToString("N"));
    private string Remote => Path.Combine(_root, "remote.git");
    private string Local => Path.Combine(_root, "local");
    private string Other => Path.Combine(_root, "other");
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await RunAsync(_root, "init", "--bare", "-b", "main", Remote);
        await RunAsync(_root, "clone", Remote, Other);
        await ConfigureAsync(Other);
        await File.WriteAllTextAsync(Path.Combine(Other, "a.txt"), "1\n");
        await RunAsync(Other, "add", "-A");
        await RunAsync(Other, "commit", "-m", "init");
        await RunAsync(Other, "push", "origin", "main");
        await RunAsync(_root, "clone", Remote, Local);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(Local);
        _git = new GitService(workspace);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task 遠隔に新しいコミットがあれば取得して変更を知らせ_無ければ知らせない()
    {
        var changed = 0;
        _git.RepositoryChanged += (_, _) => Interlocked.Increment(ref changed);

        var nothing = await _git.FetchInBackgroundAsync();
        Assert.True(nothing.Success, nothing.Message);
        Assert.Equal(0, changed);

        await File.WriteAllTextAsync(Path.Combine(Other, "a.txt"), "2\n");
        await RunAsync(Other, "commit", "-am", "second");
        await RunAsync(Other, "push", "origin", "main");

        var fetched = await _git.FetchInBackgroundAsync();
        Assert.True(fetched.Success, fetched.Message);
        Assert.Equal(1, changed);
        var behind = await _git.RunAsync("rev-list", "--count", "HEAD..origin/main");
        Assert.Equal("1", behind.Output.Trim());
        // 作業ツリーは動かさない（取得だけ）。
        Assert.Equal("1", (await File.ReadAllTextAsync(Path.Combine(Local, "a.txt"))).Trim());   // 改行は手元の autocrlf しだい
    }

    [Fact]
    public async Task FETCH_HEADの場所を引け_取得すると時刻が新しくなる()
    {
        var path = await _git.GetFetchHeadPathAsync();
        Assert.NotNull(path);
        var before = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        await Task.Delay(50);
        await _git.FetchInBackgroundAsync();
        Assert.True(File.Exists(path));
        Assert.True(File.GetLastWriteTimeUtc(path) >= before);
    }

    private static async Task ConfigureAsync(string repo)
    {
        // 同じ .git/config を同時に書くと鍵の取り合いになるので 1 つずつ。
        await RunAsync(repo, "config", "user.name", "Loomo Test");
        await RunAsync(repo, "config", "user.email", "loomo@example.invalid");
        await RunAsync(repo, "config", "core.autocrlf", "false");
    }

    private static async Task RunAsync(string cwd, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {await stderr}{await stdout}");
    }
}
