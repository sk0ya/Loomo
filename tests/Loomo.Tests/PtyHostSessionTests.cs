using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using sk0ya.Loomo.Pty.Host;
using sk0ya.Loomo.Services.Terminal;
using Terminal.Sessions;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 端末の常駐（§34）を、本物のシェルと本物の名前付きパイプで通す。ホストのサーバーはテストの
/// プロセス内で回す（パイプ名をテストごとに変えるので、動いている Loomo のホストとは出会わない）。
/// 肝は「接続を捨ててもシェルが生き残り、繋ぎ直すと前の画面が最初の出力として届く」こと。
/// </summary>
public sealed class PtyHostSessionTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private readonly string _pipeName = "loomo-pty-test-" + Guid.NewGuid().ToString("N");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-pty-test-" + Guid.NewGuid().ToString("N"));
    private readonly PtyHostClient _client;

    public PtyHostSessionTests()
    {
        Directory.CreateDirectory(_root);
        var server = new PtyHostServer(_pipeName, idleExit: null);
        new Thread(server.Run) { IsBackground = true, Name = "PtyHostServerUnderTest" }.Start();
        _client = new PtyHostClient(_pipeName, Path.Combine(_root, "bundled"), Path.Combine(_root, "installed"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private TerminalSessionRequest Request() => new(
        "cmd.exe", "cmd.exe /d /k prompt $G$G", _root, 80, 24,
        new Dictionary<string, string?>(), ScrollbackLimit: 1000);

    private sealed class Collector
    {
        private readonly StringBuilder _text = new();
        public readonly ConcurrentQueue<string> Chunks = new();
        public int? ExitCode;

        public Collector(ITerminalSession session)
        {
            session.OutputReceived += (_, text) =>
            {
                Chunks.Enqueue(text);
                lock (_text) _text.Append(text);
            };
            session.Exited += (_, code) => ExitCode = code;
        }

        public string Text
        {
            get { lock (_text) return _text.ToString(); }
        }

        public void WaitFor(Func<string, bool> condition, string what)
        {
            var watch = Stopwatch.StartNew();
            while (!condition(Text))
            {
                if (watch.Elapsed > Wait)
                    throw new TimeoutException($"{what} が届かない。出力: {Text}");
                Thread.Sleep(20);
            }
        }
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public async Task ShellSurvivesADroppedConnectionAndTheScreenComesBack()
    {
        var id = Guid.NewGuid();
        var workspace = Guid.NewGuid();

        var first = _client.OpenSession(id, workspace, Request());
        Assert.True(first.Created);
        var firstOutput = new Collector(first);
        first.Start();
        firstOutput.WaitFor(t => t.Contains(">>"), "プロンプト");
        first.Write("echo marker-123\r");
        // エコーされたコマンド行と、その実行結果で2回。
        firstOutput.WaitFor(t => Count(t, "marker-123") >= 2, "echo の結果");

        // Loomo が落ちた：殺さずに接続だけ切る。
        first.Abandon();

        var second = _client.OpenSession(id, workspace, Request());
        Assert.False(second.Created);
        var secondOutput = new Collector(second);
        second.Start();
        secondOutput.WaitFor(t => Count(t, "marker-123") >= 2, "再接続後のスナップショット");
        Assert.True(secondOutput.Chunks.TryPeek(out var snapshot));
        Assert.StartsWith("\u001b]7770;begin\u0007", snapshot);

        // 同じシェルがまだ対話できる。
        second.Write("echo after-reattach\r");
        secondOutput.WaitFor(t => Count(t, "after-reattach") >= 2, "再接続後の echo");

        var listed = await _client.ListAsync();
        var info = Assert.Single(listed, s => s.SessionId == id);
        Assert.Equal(workspace, info.WorkspaceId);
        Assert.True(info.Attached);

        // タブを閉じた：Dispose は殺す。
        second.Dispose();
        await WaitUntilAsync(async () => (await _client.ListAsync()).All(s => s.SessionId != id), "セッションの終了");
    }

    [Fact]
    public async Task ExitingTheShellEndsTheSessionAndReportsTheCode()
    {
        var id = Guid.NewGuid();
        var session = _client.OpenSession(id, Guid.NewGuid(), Request());
        var output = new Collector(session);
        session.Start();
        output.WaitFor(t => t.Contains(">>"), "プロンプト");

        session.Write("exit 7\r");

        output.WaitFor(_ => output.ExitCode is not null, "終了の通知");
        Assert.Equal(7, output.ExitCode);
        await WaitUntilAsync(async () => (await _client.ListAsync()).All(s => s.SessionId != id), "表からの削除");
    }

    [Fact]
    public async Task KillWorkspaceEndsOnlyThatWorkspacesSessions()
    {
        var doomedWorkspace = Guid.NewGuid();
        var keptWorkspace = Guid.NewGuid();
        var doomed = _client.OpenSession(Guid.NewGuid(), doomedWorkspace, Request());
        var kept = _client.OpenSession(Guid.NewGuid(), keptWorkspace, Request());
        doomed.Start();
        kept.Start();

        await _client.KillWorkspaceAsync(doomedWorkspace);

        await WaitUntilAsync(async () => (await _client.ListAsync()).All(s => s.WorkspaceId != doomedWorkspace), "削除");
        Assert.Contains(await _client.ListAsync(), s => s.WorkspaceId == keptWorkspace);
        kept.Dispose();
        doomed.Dispose();
    }

    [Fact]
    public void InstallCopiesTheBundleIntoAContentAddressedFolderOnce()
    {
        var bundled = Path.Combine(_root, "bundled");
        Directory.CreateDirectory(bundled);
        File.WriteAllText(Path.Combine(bundled, "sk0ya.Loomo.Pty.Host.exe"), "exe");
        File.WriteAllText(Path.Combine(bundled, "a.dll"), "one");
        File.WriteAllText(Path.Combine(bundled, "a.pdb"), "symbols");

        var first = _client.Install();
        var again = _client.Install();
        Assert.Equal(first, again);
        Assert.True(File.Exists(Path.Combine(first, "a.dll")));
        Assert.False(File.Exists(Path.Combine(first, "a.pdb")));

        // 中身が変われば別のフォルダー（古いホストが掴んでいる一式を上書きしない）。
        File.WriteAllText(Path.Combine(bundled, "a.dll"), "two");
        Assert.NotEqual(first, _client.Install());
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.Elapsed > Wait)
                throw new TimeoutException($"{what} が起きない");
            await Task.Delay(50);
        }
    }
}
