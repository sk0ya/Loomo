using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using sk0ya.Loomo.Core.Git;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>git・ssh の資格情報の問いを部屋の中で受ける（§24.101）——問いの読み分け。</summary>
public sealed class GitAskPassClassifyTests
{
    [Theory]
    [InlineData("Username for 'https://github.com': ", null, GitCredentialPromptKind.Text)]
    [InlineData("Password for 'https://me@github.com': ", null, GitCredentialPromptKind.Secret)]
    [InlineData("Enter passphrase for key '/c/Users/me/.ssh/id_ed25519': ", null, GitCredentialPromptKind.Secret)]
    [InlineData("Enter PIN for ECDSA-SK key /c/Users/me/.ssh/id_ecdsa_sk: ", null, GitCredentialPromptKind.Secret)]
    [InlineData("The authenticity of host 'github.com (140.82.112.3)' can't be established.\nAre you sure you want to continue connecting (yes/no/[fingerprint])? ",
        null, GitCredentialPromptKind.YesNo)]
    [InlineData("Allow use of key /c/Users/me/.ssh/id_ed25519?", "confirm", GitCredentialPromptKind.Confirm)]
    [InlineData("Confirm user presence for key ECDSA-SK SHA256:abc", "none", GitCredentialPromptKind.Notice)]
    // 引用符の中（URL・ホスト名・鍵のパス）の語には釣られない。ユーザー名は見せたまま。
    [InlineData("Username for 'https://token.example.com': ", null, GitCredentialPromptKind.Text)]
    [InlineData("Username for 'https://passwords.corp.example': ", null, GitCredentialPromptKind.Text)]
    [InlineData("Password for 'https://me@token.example.com': ", null, GitCredentialPromptKind.Secret)]
    public void 問いの文面とsshの申告から入力欄の形を決める(string prompt, string? sshPrompt, GitCredentialPromptKind expected)
        => Assert.Equal(expected, GitAskPass.Classify(new GitAskPass.Request(prompt, sshPrompt)));

    [Fact]
    public void 入力欄に出す操作名にURLや資格情報を混ぜない()
    {
        Assert.Equal("git push origin main", GitCommandRunner.AskPassContext(["push", "-u", "origin", "main"]));
        Assert.Equal("git fetch", GitCommandRunner.AskPassContext(["fetch", "--all", "--prune"]));
        // URL には user:token が埋まっていることがある。
        Assert.Equal("git clone repo", GitCommandRunner.AskPassContext(["clone", "--progress", "https://me:tok@host/r.git", "repo"]));
        Assert.Equal("git clone repo", GitCommandRunner.AskPassContext(["clone", "git@github.com:me/r.git", "repo"]));
    }

    [Fact]
    public void 資格情報を問いうる操作にだけ入力欄を付ける()
    {
        Assert.True(GitCommandRunner.CanPrompt(["push", "-u", "origin", "HEAD"]));
        Assert.True(GitCommandRunner.CanPrompt(["fetch", "--all", "--prune"]));
        Assert.True(GitCommandRunner.CanPrompt(["-c", "credential.helper=", "clone", "x"]));
        Assert.True(GitCommandRunner.CanPrompt(["commit", "-m", "m"]));   // SSH 署名
        Assert.False(GitCommandRunner.CanPrompt(["status", "--porcelain"]));
        Assert.False(GitCommandRunner.CanPrompt(["--literal-pathspecs", "diff", "HEAD"]));
        Assert.False(GitCommandRunner.CanPrompt(["-c", "push.default=simple", "log"]));
    }

    [Fact]
    public async Task 上限を超える行は読まない()
    {
        using var stream = new MemoryStream(new byte[GitAskPass.MaxLineBytes + 10]);
        Assert.Null(await GitAskPass.ReadLineAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void 認証で止まった文面を読む()
    {
        Assert.True(GitRefusal.IsAuthenticationFailure("fatal: Authentication failed for 'https://github.com/me/r.git/'"));
        Assert.True(GitRefusal.IsAuthenticationFailure("fatal: could not read Username for 'https://github.com': terminal prompts disabled"));
        Assert.True(GitRefusal.IsAuthenticationFailure("git@github.com: Permission denied (publickey).\r\nfatal: Could not read from remote repository."));
        Assert.True(GitRefusal.IsAuthenticationFailure("Host key verification failed.\nfatal: Could not read from remote repository."));
        Assert.False(GitRefusal.IsAuthenticationFailure(" ! [rejected]        main -> main (fetch first)"));
        Assert.False(GitRefusal.IsAuthenticationFailure("fatal: repository 'https://x/y.git/' not found"));
        Assert.False(GitRefusal.IsAuthenticationFailure(null));
    }
}

/// <summary>同梱の exe とパイプの往復、git が居なくなったときの後始末（§24.101）。</summary>
public sealed class GitAskPassServerTests
{
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, GitAskPass.ExecutableName);

    [Fact]
    public async Task exeが問いを渡して答えを標準出力へ書く()
    {
        var prompter = new FakePrompter(_ => "pä ss ");
        using var server = new GitAskPassServer(prompter, HelperPath);
        var (exit, stdout) = await RunHelperAsync(server, "Password for 'https://me@host': ", context: "git push origin main");

        Assert.Equal(0, exit);
        // 合言葉は前後の空白と非 ASCII ごとそのまま（git も ssh も末尾の改行だけを落とす）。
        Assert.Equal("pä ss \n", stdout);
        var asked = Assert.Single(prompter.Requests);
        Assert.Equal("Password for 'https://me@host': ", asked.Request.Prompt);
        Assert.Equal("git push origin main", asked.Request.Context);
        Assert.Equal(GitCredentialPromptKind.Secret, asked.Kind);
    }

    [Fact]
    public async Task 取り消したらexeは何も書かずに失敗で終わる()
    {
        using var server = new GitAskPassServer(new FakePrompter(_ => null), HelperPath);
        var (exit, stdout) = await RunHelperAsync(server, "Username for 'https://host': ");
        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
    }

    [Fact]
    public async Task Loomoが居なければexeは失敗で終わる()
    {
        using var server = new GitAskPassServer(new FakePrompter(_ => "x"), HelperPath, pipeName: $"loomo-askpass-test-{Guid.NewGuid():N}");
        // 受け口を開かせない（EnvironmentFor を呼ばない）まま、その名前を指して起こす。
        var environment = new Dictionary<string, string> { [GitAskPass.PipeVariable] = server.PipeName };
        var (exit, stdout) = await RunHelperAsync(environment, "Username: ");
        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
    }

    [Fact]
    public async Task 問うたgitが居なくなったら入力欄を閉じる()
    {
        var asked = new TaskCompletionSource();
        var closed = new TaskCompletionSource();
        var prompter = new FakePrompter(async (_, cancellationToken) =>
        {
            asked.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { closed.TrySetResult(); }
            return null;
        });
        using var server = new GitAskPassServer(prompter, HelperPath);
        using var process = StartHelper(server.EnvironmentFor("git fetch")!, "Password: ");

        await asked.Task.WaitAsync(TimeSpan.FromSeconds(20));
        process.Kill(entireProcessTree: true);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void exeが無ければ何も足さない()
    {
        using var server = new GitAskPassServer(new FakePrompter(_ => "x"), Path.Combine(Path.GetTempPath(), "no-such-askpass.exe"));
        Assert.Null(server.EnvironmentFor("git push"));
    }

    private static async Task<(int Exit, string Stdout)> RunHelperAsync(GitAskPassServer server, string prompt, string context = "git")
        => await RunHelperAsync(server.EnvironmentFor(context)!, prompt);

    private static async Task<(int Exit, string Stdout)> RunHelperAsync(IReadOnlyDictionary<string, string> environment, string prompt)
    {
        using var process = StartHelper(environment, prompt);
        using var stdout = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(stdout).WaitAsync(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (process.ExitCode, Encoding.UTF8.GetString(stdout.ToArray()));
    }

    private static Process StartHelper(IReadOnlyDictionary<string, string> environment, string prompt)
    {
        var info = new ProcessStartInfo(HelperPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        info.ArgumentList.Add(prompt);
        foreach (var (key, value) in environment)
            info.Environment[key] = value;
        return Process.Start(info)!;
    }
}

/// <summary>
/// 本物の git が、資格情報を要る遠隔に対して askpass 経由で部屋に聞いてくるか（§24.101）。遠隔は 401 を返し続ける
/// 手元の HTTP サーバーで、git が送ってきた Authorization を見て、入力欄の答えがそのまま届いたことを確かめる。
/// </summary>
public sealed class GitAskPassGitTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "loomo-git-askpass", Guid.NewGuid().ToString("N"));
    private readonly BasicAuthChallengeServer _remote = new();
    private readonly FakePrompter _prompter = new(request =>
        request.Prompt.StartsWith("Username", StringComparison.Ordinal) ? "alice" : "s3cret pass");
    private GitAskPassServer _askPass = null!;
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        _askPass = new GitAskPassServer(_prompter, Path.Combine(AppContext.BaseDirectory, GitAskPass.ExecutableName));
        _git = new GitService(workspace, _askPass);
        await MustRunAsync("init");
        // 入っている資格情報ヘルパー（GCM など）に答えさせない——このテストは部屋の入力欄の経路を見る。
        await MustRunAsync("config", "credential.helper", "");
        await MustRunAsync("remote", "add", "origin", $"http://127.0.0.1:{_remote.Port}/repo.git");
    }

    public Task DisposeAsync()
    {
        _askPass.Dispose();
        _remote.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* git の解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task 人の取得はユーザー名とパスワードを部屋で聞いて遠隔へ送る()
    {
        var result = await _git.FetchAsync();

        Assert.False(result.Success);
        Assert.Contains(_prompter.Requests, r => r.Request.Prompt.StartsWith("Username for", StringComparison.Ordinal)
                                                 && r.Kind == GitCredentialPromptKind.Text);
        Assert.Contains(_prompter.Requests, r => r.Request.Prompt.StartsWith("Password for", StringComparison.Ordinal)
                                                 && r.Kind == GitCredentialPromptKind.Secret);
        Assert.All(_prompter.Requests, r => Assert.StartsWith("git fetch", r.Request.Context));
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cret pass"));
        Assert.Contains(expected, _remote.Authorizations);
        // 遠隔が受け付けなかったので、git の文面の前に次の一手が付く。
        Assert.StartsWith(GitRefusal.AuthenticationFailureMessage, result.Error);
    }

    [Fact]
    public async Task 人が答えていた時間はgitの時間制限に数えない()
    {
        // 制限 2 秒の git に、入力欄で 4 秒かけて答える。数えていれば答える前に git ごと刈られる。
        var slow = new FakePrompter(async (request, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            return request.Prompt.StartsWith("Username", StringComparison.Ordinal) ? "alice" : "s3cret pass";
        });
        using var askPass = new GitAskPassServer(slow, Path.Combine(AppContext.BaseDirectory, GitAskPass.ExecutableName));
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        var runner = new GitCommandRunner(new GitRootState(workspace), TimeSpan.FromSeconds(2), askPass);

        var result = await runner.RunAsync("fetch", "origin");

        Assert.DoesNotContain("タイムアウト", result.Error);
        Assert.Contains("Authentication failed", result.Error);
        Assert.Contains("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cret pass")), _remote.Authorizations);
    }

    [Fact]
    public async Task 裏の取得は部屋に聞かずに失敗する()
    {
        var result = await _git.FetchInBackgroundAsync();

        Assert.False(result.Success);
        Assert.Empty(_prompter.Requests);
        Assert.Empty(_remote.Authorizations);
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Error}");
        return result;
    }
}

/// <summary>
/// ssh の側：パスフレーズ付きの鍵を、端末の無い ssh が部屋に聞いて開けるか（§24.101）。<c>SSH_ASKPASS_REQUIRE=force</c> が
/// 効くのは OpenSSH 8.4 以降なので、Git for Windows 同梱の ssh と Windows の OpenSSH の両方で確かめる（入っているものだけ）。
/// </summary>
public sealed class GitAskPassSshTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-ssh-askpass", Guid.NewGuid().ToString("N"));

    public GitAskPassSshTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 解放待ちは無視 */ }
    }

    public static TheoryData<string> SshKeygens()
    {
        var data = new TheoryData<string>();
        foreach (var path in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin", "ssh-keygen.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh-keygen.exe"),
                 })
            if (File.Exists(path)) data.Add(path);
        return data;
    }

    [Theory]
    [MemberData(nameof(SshKeygens))]
    public async Task パスフレーズを部屋で聞いて鍵を開ける(string sshKeygen)
    {
        var key = Path.Combine(_root, Path.GetFileNameWithoutExtension(Path.GetDirectoryName(sshKeygen)!) + "_id");
        var (created, _) = await RunAsync(sshKeygen, null, "-q", "-t", "ed25519", "-N", "open sesame", "-C", "loomo", "-f", key);
        Assert.Equal(0, created);

        var prompter = new FakePrompter(_ => "open sesame");
        using var server = new GitAskPassServer(prompter, Path.Combine(AppContext.BaseDirectory, GitAskPass.ExecutableName));
        var (exit, output) = await RunAsync(sshKeygen, server.EnvironmentFor("git push"), "-y", "-f", key);

        Assert.Equal(0, exit);
        Assert.StartsWith("ssh-ed25519 ", output);
        var asked = Assert.Single(prompter.Requests);
        Assert.Contains("passphrase", asked.Request.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GitCredentialPromptKind.Secret, asked.Kind);
    }

    private static async Task<(int Exit, string Output)> RunAsync(string exe, IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var (k, v) in environment) info.Environment[k] = v;
        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (process.ExitCode, await stdout + await stderr);
    }
}

internal sealed class FakePrompter : IGitCredentialPrompter
{
    private readonly Func<GitAskPass.Request, CancellationToken, Task<string?>> _answer;

    public FakePrompter(Func<GitAskPass.Request, string?> answer)
        : this((request, _) => Task.FromResult(answer(request)))
    {
    }

    public FakePrompter(Func<GitAskPass.Request, CancellationToken, Task<string?>> answer) => _answer = answer;

    /// <summary>「次から聞かれないようにする」が出ていたら選ぶか。</summary>
    public bool Remember { get; init; }

    public ConcurrentQueue<(GitAskPass.Request Request, GitCredentialPromptKind Kind, GitCredentialRemember? Offer)> Requests { get; } = new();

    public ConcurrentQueue<(string Message, bool Success)> Reports { get; } = new();

    public TaskCompletionSource Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<GitCredentialAnswer?> AskAsync(GitAskPass.Request request, GitCredentialPromptKind kind,
        GitCredentialRemember? remember, CancellationToken cancellationToken)
    {
        Requests.Enqueue((request, kind, remember));
        var text = await _answer(request, cancellationToken);
        return text is null ? null : new GitCredentialAnswer(text, Remember && remember is not null);
    }

    public void Report(string message, bool success)
    {
        Reports.Enqueue((message, success));
        Reported.TrySetResult();
    }
}

/// <summary>どの要求にも 401（Basic 認証を求める）を返す HTTP サーバー。送られてきた Authorization を覚える。</summary>
internal sealed class BasicAuthChallengeServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public BasicAuthChallengeServer()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ConcurrentQueue<string> Authorizations { get; } = new();

    /// <summary>通す Authorization（<c>Basic …</c>）。null ならどれも通さない。</summary>
    public string? Accepts { get; init; }

    public static string Basic(string user, string password)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            string? line;
            string? authorization = null;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            {
                if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                    Authorizations.Enqueue(authorization = line["Authorization:".Length..].Trim());
            }
            // 正しい資格情報なら 404（認証は通った・リポジトリが無い）。それ以外は 401。
            var response = Encoding.ASCII.GetBytes(Accepts is not null && authorization == Accepts
                ? "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                : "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"loomo\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
