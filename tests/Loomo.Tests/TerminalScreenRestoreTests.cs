using System.Diagnostics;
using System.IO;
using System.Text;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Pty;
using sk0ya.Loomo.Pty.Host;
using sk0ya.Loomo.Services.Terminal;
using Terminal.Buffer;
using Terminal.Sessions;

namespace sk0ya.Loomo.Tests;

/// <summary>§34.13 Loomo が全部終わったときにタブの画面を残し、次の起動で同じタブへ戻す。</summary>
public sealed class TerminalScreenRestoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 8, 3, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-restore-test-" + Guid.NewGuid().ToString("N"));
    private string Dir => Path.Combine(_root, "restore");

    public TerminalScreenRestoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Take_returns_the_screen_once_and_then_forgets_it()
    {
        var id = Guid.NewGuid();
        var workspace = Guid.NewGuid();
        Assert.True(TerminalScreenRestore.Write(Dir, id, workspace, "\u001b]7770;begin\u0007hello\u001b]7770;end\u0007", T0));
        Assert.Contains(id, TerminalScreenRestore.SavedIds(Dir));

        var saved = TerminalScreenRestore.Take(Dir, id);
        Assert.NotNull(saved);
        Assert.Equal(workspace, saved!.WorkspaceId);
        Assert.Equal(T0, saved.ClosedUtc);
        Assert.Contains("hello", saved.Screen);

        // 2 回目の起動で同じ画面が二重にならない。
        Assert.Null(TerminalScreenRestore.Take(Dir, id));
        Assert.DoesNotContain(id, TerminalScreenRestore.SavedIds(Dir));
    }

    [Fact]
    public void Blank_and_oversized_screens_are_not_kept()
    {
        Assert.False(TerminalScreenRestore.Write(Dir, Guid.NewGuid(), null, "   ", T0));
        Assert.False(TerminalScreenRestore.Write(Dir, Guid.NewGuid(), null, new string('x', TerminalScreenRestore.MaxChars + 1), T0));
        Assert.Empty(TerminalScreenRestore.SavedIds(Dir));
    }

    [Fact]
    public void Old_screens_are_pruned_and_discard_removes_one()
    {
        var old = Guid.NewGuid();
        var kept = Guid.NewGuid();
        TerminalScreenRestore.Write(Dir, old, null, "old", T0 - TimeSpan.FromDays(40));
        TerminalScreenRestore.Write(Dir, kept, null, "new", T0);
        Assert.Equal([kept], TerminalScreenRestore.SavedIds(Dir));

        TerminalScreenRestore.Discard(Dir, kept);
        Assert.Empty(TerminalScreenRestore.SavedIds(Dir));
    }

    [Fact]
    public void Secrets_are_masked_before_writing()
    {
        var id = Guid.NewGuid();
        TerminalScreenRestore.Write(Dir, id, null, "token ghp_abcdefghijklmnopqrstuvwxyz0123456789AB done", T0);
        var screen = TerminalScreenRestore.Take(Dir, id)!.Screen;
        Assert.DoesNotContain("ghp_abcdefghijklmnopqrstuvwxyz0123456789AB", screen);
        Assert.Contains("done", screen);
    }

    [Fact]
    public void Cleanup_drops_the_modes_a_program_left_on_but_keeps_the_primary_screen()
    {
        // 前回 Claude Code や vim が立てていたもの：kitty キーボード・マウス・ブラケットペースト・代替画面・プログラムの申告。
        var old = new HeadlessTerminal(80, 24);
        old.Process("PS C:\\work> echo kept-line\r\nkept-line\r\nPS C:\\work> ");
        old.Process("\u001b[>1u\u001b[?1000h\u001b[?1006h\u001b[?2004h\u001b[?1049h\u001b[2Jfull-screen-tui");
        old.Process("\u001b]7501;state=working:app=claude-code\u001b\\");
        old.Process(TerminalScreenRestore.CleanupBeforeSnapshot);
        var snapshot = old.CreateVtSnapshot();

        Assert.Contains("kept-line", snapshot);
        Assert.DoesNotContain("full-screen-tui", snapshot);   // 代替画面の絵は捨てる
        Assert.DoesNotContain("\u001b[>1u", snapshot);
        Assert.DoesNotContain("\u001b[?1000h", snapshot);
        Assert.DoesNotContain("\u001b[?2004h", snapshot);
        Assert.DoesNotContain("\u001b[?1049h", snapshot);
        Assert.DoesNotContain("7501;", snapshot);

        // 新しいタブの画面モデルへ流すと、前回の文字が見え、区切りがその下に来る。
        var fresh = new HeadlessTerminal(80, 24);
        fresh.Process(snapshot);
        fresh.Process(TerminalScreenRestore.Divider(T0));
        var text = fresh.CreatePlainTextSnapshot();
        Assert.Contains("kept-line", text);
        Assert.True(text.IndexOf("ここまで前回の画面", StringComparison.Ordinal) > text.IndexOf("kept-line", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_last_Loomo_leaving_keeps_the_screen_and_the_next_shell_of_the_same_tab_shows_it()
    {
        var pipe = "loomo-restore-test-" + Guid.NewGuid().ToString("N");
        var server = new PtyHostServer(pipe, idleExit: null, restoreDirectory: Dir);
        new Thread(server.Run) { IsBackground = true, Name = "PtyHostServerUnderTest" }.Start();
        using var mutex = new Mutex(initiallyOwned: false, PtyProtocol.HostMutexName(pipe));
        var client = new PtyHostClient(pipe, Path.Combine(_root, "bundled"), Path.Combine(_root, "installed"));
        var id = Guid.NewGuid();
        var workspace = Guid.NewGuid();

        var first = client.OpenSession(id, workspace, Request());
        var output = new StringBuilder();
        first.OutputReceived += (_, text) => { lock (output) output.Append(text); };
        first.Start();
        await WaitAsync(() => { lock (output) return output.ToString().Contains(">>"); }, "プロンプト");
        first.Write("echo restored-marker-77\r");
        await WaitAsync(() => { lock (output) return CountOf(output.ToString(), "restored-marker-77") >= 2; }, "echo の結果");

        // 最後の Loomo の正常終了：ホストは全部殺す前に画面を残す。
        client.Leave();
        await WaitAsync(() => TerminalScreenRestore.SavedIds(Dir).Contains(id), "画面の書き出し");
        first.Dispose();

        // 次の起動：同じタブ ID でシェルを起こすと、前回の画面と区切りが最初に届く。
        var second = client.OpenSession(id, workspace, Request());
        var replay = new StringBuilder();
        second.OutputReceived += (_, text) => { lock (replay) replay.Append(text); };
        second.Start();
        await WaitAsync(() => { lock (replay) return replay.ToString().Contains("ここまで前回の画面"); }, "前回の画面の再生");
        lock (replay)
        {
            var text = replay.ToString();
            var marker = text.IndexOf("restored-marker-77", StringComparison.Ordinal);
            var divider = text.IndexOf("ここまで前回の画面", StringComparison.Ordinal);
            Assert.True(marker >= 0 && marker < divider,
                $"marker={marker} divider={divider}\n" + text.Replace("\u001b", "<ESC>"));
        }
        Assert.DoesNotContain(id, TerminalScreenRestore.SavedIds(Dir));   // 1 回だけ
        second.Dispose();
    }

    [Fact]
    public async Task A_tab_closed_by_hand_is_not_kept_for_the_next_start()
    {
        var pipe = "loomo-restore-test-" + Guid.NewGuid().ToString("N");
        var server = new PtyHostServer(pipe, idleExit: null, restoreDirectory: Dir);
        new Thread(server.Run) { IsBackground = true, Name = "PtyHostServerUnderTest" }.Start();
        using var mutex = new Mutex(initiallyOwned: false, PtyProtocol.HostMutexName(pipe));
        var client = new PtyHostClient(pipe, Path.Combine(_root, "bundled"), Path.Combine(_root, "installed"));
        var id = Guid.NewGuid();
        var session = client.OpenSession(id, Guid.NewGuid(), Request());
        var output = new StringBuilder();
        session.OutputReceived += (_, text) => { lock (output) output.Append(text); };
        session.Start();
        await WaitAsync(() => { lock (output) return output.ToString().Contains(">>"); }, "プロンプト");

        session.Dispose();   // タブを閉じた（Kill）
        await WaitAsync(async () => (await client.ListAsync()).All(s => s.SessionId != id), "セッションの終了");
        Assert.DoesNotContain(id, TerminalScreenRestore.SavedIds(Dir));
    }

    [Fact]
    public void Tabs_whose_screen_was_kept_are_restored_even_without_a_shell()
    {
        var alive = new TerminalTabSnapshot { Id = Guid.NewGuid() };
        var kept = new TerminalTabSnapshot { Id = Guid.NewGuid() };
        var gone = new TerminalTabSnapshot { Id = Guid.NewGuid() };
        var restorable = WorkspaceSessionCoordinator.ResolveRestorableTerminalTabs(
            [alive, kept, gone], hostSessions: [alive.Id], savedScreens: [kept.Id]);
        Assert.Equal([alive.Id, kept.Id], restorable.Select(t => t.Id));
    }

    [Fact]
    public void Claude_is_resumed_only_in_tabs_whose_shell_was_lost()
    {
        var lost = new TerminalTabSnapshot { Id = Guid.NewGuid(), ResumeClaudeSessionId = "0f8e4a1c-2b3d-4e5f-8a9b-1c2d3e4f5a6b" };
        var alive = new TerminalTabSnapshot { Id = Guid.NewGuid(), ResumeClaudeSessionId = lost.ResumeClaudeSessionId };
        var noClaude = new TerminalTabSnapshot { Id = Guid.NewGuid() };
        var hostile = new TerminalTabSnapshot { Id = Guid.NewGuid(), ResumeClaudeSessionId = "x; Remove-Item -Recurse C:\\" };

        Assert.Equal("claude --resume 0f8e4a1c-2b3d-4e5f-8a9b-1c2d3e4f5a6b",
            WorkspaceSessionCoordinator.ClaudeResumeCommand(lost, hostSessions: [alive.Id]));
        Assert.Null(WorkspaceSessionCoordinator.ClaudeResumeCommand(alive, hostSessions: [alive.Id]));   // Claude ごと生きている
        Assert.Null(WorkspaceSessionCoordinator.ClaudeResumeCommand(lost, hostSessions: null));         // 分からないので打たない
        Assert.Null(WorkspaceSessionCoordinator.ClaudeResumeCommand(noClaude, hostSessions: []));
        Assert.Null(WorkspaceSessionCoordinator.ClaudeResumeCommand(hostile, hostSessions: []));         // 形の違う ID は打たない
    }

    private TerminalSessionRequest Request()
        => new("cmd.exe", "cmd.exe /d /k prompt $G$G", _root, 80, 24, new Dictionary<string, string?>(), ScrollbackLimit: 1000);

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
        => await WaitAsync(() => Task.FromResult(condition()), what);

    private static async Task WaitAsync(Func<Task<bool>> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20))
                throw new TimeoutException($"{what} が来ない");
            await Task.Delay(50);
        }
    }
}
