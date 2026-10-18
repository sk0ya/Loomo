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
    /// <summary>本番のホストが握るミューテックスの代役。クライアントはこれでホストの在否を見る。</summary>
    private readonly Mutex _hostMutex;

    public PtyHostSessionTests()
    {
        Directory.CreateDirectory(_root);
        var server = new PtyHostServer(_pipeName, idleExit: null);
        new Thread(server.Run) { IsBackground = true, Name = "PtyHostServerUnderTest" }.Start();
        _hostMutex = new Mutex(initiallyOwned: false, sk0ya.Loomo.Core.Pty.PtyProtocol.HostMutexName(_pipeName));
        _client = new PtyHostClient(_pipeName, Path.Combine(_root, "bundled"), Path.Combine(_root, "installed"));
    }

    public void Dispose()
    {
        _hostMutex.Dispose();
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
    public void TheFirstConnectionKeepsTheShellAndALaterOneIsTurnedAway()
    {
        var id = Guid.NewGuid();
        var workspace = Guid.NewGuid();
        var first = _client.OpenSession(id, workspace, Request());
        var firstOutput = new Collector(first);
        first.Start();
        firstOutput.WaitFor(t => t.Contains(">>"), "プロンプト");

        // 先勝ち：後から来た方には渡さず、先の接続はそのまま流れ続ける。
        Assert.Throws<PtySessionInUseException>(() => _client.OpenSession(id, workspace, Request()));
        first.Write("echo still-mine\r");
        firstOutput.WaitFor(t => Count(t, "still-mine") >= 2, "先の接続の echo");
        Assert.Null(firstOutput.ExitCode);

        // 先の Loomo が落ちれば席は空き、次は繋がる。
        first.Abandon();
        var watch = Stopwatch.StartNew();
        RemotePtySession? second = null;
        while (second is null)
        {
            try { second = _client.OpenSession(id, workspace, Request()); }
            catch (PtySessionInUseException) when (watch.Elapsed < Wait) { Thread.Sleep(20); }
        }

        Assert.False(second.Created);
        second.Dispose();
    }

    [Fact]
    public void RestartingATabStartsAFreshShellUnderTheSameId()
    {
        var id = Guid.NewGuid();
        var workspace = Guid.NewGuid();
        var first = _client.OpenSession(id, workspace, Request());
        var firstOutput = new Collector(first);
        first.Start();
        firstOutput.WaitFor(t => t.Contains(">>"), "プロンプト");

        // 再起動ボタン：殺してすぐ同じ ID で開き直す。殺される途中のシェルを掴んではいけない。
        first.Dispose();
        var second = _client.OpenSession(id, workspace, Request());
        Assert.True(second.Created);
        var secondOutput = new Collector(second);
        second.Start();
        secondOutput.WaitFor(t => t.Contains(">>"), "新しいシェルのプロンプト");
        Assert.Null(secondOutput.ExitCode);
        second.Dispose();
    }

    [Fact]
    public async Task KillFromOutsideSparesAShellSomeoneIsAttachedTo()
    {
        var id = Guid.NewGuid();
        var workspace = Guid.NewGuid();
        var session = _client.OpenSession(id, workspace, Request());
        var output = new Collector(session);
        session.Start();
        output.WaitFor(t => t.Contains(">>"), "プロンプト");

        // 別の Loomo がタブを閉じた・ワークスペースを消した：繋いでいるこちらのシェルは殺させない。
        await _client.KillAsync(id);
        await _client.KillWorkspaceAsync(workspace);
        session.Write("echo alive\r");
        output.WaitFor(t => Count(t, "alive") >= 2, "生きているシェルの echo");

        // 誰も繋いでいなければ効く。
        session.Abandon();
        await WaitUntilAsync(async () => (await _client.ListAsync()).Any(s => s.SessionId == id && !s.Attached), "切り離し");
        await _client.KillAsync(id);
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
        // 消すのは誰も繋いでいないシェル（一度も開いていないワークスペース・常駐を切った後の復元）。
        doomed.Abandon();
        kept.Abandon();
        await WaitUntilAsync(async () => (await _client.ListAsync()).Count(s => !s.Attached &&
            (s.WorkspaceId == doomedWorkspace || s.WorkspaceId == keptWorkspace)) == 2, "切り離し");

        await _client.KillWorkspaceAsync(doomedWorkspace);

        await WaitUntilAsync(async () => (await _client.ListAsync()).All(s => s.WorkspaceId != doomedWorkspace), "削除");
        Assert.Contains(await _client.ListAsync(), s => s.WorkspaceId == keptWorkspace);
        await _client.KillWorkspaceAsync(keptWorkspace);
    }

    [Fact]
    public async Task LeavingAsTheLastLoomoKillsEveryShell()
    {
        var attached = _client.OpenSession(Guid.NewGuid(), Guid.NewGuid(), Request());
        var detached = _client.OpenSession(Guid.NewGuid(), Guid.NewGuid(), Request());
        attached.Start();
        detached.Start();
        detached.Abandon();
        Assert.Equal(2, (await _client.ListAsync()).Count);

        // このテストのプロセスが唯一の Loomo。正常終了を伝えたら、繋いでいるシェルも切り離したシェルも残さない。
        _client.Leave();

        await WaitUntilAsync(async () => (await _client.ListAsync()).Count == 0, "全セッションの終了");
        attached.Dispose();
    }

    [Fact]
    public async Task LeavingSparesTheShellsWhileAnotherLoomoIsAlive()
    {
        var session = _client.OpenSession(Guid.NewGuid(), Guid.NewGuid(), Request());
        session.Start();
        session.Abandon();

        // もう1つの Loomo（の代役）が生きていて、ホストに一度は話しかけている。
        using var other = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c ping -n 60 127.0.0.1 >nul")
        {
            CreateNoWindow = true, UseShellExecute = false,
        })!;
        AskAs(other.Id, new sk0ya.Loomo.Core.Pty.PtyProtocol.Request(sk0ya.Loomo.Core.Pty.PtyProtocol.Operation.List));

        _client.Leave();
        await Task.Delay(300);
        Assert.Single(await _client.ListAsync());

        // その Loomo が落ちた（Leave を言わずに消えた）なら、もう数えない：次の正常終了が最後になる。
        other.Kill();
        other.WaitForExit();
        _client.Leave();
        await WaitUntilAsync(async () => (await _client.ListAsync()).Count == 0, "全セッションの終了");
    }

    [Fact]
    public async Task TryListTellsANotRunningHostFromAnUnansweredOne()
    {
        var absent = new PtyHostClient("loomo-pty-test-absent-" + Guid.NewGuid().ToString("N"),
            Path.Combine(_root, "bundled"), Path.Combine(_root, "installed"));
        Assert.Empty((await absent.TryListAsync())!);

        var id = Guid.NewGuid();
        var session = _client.OpenSession(id, Guid.NewGuid(), Request());
        session.Start();
        Assert.Contains((await _client.TryListAsync())!, s => s.SessionId == id);
        session.Dispose();
    }

    private void AskAs(int processId, sk0ya.Loomo.Core.Pty.PtyProtocol.Request request)
    {
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", _pipeName, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        pipe.Connect(5000);
        sk0ya.Loomo.Core.Pty.PtyProtocol.WriteJson(pipe, sk0ya.Loomo.Core.Pty.PtyProtocol.FrameType.Request,
            request with { ClientProcessId = processId });
        Assert.NotNull(sk0ya.Loomo.Core.Pty.PtyProtocol.ReadFrame(pipe));
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

    [Fact]
    public async Task 閉じたタブを預けたシェルは開き直すと同じ画面に戻り_一覧では預かり中と分かる()
    {
        var id = Guid.NewGuid();
        var workspace = Guid.NewGuid();
        var first = _client.OpenSession(id, workspace, Request());
        var firstOutput = new Collector(first);
        first.Start();
        firstOutput.WaitFor(t => t.Contains(">>"), "プロンプト");
        first.Write("echo parked-456\r");
        firstOutput.WaitFor(t => Count(t, "parked-456") >= 2, "echo の結果");

        first.Park(TimeSpan.FromMinutes(3));
        first.Dispose();   // 預けたあとの Dispose は殺さない（ビューは閉じるときに必ず呼ぶ）

        await WaitUntilAsync(async () =>
            (await _client.ListAsync()).SingleOrDefault(s => s.SessionId == id) is { Attached: false, ParkedUntilUtc: not null },
            "預かり中になる");
        Assert.Null(firstOutput.ExitCode);

        var second = _client.OpenSession(id, workspace, Request());
        Assert.False(second.Created);
        var secondOutput = new Collector(second);
        second.Start();
        secondOutput.WaitFor(t => Count(t, "parked-456") >= 2, "開き直した後のスナップショット");
        // 開き直したら預かりは解ける（期限で殺されない）。
        await WaitUntilAsync(async () =>
            (await _client.ListAsync()).SingleOrDefault(s => s.SessionId == id) is { Attached: true, ParkedUntilUtc: null },
            "預かりが解ける");
        second.Dispose();
    }

    [Fact]
    public async Task 預けた期限を過ぎたシェルはホストが殺す()
    {
        var id = Guid.NewGuid();
        var first = _client.OpenSession(id, Guid.NewGuid(), Request());
        var output = new Collector(first);
        first.Start();
        output.WaitFor(t => t.Contains(">>"), "プロンプト");

        first.Park(TimeSpan.FromSeconds(1));

        await WaitUntilAsync(async () => (await _client.ListAsync()).All(s => s.SessionId != id), "期限で消える");
    }

    [Fact]
    public void 閉じたターミナルの控えは新しい順に戻り_期限と別の部屋のものは戻さない()
    {
        var workspace = Guid.NewGuid();
        var other = Guid.NewGuid();
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var tabs = new sk0ya.Loomo.App.Services.ClosedTerminalTabs();
        var old = new sk0ya.Loomo.App.Services.ClosedTerminalTab(Guid.NewGuid(), workspace, null, null, now.AddMinutes(-5));
        var a = new sk0ya.Loomo.App.Services.ClosedTerminalTab(Guid.NewGuid(), workspace, "A", null, now.AddMinutes(-2));
        var b = new sk0ya.Loomo.App.Services.ClosedTerminalTab(Guid.NewGuid(), workspace, "B", null, now.AddMinutes(-1));
        var elsewhere = new sk0ya.Loomo.App.Services.ClosedTerminalTab(Guid.NewGuid(), other, "X", null, now);
        tabs.Push(old); tabs.Push(a); tabs.Push(b); tabs.Push(elsewhere);

        Assert.Equal(2, tabs.Count(workspace, now));
        Assert.Equal(b, tabs.TakeLatest(workspace, now));
        Assert.Equal(a, tabs.TakeLatest(workspace, now));
        Assert.Null(tabs.TakeLatest(workspace, now));
        Assert.Equal(elsewhere, tabs.TakeLatest(other, now));
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
