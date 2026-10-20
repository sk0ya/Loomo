using System.IO;
using System.Linq;
using System.Text.Json;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Tasks;
using sk0ya.Loomo.Services;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Claude の会話のツリー（§39.9）。ターン軸で、返事の節・書き換えたファイル・サブエージェント（その下にその操作）・
/// コマンド・失敗・メモをトグルで出し分ける・文字で絞り込む・節の id が書き足しで変わらない・選んだ節だけを描く
/// （記録から全文を引き直す）・最新のターンへの追従を見る。画面は対象外。
/// </summary>
public sealed class ClaudeSessionTreeTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), $"loomo-cctree-{Guid.NewGuid():N}");

    public ClaudeSessionTreeTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (IOException) { }
    }

    // ===== 記録の行 =====

    private static string User(string text, string at)
        => JsonSerializer.Serialize(new { type = "user", timestamp = at, cwd = @"C:\ws", message = new { role = "user", content = text } });

    private static string Assistant(string id, object[] content, string at)
        => JsonSerializer.Serialize(new
        {
            type = "assistant", timestamp = at,
            message = new
            {
                id, model = "claude-opus-5-5", role = "assistant", content,
                usage = new { input_tokens = 10, output_tokens = 5, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 },
            },
        });

    private static object ToolUse(string id, string name, object input) => new { type = "tool_use", id, name, input };

    private static string Result(string toolUseId, string content, string at, bool isError = false, object? toolUseResult = null)
        => toolUseResult is null
            ? JsonSerializer.Serialize(new
            {
                type = "user", timestamp = at,
                message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = toolUseId, content, is_error = isError } } },
            })
            : JsonSerializer.Serialize(new
            {
                type = "user", timestamp = at, toolUseResult,
                message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = toolUseId, content, is_error = isError } } },
            });

    private static string Reply(string id, string text, string at) => Assistant(id, [new { type = "text", text }], at);

    /// <summary>2ターン。2つ目でサブエージェントを2つ（同期＝結果に agentId、動いている途中＝頼みごとで結ぶ）。</summary>
    private static string[] Session() =>
    [
        User("一つ目の頼みごと", "2026-10-19T01:00:00Z"),
        Assistant("m1", [ToolUse("t1", "Bash", new { command = "dotnet build" })], "2026-10-19T01:00:01Z"),
        Result("t1", "Exit code 1\nerror CS1002: ; expected", "2026-10-19T01:00:02Z", isError: true),
        Assistant("m2", [ToolUse("t2", "Write", new { file_path = @"C:\ws\new.cs", content = "class New {}" })], "2026-10-19T01:00:03Z"),
        Result("t2", "ok", "2026-10-19T01:00:04Z"),
        Reply("r0", "途中のメモです。", "2026-10-19T01:00:04Z"),
        Reply("r1", "前置き。\n\n## 原因\n\nセミコロン。\n\n```md\n## コードの中の見出し\n```\n\n## 直し方\n\n足しました。", "2026-10-19T01:00:05Z"),
        User("二つ目の頼みごと", "2026-10-19T01:01:00Z"),
        Assistant("m3", [ToolUse("t3", "Edit", new { file_path = @"C:\ws\b.cs", old_string = "a", new_string = "b" })], "2026-10-19T01:01:01Z"),
        Result("t3", "ok", "2026-10-19T01:01:02Z"),
        Assistant("m4", [ToolUse("t4", "Agent", new { description = "調べる", subagent_type = "Explore", prompt = "呼び出し元を調べて" })], "2026-10-19T01:01:03Z"),
        Result("t4", "呼び出し元は3つ。", "2026-10-19T01:01:20Z", toolUseResult: new { status = "completed", agentId = "aaa" }),
        Assistant("m5", [ToolUse("t5", "Agent", new { description = "直す", subagent_type = "general-purpose", prompt = "b.cs を直して" })], "2026-10-19T01:01:21Z"),
        Reply("r2", "## まとめ\n\n直しています。", "2026-10-19T01:01:22Z"),
    ];

    private string WriteSession(string[] lines)
    {
        var path = Path.Combine(_base, "s1.jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        var sub = Directory.CreateDirectory(Path.Combine(_base, "s1", "subagents")).FullName;
        File.WriteAllText(Path.Combine(sub, "agent-aaa.jsonl"), string.Join("\n",
            User("呼び出し元を調べて", "2026-10-19T01:01:04Z"),
            Assistant("sa1", [ToolUse("sa-t1", "Grep", new { pattern = "Foo" })], "2026-10-19T01:01:05Z"),
            Result("sa-t1", "3 matches", "2026-10-19T01:01:06Z"),
            Reply("sa-r1", "呼び出し元は3つ。", "2026-10-19T01:01:19Z")) + "\n");
        File.WriteAllText(Path.Combine(sub, "agent-aaa.meta.json"), """{"agentType":"Explore","description":"調べる"}""");
        File.WriteAllText(Path.Combine(sub, "agent-bbb.jsonl"), string.Join("\n",
            User("b.cs を直して", "2026-10-19T01:01:22Z"),
            Assistant("sb1", [ToolUse("sb-t1", "Edit", new { file_path = @"C:\ws\sub.cs", old_string = "x", new_string = "y" })], "2026-10-19T01:01:23Z"),
            Result("sb-t1", "ok", "2026-10-19T01:01:24Z"),
            Assistant("sb2", [ToolUse("sb-t2", "Bash", new { command = "dotnet test" })], "2026-10-19T01:01:25Z"),
            Result("sb-t2", "Exit code 1\nfailed", "2026-10-19T01:01:26Z", isError: true)) + "\n");
        return path;
    }

    private ClaudeSessionDigest Read(string[]? lines = null)
        => new ClaudeSessionFollower(WriteSession(lines ?? Session())).Refresh().Digest!;

    private static IReadOnlyList<ClaudeTreeNode> Tree(ClaudeSessionDigest digest, ClaudeTreeFilter? filter = null, bool working = false)
        => ClaudeSessionTree.Build(digest, filter ?? new ClaudeTreeFilter(), _ => true, p => p, working);

    private static ClaudeTreeNode TurnOf(IReadOnlyList<ClaudeTreeNode> roots, string prompt)
        => roots.Single(n => n.Kind == ClaudeTreeKind.Turn && n.Label == prompt);

    // ===== サブエージェントを結ぶ =====

    [Fact]
    public void Subagents_AreLinkedToTheirLaunch_ByAgentIdOrByPrompt()
    {
        var turn = Read().CurrentTurn!;
        Assert.Equal(2, turn.Agents.Count);
        var explore = turn.Agents.Single(a => a.AgentId == "aaa");
        Assert.Equal("調べる", explore.Name);                     // meta.json の description
        Assert.Equal("t4", explore.Launch?.ToolUseId);           // 結果の agentId で
        Assert.Equal("呼び出し元は3つ。", explore.Report);
        var fixer = turn.Agents.Single(a => a.AgentId == "bbb");
        Assert.Equal("t5", fixer.Launch?.ToolUseId);             // まだ結果が無い：頼みごとが同じもの
        Assert.Equal("直す", fixer.Name);                        // meta が無ければ呼び出しの説明
        Assert.Contains(fixer.Calls, c => c.Command == "dotnet test");
    }

    [Fact]
    public void Tree_PutsSubagentOperationsUnderTheAgent()
    {
        var roots = Tree(Read(), new ClaudeTreeFilter(Commands: true));
        var turn = TurnOf(roots, "二つ目の頼みごと");
        // ターンの直下は本体の操作だけ（サブエージェントの書き換えはエージェントの下）
        Assert.Contains(turn.Children, n => n.Kind == ClaudeTreeKind.File && n.Label == "b.cs");
        Assert.DoesNotContain(turn.Children, n => n.Kind == ClaudeTreeKind.File && n.Label == "sub.cs");
        var fixer = turn.Children.Single(n => n.Kind == ClaudeTreeKind.Agent && n.Label == "直す");
        Assert.Contains(fixer.Children, n => n.Kind == ClaudeTreeKind.File && n.Label == "sub.cs");
        var test = fixer.Children.Single(n => n.Kind == ClaudeTreeKind.Command);
        Assert.True(test.Failed);
        Assert.True(fixer.Failed);

        // サブエージェントを出さなければ、その書き換えはターンの直下に並ぶ
        var flat = TurnOf(Tree(Read(), new ClaudeTreeFilter(Agents: false)), "二つ目の頼みごと");
        Assert.Contains(flat.Children, n => n.Kind == ClaudeTreeKind.File && n.Label == "sub.cs");
        Assert.DoesNotContain(flat.Children, n => n.Kind == ClaudeTreeKind.Agent);
    }

    // ===== 種類と絞り込み =====

    [Fact]
    public void Tree_TurnsInConversationOrder_WithReplySectionsAndChanges_ByDefault()
    {
        var roots = Tree(Read());
        Assert.Equal(ClaudeTreeKind.Conversation, roots[0].Kind);
        Assert.Equal(["一つ目の頼みごと", "二つ目の頼みごと"], roots.Skip(1).Select(n => n.Label));   // 新しいものが下へつながる
        var first = TurnOf(roots, "一つ目の頼みごと");
        // 返事は見出しで節に（前置きも1節・コードブロックの中の見出しは切らない）
        Assert.Equal(["前置き。", "原因", "直し方"], first.Children.Where(n => n.Kind == ClaudeTreeKind.Section).Select(n => n.Label));
        Assert.Contains("コードの中の見出し", first.Children.Single(n => n.Label == "原因").Text);
        Assert.Contains(first.Children, n => n.Kind == ClaudeTreeKind.File && n.Label == "new.cs" && n.Hint == "作成");
        // 既定ではコマンド・失敗・メモは出さない
        Assert.DoesNotContain(first.Children, n => n.Kind is ClaudeTreeKind.Command or ClaudeTreeKind.Failure or ClaudeTreeKind.Note);
    }

    [Fact]
    public void Tree_Toggles_ShowOnlyChosenKinds_AndHideTurnsWithNothing()
    {
        var roots = Tree(Read(), new ClaudeTreeFilter(Replies: false, Changes: false, Agents: false, Failures: true, Notes: true));
        var first = TurnOf(roots, "一つ目の頼みごと");
        Assert.Equal([ClaudeTreeKind.Failure, ClaudeTreeKind.Note], first.Children.Select(n => n.Kind));
        // サブエージェントを出さなければ、その中の失敗もターンの直下に
        Assert.Equal("Bash: dotnet test", Assert.Single(TurnOf(roots, "二つ目の頼みごと").Children).Label);

        // コマンドを出しているなら、失敗したコマンドはそちらに ✗ 付きで（失敗に二重に出さない）
        var both = TurnOf(Tree(Read(), new ClaudeTreeFilter(Commands: true, Failures: true)), "一つ目の頼みごと");
        Assert.Single(both.Children, n => n.Kind == ClaudeTreeKind.Command && n.Failed);
        Assert.DoesNotContain(both.Children, n => n.Kind == ClaudeTreeKind.Failure);

        // 古いターンで選んだ種類が1つも無ければ、そのターンは出さない
        var onlyAgents = Tree(Read(), new ClaudeTreeFilter(Replies: false, Changes: false));
        Assert.DoesNotContain(onlyAgents, n => n.Label == "一つ目の頼みごと");
    }

    [Fact]
    public void Tree_Search_KeepsMatchesAndTheirParents()
    {
        // 返事の本文に当たる：その節だけを残す
        var roots = Tree(Read(), new ClaudeTreeFilter(Text: "セミコロン"));
        Assert.DoesNotContain(roots, n => n.Kind == ClaudeTreeKind.Conversation);
        var turn = Assert.Single(roots);
        Assert.Equal("原因", Assert.Single(turn.Children).Label);

        // サブエージェントの中の操作に当たる：ターン → エージェント → 操作
        var deep = Assert.Single(Tree(Read(), new ClaudeTreeFilter(Text: "sub.cs")));
        var agent = Assert.Single(deep.Children);
        Assert.Equal(ClaudeTreeKind.Agent, agent.Kind);
        Assert.Equal("sub.cs", Assert.Single(agent.Children).Label);

        // 頼みごとに当たれば、ターンの中身はそのまま
        var whole = Assert.Single(Tree(Read(), new ClaudeTreeFilter(Text: "一つ目")));
        Assert.True(whole.Children.Count >= 3);
    }

    [Fact]
    public void Tree_NodeIds_SurviveAppends()
    {
        var lines = Session();
        var before = Tree(Read(lines[..10]));
        var after = Tree(Read(lines));
        string FileId(IReadOnlyList<ClaudeTreeNode> roots) => TurnOf(roots, "二つ目の頼みごと").Children.Single(n => n.Label == "b.cs").Id;
        Assert.Equal(FileId(before), FileId(after));
        Assert.Equal(TurnOf(before, "一つ目の頼みごと").Id, TurnOf(after, "一つ目の頼みごと").Id);
    }

    // ===== 選んだ節を描く =====

    private static ClaudeConversationDocument.NodeContext Context()
        => new(_ => true, p => p, false, @"C:\ws", ClaudeTranscriptLookup.ReadResult, ClaudeTranscriptLookup.ReadInput,
            output => TerminalOutputPresenter.Present(output, new ProblemMatchContext(@"C:\ws", @"C:\ws", _ => true)));

    [Fact]
    public void NodeBody_Command_ReadsTheOutputFromTheRecord_AndPresentsIt()
    {
        var digest = Read();
        var command = TurnOf(Tree(digest, new ClaudeTreeFilter(Commands: true)), "一つ目の頼みごと")
            .Children.Single(n => n.Kind == ClaudeTreeKind.Command);
        var body = ClaudeConversationDocument.BuildNodeBody(command, digest, Context());
        Assert.Contains("dotnet build", body);
        Assert.Contains("CS1002", body);                 // 出力は記録から引き直す
        Assert.DoesNotContain("Exit code 1", body);      // 頭の終了コードは外す
        Assert.Contains("一つ目の頼みごと", body);         // どの頼みごとの中のものか
    }

    [Fact]
    public void NodeBody_File_ShowsDiffOrWrittenContent()
    {
        var digest = Read();
        var roots = Tree(digest);
        var written = ClaudeConversationDocument.BuildNodeBody(
            TurnOf(roots, "一つ目の頼みごと").Children.Single(n => n.Label == "new.cs"), digest, Context());
        Assert.Contains("New", written);                 // Write の中身は記録から
        var edited = ClaudeConversationDocument.BuildNodeBody(
            TurnOf(roots, "二つ目の頼みごと").Children.Single(n => n.Label == "b.cs"), digest, Context());
        Assert.Contains("language-diff", edited, StringComparison.Ordinal);
    }

    [Fact]
    public void NodeBody_Agent_ShowsPromptAndReport()
    {
        var digest = Read();
        var agent = TurnOf(Tree(digest), "二つ目の頼みごと").Children.Single(n => n.Label == "調べる");
        var body = ClaudeConversationDocument.BuildNodeBody(agent, digest, Context());
        Assert.Contains("呼び出し元を調べて", body);
        Assert.Contains("呼び出し元は3つ。", body);
    }

    [Theory]
    [InlineData("Exit code 2\nboom", true, 2, "boom")]
    [InlineData("all good", false, 0, "all good")]
    [InlineData("no prefix", true, 1, "no prefix")]
    public void SplitExitCode(string text, bool isError, int expectedCode, string expectedOutput)
        => Assert.Equal((expectedCode, expectedOutput), ClaudeConversationDocument.SplitExitCode(text, isError));

    [Fact]
    public void Fence_SwitchesToTildesAroundBackticks()
        => Assert.StartsWith("~~~md\n", ClaudeConversationDocument.Fence("```\nx\n```", "md"));

    // ===== ViewModel：選択と追従 =====

    private static TerminalSupportSubject Subject(Guid tabId)
        => new(tabId, "pwsh", @"C:\ws", "claude", DateTime.UtcNow, new ClaudeAgentTab(tabId, ClaudeAgentState.TurnEnded, null, null, null, 0));

    private static IEnumerable<ClaudeTreeItem> All(IEnumerable<ClaudeTreeItem> items)
        => items.SelectMany(i => All(i.Children).Prepend(i));

    [Fact]
    public async Task ViewModel_SelectsTheLatestTurn_AndFollowsOnlyWhileReadingIt()
    {
        var lines = Session();
        var third = lines.Append(User("三つ目の頼みごと", "2026-10-19T01:02:00Z")).ToArray();
        var vm = new TerminalSupportViewModel();
        var tab = Guid.NewGuid();
        vm.Update(Subject(tab), [], _ => false, DateTime.UtcNow);
        await vm.ApplyDigest(Read(lines));
        var latest = vm.Tree.Single(i => i.Label == "二つ目の頼みごと");
        Assert.True(latest.IsSelected);
        Assert.True(latest.IsExpanded);                  // 最新のターンだけ開いて生やす
        Assert.Contains("二つ目の頼みごと", vm.ConversationBody);

        // 最新のターンの中の節を読んでいる：新しいターンが始まったらその頭へ
        await vm.SelectTreeItemAsync(latest.Children.Single(c => c.Label == "b.cs"));
        await vm.ApplyDigest(Read(third));
        Assert.True(vm.Tree.Single(i => i.Label == "三つ目の頼みごと").IsSelected);
        Assert.Same(latest, vm.Tree.Single(i => i.Label == "二つ目の頼みごと"));   // 行は使い回す

        // 古いターンを読み返している：動かさない
        await vm.SelectTreeItemAsync(vm.Tree.Single(i => i.Label == "一つ目の頼みごと"));
        await vm.ApplyDigest(Read(third.Append(User("四つ目の頼みごと", "2026-10-19T01:03:00Z")).ToArray()));
        Assert.True(vm.Tree.Single(i => i.Label == "一つ目の頼みごと").IsSelected);
        Assert.Contains("一つ目の頼みごと", vm.ConversationBody);
    }

    [Fact]
    public async Task ViewModel_Toggles_RebuildTheTree()
    {
        var vm = new TerminalSupportViewModel();
        vm.Update(Subject(Guid.NewGuid()), [], _ => false, DateTime.UtcNow);
        await vm.ApplyDigest(Read());
        Assert.DoesNotContain(All(vm.Tree), i => i.Icon == "$");
        vm.ShowCommands = true;
        Assert.Contains(All(vm.Tree), i => i.Icon == "$" && i.Label == "dotnet build" && i.Failed);
    }
}
