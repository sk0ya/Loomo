using System.Diagnostics;
using System.IO;
using System.Text;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Core.Pty;
using sk0ya.Loomo.Pty.Host;
using sk0ya.Loomo.Services.Terminal;
using Terminal.Sessions;

namespace sk0ya.Loomo.Tests;

/// <summary>§24.108 ターミナルのタブを切り替えずに覗き、1 行送る。</summary>
public sealed class TerminalPeekTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-peek-" + Guid.NewGuid().ToString("N"));

    public TerminalPeekTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void 末尾は画面の下の空行と行末の空白を落として数える()
    {
        Assert.Equal("b\nc", TerminalScreenTail.Last("a\nb  \r\nc\n\n   \n", 2));
        Assert.Equal("a\nb", TerminalScreenTail.Last("a\nb", 10));
        Assert.Equal("", TerminalScreenTail.Last("", 5));
        Assert.Equal("", TerminalScreenTail.Last("x", 0));
    }

    [Fact]
    public void 動いているもの面の行の鍵からタブを引く()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, ShellWindow.TerminalTabIdOf($"terminal:{id}"));
        Assert.Equal(id, ShellWindow.TerminalTabIdOf($"claude:{id}"));
        Assert.Equal(id, ShellWindow.TerminalTabIdOf($"program:{id}:build"));
        Assert.Null(ShellWindow.TerminalTabIdOf($"debug:{id}"));
        Assert.Null(ShellWindow.TerminalTabIdOf("terminal:not-a-guid"));
    }

    [Fact]
    public async Task 送るときは文字列とEnterを分け_空ならEnterだけ_送ったら入力欄を空にする()
    {
        var sent = new List<string>();
        var vm = new TerminalPeekViewModel(Guid.NewGuid(), "pwsh", () => Task.FromResult<string?>("PS>"), text => { sent.Add(text); return true; });

        vm.Input = "1";
        await vm.SendCommand.ExecuteAsync(null);
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(["1", "\r", "\r"], sent);
        Assert.Equal("", vm.Input);
        Assert.Equal("PS>", vm.ScreenText);
        vm.Dispose();
    }

    [Fact]
    public async Task 画面を取れなければ理由を出して入力を止める()
    {
        var vm = new TerminalPeekViewModel(Guid.NewGuid(), "pwsh", () => Task.FromResult<string?>(null), _ => true);

        await vm.RefreshAsync();

        Assert.True(vm.IsUnavailable);
        Assert.Equal(TerminalPeekViewModel.UnavailableMessage, vm.ScreenText);
        vm.Dispose();
    }

    [Fact]
    public async Task ホストは繋いでいるセッションの画面の末尾を返す()
    {
        var pipe = "loomo-peek-" + Guid.NewGuid().ToString("N");
        var server = new PtyHostServer(pipe, idleExit: null);
        new Thread(server.Run) { IsBackground = true, Name = "PtyHostServerUnderTest" }.Start();
        using var mutex = new Mutex(initiallyOwned: false, PtyProtocol.HostMutexName(pipe));
        var client = new PtyHostClient(pipe, Path.Combine(_root, "bundled"), Path.Combine(_root, "installed"));
        var id = Guid.NewGuid();
        var session = client.OpenSession(id, Guid.NewGuid(), new TerminalSessionRequest(
            "cmd.exe", "cmd.exe /d /k prompt $G$G", _root, 80, 24, new Dictionary<string, string?>(), ScrollbackLimit: 1000));
        var output = new StringBuilder();
        session.OutputReceived += (_, text) => { lock (output) output.Append(text); };
        session.Start();
        await WaitAsync(() => { lock (output) return output.ToString().Contains(">>"); }, "プロンプト");
        session.Write("echo peek-tail-42\r");
        await WaitAsync(() => { lock (output) return output.ToString().Split("peek-tail-42").Length >= 3; }, "echo の結果");

        // 表示中のタブ（繋いだまま）でも、ホストの画面から取れる
        var tail = await client.TailAsync(id, 3);

        Assert.NotNull(tail);
        Assert.Contains("peek-tail-42", tail);
        Assert.True(tail!.Split('\n').Length <= 3);
        Assert.Null(await client.TailAsync(Guid.NewGuid(), 3));   // 無いセッション
        session.Dispose();
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20))
                throw new TimeoutException($"{what} が来ない");
            await Task.Delay(50);
        }
    }
}
