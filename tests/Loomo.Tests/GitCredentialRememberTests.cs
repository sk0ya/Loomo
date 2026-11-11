using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using sk0ya.Loomo.Core.Git;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>「次から聞かれないようにする」（§24.101）——問いから覚えさせ方を読む・git の ssh の設定をどうするか。</summary>
public sealed class GitCredentialRememberParseTests
{
    [Fact]
    public void HTTPSのパスワードの問いから接続先とユーザー名を読む()
    {
        var offer = GitCredentialRemember.From(
            new GitAskPass.Request("Password for 'https://alice%40corp@git.example.com:8443': "), GitCredentialPromptKind.Secret);
        Assert.Equal(new GitCredentialRemember.Https("https://git.example.com:8443", "alice@corp"), offer);

        Assert.Equal(new GitCredentialRemember.Https("http://127.0.0.1:18765", null),
            GitCredentialRemember.From(new GitAskPass.Request("Password for 'http://127.0.0.1:18765': "), GitCredentialPromptKind.Secret));
    }

    [Fact]
    public void 鍵の合言葉の問いから鍵のWindowsのパスを読む()
    {
        Assert.Equal(new GitCredentialRemember.SshKey(@"C:\Users\me\.ssh\id_ed25519"),
            GitCredentialRemember.From(new GitAskPass.Request("Enter passphrase for key '/c/Users/me/.ssh/id_ed25519': "),
                GitCredentialPromptKind.Secret));
        Assert.Equal(new GitCredentialRemember.SshKey(@"C:\Users\me\.ssh\id_ed25519"),
            GitCredentialRemember.From(new GitAskPass.Request(@"Enter passphrase for key 'C:\Users\me/.ssh/id_ed25519': "),
                GitCredentialPromptKind.Secret));
    }

    [Fact]
    public void 覚えさせ方が読めない問いには出さない()
    {
        Assert.Null(GitCredentialRemember.From(new GitAskPass.Request("Username for 'https://host': "), GitCredentialPromptKind.Text));
        Assert.Null(GitCredentialRemember.From(new GitAskPass.Request("Enter PIN for ECDSA-SK key: "), GitCredentialPromptKind.Secret));
        Assert.Null(GitCredentialRemember.From(new GitAskPass.Request("Are you sure (yes/no)? "), GitCredentialPromptKind.YesNo));
    }

    [Theory]
    [InlineData(null, null, SshCommandPlan.Set)]
    [InlineData("C:/Windows/System32/OpenSSH/ssh.exe", null, SshCommandPlan.Keep)]
    [InlineData(@"""C:\Windows\System32\OpenSSH\ssh.exe""", null, SshCommandPlan.Keep)]
    [InlineData("plink.exe", null, SshCommandPlan.Conflict)]
    [InlineData(null, "C:/tools/ssh.exe", SshCommandPlan.Conflict)]
    public void gitのsshの設定は空なら向け_人の設定は上書きしない(string? configured, string? environment, SshCommandPlan expected)
        => Assert.Equal(expected, SshCommandPlanner.Decide(configured, environment, "C:/Windows/System32/OpenSSH/ssh.exe"));
}

/// <summary>
/// HTTPS：本物の git で、入力欄で答えて「覚えさせる」を選ぶと、認証が通ったときだけ資格情報ヘルパーへ保存され、次からは聞かれない。
/// git の全体の設定は一時ファイル（<c>GIT_CONFIG_GLOBAL</c>）、ヘルパーは <c>store --file</c> にして、本物の設定と GCM には触らない。
/// </summary>
public sealed class GitCredentialRememberHttpsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-git-remember", Guid.NewGuid().ToString("N"));
    private BasicAuthChallengeServer _remote = null!;
    private string _globalConfig = null!;
    private string _store = null!;
    private Dictionary<string, string> _environment = null!;

    private string RepoDir => Path.Combine(_root, "repo");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(RepoDir);
        _globalConfig = Path.Combine(_root, "global.gitconfig");
        File.WriteAllText(_globalConfig, "");
        _store = Path.Combine(_root, "credentials").Replace('\\', '/');
        _environment = new Dictionary<string, string>
        {
            ["GIT_CONFIG_GLOBAL"] = _globalConfig,
            ["GIT_CONFIG_NOSYSTEM"] = "1",   // システムの GCM に答えさせない
        };
        _remote = new BasicAuthChallengeServer { Accepts = BasicAuthChallengeServer.Basic("alice", "s3cret pass") };
        await Git("init");
        await Git("remote", "add", "origin", $"http://127.0.0.1:{_remote.Port}/repo.git");
    }

    public Task DisposeAsync()
    {
        _remote.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* 解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task 認証が通ったら資格情報ヘルパーへ覚えさせ_次は聞かれない()
    {
        var prompter = new FakePrompter(r => r.Prompt.StartsWith("Username", StringComparison.Ordinal) ? "alice" : "s3cret pass")
        { Remember = true };
        using var askPass = Server(prompter);

        var first = await Fetch(askPass);
        Assert.Contains("not found", first.Error, StringComparison.OrdinalIgnoreCase);   // 認証は通った
        var offered = Assert.Single(prompter.Requests, r => r.Offer is not null);
        Assert.Equal(new GitCredentialRemember.Https($"http://127.0.0.1:{_remote.Port}", "alice"), offered.Offer);

        await prompter.Reported.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var (message, success) = Assert.Single(prompter.Reports);
        Assert.True(success, message);
        Assert.Contains($"http://alice:s3cret%20pass@127.0.0.1%3a{_remote.Port}", File.ReadAllText(_store.Replace('/', '\\')),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"[credential \"http://127.0.0.1:{_remote.Port}\"]", File.ReadAllText(_globalConfig));

        // 2 回目：ヘルパーが答えるので、入力欄は出ない。
        var asked = prompter.Requests.Count;
        var second = await Fetch(askPass);
        Assert.Contains("not found", second.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(asked, prompter.Requests.Count);
    }

    [Fact]
    public async Task 認証が通らなければ覚えさせない()
    {
        var prompter = new FakePrompter(r => r.Prompt.StartsWith("Username", StringComparison.Ordinal) ? "alice" : "wrong")
        { Remember = true };
        using var askPass = Server(prompter);

        var result = await Fetch(askPass);

        Assert.Contains("Authentication failed", result.Error);
        await prompter.Reported.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(Assert.Single(prompter.Reports).Success);
        Assert.False(File.Exists(_store.Replace('/', '\\')));
        Assert.DoesNotContain("credential", File.ReadAllText(_globalConfig));
    }

    private GitAskPassServer Server(FakePrompter prompter)
        => new(prompter, Path.Combine(AppContext.BaseDirectory, GitAskPass.ExecutableName),
            registrar: new GitCredentialRegistrar(new FakeSshAgent(true), environment: _environment, httpsHelper: $"store --file={_store}"));

    private Task<GitCommandResult> Fetch(GitAskPassServer askPass)
    {
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(RepoDir);
        return new GitCommandRunner(new GitRootState(workspace), askPass).RunAsync(_environment, "fetch", "origin");
    }

    private async Task Git(params string[] args)
    {
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(RepoDir);
        var result = await new GitCommandRunner(new GitRootState(workspace)).RunAsync(_environment, args);
        Assert.True(result.Success, result.Error);
    }
}

/// <summary>
/// SSH：入力欄で合言葉に答えて「覚えさせる」を選ぶと、入力済みの合言葉が ssh-add へ（人に聞き直さずに）渡って鍵が agent に預けられ、
/// git の ssh の設定が agent を見る ssh へ向く。Windows の ssh-agent サービスは管理者の確認が要るので、ここでは Git 同梱の
/// ssh-agent／ssh-add で同じ流れを確かめる（サービスを起こす一手だけ <see cref="FakeSshAgent"/> に差し替える）。
/// </summary>
public sealed class GitCredentialRememberSshTests : IAsyncLifetime
{
    private static readonly string GitUsrBin =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-ssh-remember", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> _environment = new();
    private string? _agentPid;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        if (!File.Exists(Path.Combine(GitUsrBin, "ssh-agent.exe"))) return;
        var (_, output) = await Run(Path.Combine(GitUsrBin, "ssh-agent.exe"), null, "-s");
        _environment["SSH_AUTH_SOCK"] = Regex.Match(output, "SSH_AUTH_SOCK=([^;]+);").Groups[1].Value;
        _agentPid = Regex.Match(output, "SSH_AGENT_PID=([0-9]+);").Groups[1].Value;
        _environment["GIT_CONFIG_GLOBAL"] = Path.Combine(_root, "global.gitconfig");
        _environment["GIT_CONFIG_NOSYSTEM"] = "1";
        File.WriteAllText(_environment["GIT_CONFIG_GLOBAL"], "");
    }

    public async Task DisposeAsync()
    {
        if (_agentPid is { Length: > 0 })
        {
            var env = new Dictionary<string, string>(_environment) { ["SSH_AGENT_PID"] = _agentPid };
            await Run(Path.Combine(GitUsrBin, "ssh-agent.exe"), env, "-k");
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* 解放待ちは無視 */ }
    }

    [Fact]
    public async Task 入力済みの合言葉でsshAddし_gitのsshをagentを見るsshへ向ける()
    {
        if (_agentPid is null) return;   // Git for Windows が無い環境
        var key = Path.Combine(_root, "id_ed25519");
        Assert.Equal(0, (await Run(Path.Combine(GitUsrBin, "ssh-keygen.exe"), null, "-q", "-t", "ed25519", "-N", "open sesame", "-C", "loomo", "-f", key)).Exit);

        var prompter = new FakePrompter(_ => "open sesame") { Remember = true };
        var registrar = new GitCredentialRegistrar(new FakeSshAgent(true), openSshFolder: GitUsrBin, environment: _environment);
        using var server = new GitAskPassServer(prompter, Path.Combine(AppContext.BaseDirectory, GitAskPass.ExecutableName), registrar: registrar);

        // git が鍵の合言葉を問うたところを exe で再現する。
        var invocation = Guid.NewGuid().ToString("N");
        var env = new Dictionary<string, string>(server.EnvironmentFor("git push", invocation)!);
        var (exit, answer) = await Run(Path.Combine(AppContext.BaseDirectory, GitAskPass.ExecutableName), env,
            $"Enter passphrase for key '{key.Replace('\\', '/').Replace("C:", "/c")}': ");
        Assert.Equal(0, exit);
        Assert.Equal("open sesame\n", answer);
        Assert.Equal(new GitCredentialRemember.SshKey(key), Assert.Single(prompter.Requests).Offer);

        server.Complete(invocation, authenticated: true);
        await prompter.Reported.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var (message, success) = Assert.Single(prompter.Reports);
        Assert.True(success, message);
        Assert.Single(prompter.Requests);   // ssh-add の問いは人に聞き直していない

        var (_, listed) = await Run(Path.Combine(GitUsrBin, "ssh-add.exe"), _environment, "-l");
        Assert.Contains("loomo", listed);
        Assert.Contains(registrar.SshCommand, File.ReadAllText(_environment["GIT_CONFIG_GLOBAL"]));
    }

    [Fact]
    public async Task 人の設定したsshは上書きしない()
    {
        if (_agentPid is null) return;
        File.WriteAllText(_environment["GIT_CONFIG_GLOBAL"], "[core]\n\tsshCommand = plink.exe\n");
        var key = Path.Combine(_root, "id_other");
        await Run(Path.Combine(GitUsrBin, "ssh-keygen.exe"), null, "-q", "-t", "ed25519", "-N", "pp", "-C", "other", "-f", key);
        var prompter = new FakePrompter(_ => "pp");
        var registrar = new GitCredentialRegistrar(new FakeSshAgent(true), openSshFolder: GitUsrBin, environment: _environment);
        using var server = new GitAskPassServer(prompter, Path.Combine(AppContext.BaseDirectory, GitAskPass.ExecutableName), registrar: registrar);
        var (environment, _) = server.EnvironmentAnswering("ssh-add", "pp");

        var result = await registrar.RememberAsync(new GitCredentialRemember.SshKey(key), null, "pp", environment);

        Assert.True(result.Success, result.Message);
        Assert.Contains("別の ssh", result.Message);
        Assert.Contains("plink.exe", File.ReadAllText(_environment["GIT_CONFIG_GLOBAL"]));
        Assert.DoesNotContain(registrar.SshCommand, File.ReadAllText(_environment["GIT_CONFIG_GLOBAL"]));
    }

    [Fact]
    public async Task 管理者の確認が断られたら預けない()
    {
        var registrar = new GitCredentialRegistrar(new FakeSshAgent(false, startSucceeds: false), openSshFolder: GitUsrBin, environment: _environment);
        var key = Path.Combine(_root, "id_none");
        File.WriteAllText(key, "");
        var result = await registrar.RememberAsync(new GitCredentialRemember.SshKey(key), null, "pp", null);
        Assert.False(result.Success);
    }

    private static async Task<(int Exit, string Output)> Run(string exe, IReadOnlyDictionary<string, string>? environment, params string[] args)
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

internal sealed class FakeSshAgent(bool running, bool startSucceeds = true) : ISshAgentService
{
    public Task<bool?> IsRunningAsync() => Task.FromResult<bool?>(running);

    public Task<GitCredentialRegistrationResult> StartAsync() => Task.FromResult(startSucceeds
        ? new GitCredentialRegistrationResult(true, "起こした")
        : new GitCredentialRegistrationResult(false, "管理者の確認が取り消された"));
}
