using System.IO;
using System.Text.Json.Nodes;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.Tests;

/// <summary>Claude Code のフック（§24.1.1・§38.4）・セッション一覧・EDITOR の受け口（§38.5）・編集の提案（§38）。
/// フックの入力は Claude Code 2.1.302 から実際に受け取ったものに合わせてある。</summary>
public sealed class ClaudeCodeHooksTests
{
    private const string Script = @"C:\Users\me\AppData\Local\Loomo\claude\loomo-claude-hook.ps1";

    [Fact]
    public void フックは他の設定とフックを残して足し_二度目は何もしない()
    {
        var settings = JsonNode.Parse("""
            {"model":"opus","hooks":{"Notification":[{"matcher":"","hooks":[{"type":"command","command":"say hi"}]}],
             "Stop":[{"hooks":[{"type":"command","command":"x"}]}]}}
            """)!.AsObject();
        Assert.True(ClaudeHookInstaller.AddTo(settings, Script));
        Assert.True(ClaudeHookInstaller.IsInstalled(settings, Script));
        Assert.False(ClaudeHookInstaller.AddTo(settings, Script));
        Assert.Equal("opus", (string?)settings["model"]);
        Assert.Equal(2, settings["hooks"]!["Notification"]!.AsArray().Count);   // 人のフックは残る
        Assert.Single(settings["hooks"]!["SessionStart"]!.AsArray());
        Assert.NotNull(settings["hooks"]!["Stop"]);
    }

    [Fact]
    public void 外すときはLoomoのフックだけ外し空の入れ物を片付ける()
    {
        var settings = JsonNode.Parse("""{"hooks":{"Notification":[{"hooks":[{"type":"command","command":"say hi"}]}]}}""")!.AsObject();
        ClaudeHookInstaller.AddTo(settings, Script);
        Assert.True(ClaudeHookInstaller.RemoveFrom(settings, Script));
        Assert.False(ClaudeHookInstaller.IsInstalled(settings, Script));
        Assert.Single(settings["hooks"]!["Notification"]!.AsArray());
        Assert.Null(settings["hooks"]!["SessionStart"]);

        var empty = new JsonObject();
        ClaudeHookInstaller.AddTo(empty, Script);
        ClaudeHookInstaller.RemoveFrom(empty, Script);
        Assert.Null(empty["hooks"]);
    }

    [Fact]
    public void 設定ファイルへ入れると元の設定を一度だけ残す()
    {
        var dir = Directory.CreateTempSubdirectory("loomo-hooks-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), """{"model":"opus"}""");
            var script = Path.Combine(dir, "hook.ps1");
            Assert.True(ClaudeHookInstaller.Install(dir, script));
            Assert.True(File.Exists(script));
            Assert.Equal("""{"model":"opus"}""", File.ReadAllText(Path.Combine(dir, "settings.json.loomo-backup")));
            Assert.True(ClaudeHookInstaller.IsInstalled(dir, script));
            Assert.False(ClaudeHookInstaller.Install(dir, script));
            Assert.True(ClaudeHookInstaller.Uninstall(dir, script));
            Assert.Equal("opus", (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!["model"]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void フックの1行を読み承認待ちを見分ける()
    {
        var tab = Guid.NewGuid();
        var permission = ClaudeHookBridge.Parse(
            "{\"tab\":\"" + tab + "\",\"event\":{\"session_id\":\"s\",\"hook_event_name\":\"Notification\",\"message\":\"Claude needs your permission\",\"notification_type\":\"permission_prompt\"}}");
        Assert.Equal(tab, permission!.Value.Tab);
        Assert.True(ClaudeHookBridge.IsPermissionRequest(permission.Value.Event));

        Assert.False(ClaudeHookBridge.IsPermissionRequest(JsonNode.Parse(
            """{"hook_event_name":"Notification","message":"Claude is waiting for your input","notification_type":"idle_prompt"}""")!.AsObject()));
        Assert.True(ClaudeHookBridge.IsPermissionRequest(JsonNode.Parse(   // notification_type の無い古い版
            """{"hook_event_name":"Notification","message":"Claude needs your permission to use Bash"}""")!.AsObject()));
        Assert.Null(ClaudeHookBridge.Parse("not json"));
        Assert.Null(ClaudeHookBridge.Parse("""{"tab":"x","event":{}}"""));
    }

    [Fact]
    public void 承認待ちは入力待ちより先に出て_作業が再開したら消える()
    {
        var tab = Guid.NewGuid();
        var sut = new PaneActivityTracker();
        sut.TerminalTitleChanged(tab, "◐ Fix", paneWatched: false);
        sut.TerminalTitleChanged(tab, "✳ Fix", paneWatched: false);   // 承認ダイアログでも印は ✳
        Assert.True(sut.TerminalAgentNeedsApproval(tab, paneWatched: false));
        Assert.Equal(PaneActivityKind.AgentApproval, sut.AggregateTerminal(out _));
        Assert.True(sut.TerminalTitleChanged(tab, "◐ Fix", paneWatched: false).ShouldRefresh);
        Assert.Equal(PaneActivityKind.AgentWorking, sut.AggregateTerminal(out _));

        Assert.False(sut.TerminalAgentNeedsApproval(tab, paneWatched: true));   // 見ているなら残さない
    }

    [Fact]
    public void セッションはSessionStartでタブに結ばれる()
    {
        var tab = Guid.NewGuid();
        var sut = new PaneActivityTracker();
        Assert.Null(sut.AgentSessionOf(tab));
        sut.TerminalAgentSessionStarted(tab, "abc", @"C:\t\abc.jsonl");
        Assert.Equal(("abc", @"C:\t\abc.jsonl"), sut.AgentSessionOf(tab)!.Value);
    }

    [Fact]
    public void セッションの要約は題名と最後の頼みごととコンテキスト量を読む()
    {
        string[] lines =
        [
            """{"type":"user","message":{"role":"user","content":"hi"}}""",
            """{"type":"assistant","message":{"model":"claude-opus-5-5","usage":{"input_tokens":10,"cache_read_input_tokens":1000,"cache_creation_input_tokens":200,"output_tokens":5}}}""",
            """{"type":"ai-title","aiTitle":"最初の題","sessionId":"s"}""",
            """{"type":"assistant","message":{"model":"claude-opus-5-5","usage":{"input_tokens":5,"cache_read_input_tokens":120000,"cache_creation_input_tokens":3000,"output_tokens":9}}}""",
            """{"type":"ai-title","aiTitle":"ペグボードのUI改善","sessionId":"s"}""",
            """{"type":"last-prompt","lastPrompt":"続けて"}""",
            "not json \"ai-title\"",
        ];
        var summary = ClaudeSessionCatalog.Summarize("s", @"C:\s.jsonl", DateTime.UtcNow, lines);
        Assert.Equal("ペグボードのUI改善", summary.Title);
        Assert.Equal("続けて", summary.LastPrompt);
        Assert.Equal(123005, summary.ContextTokens);
        Assert.Equal("claude-opus-5-5", summary.Model);
        Assert.Equal("123k", ClaudeSessionCatalog.FormatTokens(123005));
        Assert.Equal("1.2M", ClaudeSessionCatalog.FormatTokens(1_200_000));
        Assert.Equal("950", ClaudeSessionCatalog.FormatTokens(950));
    }

    [Fact]
    public void EDITORの受け口はJSONのパスを読む()
    {
        Assert.Equal(@"C:\tmp\claude-prompt-1.md", LoomoEditBridge.ParsePath("""{"path":"C:\\tmp\\claude-prompt-1.md"}"""));
        Assert.Null(LoomoEditBridge.ParsePath(""));
        Assert.Null(LoomoEditBridge.ParsePath("{}"));
        Assert.Null(LoomoEditBridge.ParsePath("garbage"));
    }

    [Fact]
    public async Task EDITORの受け口はタブを閉じるまで返らない()
    {
        var pipe = $"loomo-edit-test-{Guid.NewGuid():N}";
        var closed = new TaskCompletionSource();
        string? opened = null;
        using var bridge = new LoomoEditBridge(path => { opened = path; return closed.Task; }, pipe);
        bridge.Start();
        await using var client = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        var writer = new StreamWriter(client) { AutoFlush = true };
        await writer.WriteLineAsync("""{"path":"C:\\tmp\\a.md"}""");
        var reader = new StreamReader(client);
        var reply = reader.ReadLineAsync();
        await Task.Delay(200);
        Assert.False(reply.IsCompleted);
        Assert.Equal(@"C:\tmp\a.md", opened);
        closed.SetResult();
        Assert.Equal("done", await reply.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task 提案は列に積み先頭から決め_Claude側で閉じたら却下で消える()
    {
        var sut = new ClaudeProposalViewModel();
        var shown = new List<string>();
        sut.ShowDiffRequested += (path, _) => shown.Add(path);
        var first = sut.Enqueue("t1", @"C:\a.cs", "A", CancellationToken.None);
        var second = sut.Enqueue("t2", @"C:\b.cs", "B", CancellationToken.None);
        Assert.True(sut.IsVisible);
        Assert.Contains("ほか 1 件", sut.Message);
        Assert.Equal([@"C:\a.cs"], shown);

        sut.AcceptCommand.Execute(null);
        var decision = await first;
        Assert.True(decision.Accepted);
        Assert.Equal("A", decision.FinalContents);
        Assert.Equal([@"C:\a.cs", @"C:\b.cs"], shown);   // 次の提案の差分を出す

        sut.RejectCommand.Execute(null);
        Assert.False((await second).Accepted);
        Assert.False(sut.IsVisible);
    }
}
