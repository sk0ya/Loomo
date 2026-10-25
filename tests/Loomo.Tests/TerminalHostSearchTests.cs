using System.Diagnostics;
using System.IO;
using System.Text;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Pty;
using sk0ya.Loomo.Pty.Host;
using sk0ya.Loomo.Services.Terminal;
using Terminal.Sessions;

namespace sk0ya.Loomo.Tests;

/// <summary>§24.67 まだ表示していないタブの画面を、常駐ホストに探してもらう。</summary>
public sealed class TerminalHostSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-host-search-" + Guid.NewGuid().ToString("N"));

    public TerminalHostSearchTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Finds_lines_ignoring_case_once_per_line()
    {
        var id = Guid.NewGuid();
        var matches = TerminalScreenSearch.Find(id, "ok\nBuild FAILED: error CS0103 error\nlast", "error", 10);

        var match = Assert.Single(matches);
        Assert.Equal((id, 1, 14), (match.SessionId, match.LineIndex, match.Column));
        Assert.Empty(TerminalScreenSearch.Find(id, "abc", "", 10));
        Assert.Equal(2, TerminalScreenSearch.Find(id, "x\nx\nx", "x", 2).Count);   // 上限
    }

    [Fact]
    public void Anchor_is_the_trimmed_head_of_the_line()
    {
        Assert.Equal("error CS0103", TerminalSearchHits.Anchor("   error CS0103   "));
        Assert.Null(TerminalSearchHits.Anchor("   "));
        Assert.Equal(40, TerminalSearchHits.Anchor(new string('x', 100))!.Length);
    }

    [Fact]
    public async Task The_host_searches_a_detached_session_screen()
    {
        var pipe = "loomo-host-search-" + Guid.NewGuid().ToString("N");
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
        session.Write("echo needle-in-host-77\r");
        await WaitAsync(() => { lock (output) return output.ToString().Split("needle-in-host-77").Length >= 3; }, "echo の結果");
        session.Abandon();   // Loomo が落ちた／まだ表示していない：画面はホストにだけある

        var matches = await client.SearchAsync("NEEDLE-in-host", [id, Guid.NewGuid()]);

        Assert.True(matches.Count >= 1);
        Assert.All(matches, m => Assert.Equal(id, m.SessionId));
        Assert.Contains(matches, m => m.LineText.Trim() == "needle-in-host-77");
        await client.KillAsync(id);
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
