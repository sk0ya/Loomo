using System.Diagnostics;
using System.IO;
using System.Text;
using sk0ya.Loomo.Core.Pty;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Pty.Host;
using sk0ya.Loomo.Services.Terminal;
using Terminal.Sessions;

namespace sk0ya.Loomo.Tests;

/// <summary>§24.72 閉じたターミナルの画面を書庫に残す——書庫そのものと、常駐ホストが閉じたタブの画面を書くこと。</summary>
public sealed class TerminalArchiveTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 25, 3, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-archive-test-" + Guid.NewGuid().ToString("N"));
    private string Dir => Path.Combine(_root, "archive");

    public TerminalArchiveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Writes_text_and_meta_and_lists_newest_first()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var ws = Guid.NewGuid();
        TerminalArchive.Write(Dir, a, ws, "pwsh", @"C:\work\app", "line1\nline2\n", T0);
        TerminalArchive.Write(Dir, b, null, null, @"C:\work\api", "x", T0.AddMinutes(1));

        var list = TerminalArchive.List(Dir);

        Assert.Equal(new[] { b, a }, list.Select(e => e.SessionId));
        Assert.Equal(2, list[1].LineCount);
        Assert.Equal(ws, list[1].WorkspaceId);
        Assert.Equal("api", TerminalArchive.DisplayTitle(list[0]));   // 題が無ければ場所のフォルダー名
        Assert.Equal("line1" + Environment.NewLine + "line2", File.ReadAllText(list[1].TextPath));
    }

    [Fact]
    public void Blank_screens_are_not_written()
    {
        Assert.Null(TerminalArchive.Write(Dir, Guid.NewGuid(), null, "pwsh", null, "  \n \n", T0));
        Assert.Empty(TerminalArchive.List(Dir));
    }

    [Fact]
    public void Keeps_only_the_newest_entries()
    {
        for (var i = 0; i < TerminalArchive.MaxEntries + 3; i++)
            TerminalArchive.Write(Dir, Guid.NewGuid(), null, $"t{i}", null, $"screen {i}", T0.AddMinutes(i));

        var list = TerminalArchive.List(Dir);

        Assert.Equal(TerminalArchive.MaxEntries, list.Count);
        Assert.Equal($"t{TerminalArchive.MaxEntries + 2}", list[0].Title);
        Assert.Equal(TerminalArchive.MaxEntries * 2, Directory.GetFiles(Dir).Length);   // .txt と .json の組
    }

    [Fact]
    public void Long_screens_keep_the_tail_and_secrets_are_masked()
    {
        var sb = new StringBuilder();
        while (sb.Length <= TerminalArchive.MaxChars) sb.Append("old line\n");
        sb.Append("export GITHUB_TOKEN=ghp_abcdefghijklmnopqrstuvwxyz0123456789\nlast line");

        var entry = TerminalArchive.Write(Dir, Guid.NewGuid(), null, "pwsh", null, sb.ToString(), T0)!;
        var text = File.ReadAllText(entry.TextPath);

        Assert.True(entry.HeadOmitted);
        Assert.Equal(1, entry.MaskedSecrets);
        Assert.EndsWith("last line", text);
        Assert.Contains($"GITHUB_TOKEN={SecretRedactor.Mask}", text);
        Assert.StartsWith("old line", text);   // 行の途中からは始めない
    }

    [Fact]
    public void Document_name_has_the_title_and_the_time()
        => Assert.StartsWith("閉じた画面-pwsh-",
            TerminalArchive.DocumentName(new TerminalArchiveEntry("x.txt", Guid.NewGuid(), null, "pwsh", null, T0, 1, false, 0)));

    [Fact]
    public async Task The_host_archives_the_screen_of_a_closed_tab()
    {
        var pipe = "loomo-archive-test-" + Guid.NewGuid().ToString("N");
        var server = new PtyHostServer(pipe, idleExit: null, archiveDirectory: Dir);
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
        session.Write("echo archived-marker-42\r");
        await WaitAsync(() => { lock (output) return CountOf(output.ToString(), "archived-marker-42") >= 2; }, "echo の結果");

        session.Dispose();   // タブを閉じた：ホストは殺す前に画面を書庫へ書く

        await WaitAsync(() => TerminalArchive.List(Dir).Any(e => e.SessionId == id), "書庫への書き込み");
        var entry = Assert.Single(TerminalArchive.List(Dir), e => e.SessionId == id);
        Assert.Contains("archived-marker-42", File.ReadAllText(entry.TextPath));
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
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
