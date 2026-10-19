using System.IO;
using System.Linq;
using System.Text.Json;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ターミナルサポートペイン（§39）。前面のコマンドから見せ方を決める（Claude を先に見る）・知っているプログラムの引き方
/// （呼び出し演算子・REPL は引数なしだけ）・Claude の記録の畳み方（書き換えたファイル・新しい順）・セッションの当て推量・
/// 一覧を作り直さない（作業中の読み直しで選択とスクロールを飛ばさない）を見る。画面は対象外。
/// </summary>
public sealed class TerminalSupportTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), $"loomo-tsupport-{Guid.NewGuid():N}");

    public TerminalSupportTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (IOException) { }
    }

    // ===== 前面のプログラムの引き方 =====

    [Theory]
    [InlineData("claude", "claude")]
    [InlineData("claude --resume 0e3f", "claude")]
    [InlineData(@"& 'C:\Users\me\.local\bin\claude.exe' -c", "claude")]
    [InlineData("nvim src/a.cs", "vim")]
    [InlineData("vi", "vim")]
    [InlineData("lazygit", "lazygit")]
    [InlineData("python", "python")]
    [InlineData("dotnet watch run", "dotnet-watch")]
    public void Match_KnownPrograms(string commandLine, string expectedId)
        => Assert.Equal(expectedId, TerminalProgramCatalog.Match(commandLine)?.Id);

    [Theory]
    [InlineData("python build.py")]     // スクリプトの実行に REPL の案内は的外れ
    [InlineData("node server.js")]
    [InlineData("dotnet test")]
    [InlineData("git status")]
    [InlineData("")]
    [InlineData(null)]
    public void Match_Unknown_ReturnsNull(string? commandLine)
        => Assert.Null(TerminalProgramCatalog.Match(commandLine));

    // ===== 見せ方 =====

    private static TerminalSupportSubject Subject(string? running = null, ClaudeAgentTab? agent = null)
        => new(Guid.NewGuid(), "pwsh", @"C:\ws", running, running is null ? null : DateTime.UtcNow, agent);

    [Fact]
    public void Mode_FollowsForeground()
    {
        Assert.Equal(TerminalSupportMode.Shell, Subject().Mode);
        Assert.Equal(TerminalSupportMode.Program, Subject("lazygit").Mode);
        Assert.Equal(TerminalSupportMode.Program, Subject("dotnet test").Mode);
    }

    [Fact]
    public void Mode_ClaudeWinsOverRunningCommand()
    {
        // タイトルの印が出る前（起動直後）でもコマンド行で分かる
        Assert.Equal(TerminalSupportMode.ClaudeCode, Subject("claude").Mode);
        // 常駐の復元直後は C が来ないまま印だけ分かる（§38.7）
        var agent = new ClaudeAgentTab(Guid.NewGuid(), ClaudeAgentState.Waiting, "題", null, null, 0);
        Assert.Equal(TerminalSupportMode.ClaudeCode, Subject(agent: agent).Mode);
    }

    // ===== Claude の記録の畳み方 =====

    private static string User(string text, string at)
        => JsonSerializer.Serialize(new { type = "user", timestamp = at, cwd = @"C:\ws", message = new { role = "user", content = text } });

    private static string Assistant(string id, object[] content, string at, int input = 1000)
        => JsonSerializer.Serialize(new
        {
            type = "assistant", timestamp = at,
            message = new
            {
                id, model = "claude-opus-5-5", role = "assistant", content,
                usage = new { input_tokens = input, output_tokens = 5, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 },
            },
        });

    private static object ToolUse(string id, string name, object input) => new { type = "tool_use", id, name, input };

    private static string Result(string toolUseId, string content, string at, bool isError = false)
        => JsonSerializer.Serialize(new
        {
            type = "user", timestamp = at,
            message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = toolUseId, content, is_error = isError } } },
        });

    private static string Reply(string id, string text, string at)
        => Assistant(id, [new { type = "text", text }], at);

    private static string[] SampleSession() =>
    [
        User("一つ目の頼みごと", "2026-10-19T01:00:00Z"),
        Assistant("m1", [ToolUse("t1", "Read", new { file_path = @"C:\ws\a.cs" })], "2026-10-19T01:00:01Z"),
        Result("t1", "ok", "2026-10-19T01:00:02Z"),
        Assistant("m2", [ToolUse("t2", "Write", new { file_path = @"C:\ws\new.cs", content = "x" })], "2026-10-19T01:00:03Z"),
        Result("t2", "ok", "2026-10-19T01:00:04Z"),
        Reply("r1", "new.cs を作りました。", "2026-10-19T01:00:05Z"),
        User("二つ目の頼みごと", "2026-10-19T01:01:00Z"),
        Assistant("m3", [ToolUse("t3", "Edit", new { file_path = @"C:\ws\b.cs", old_string = "a", new_string = "b" })], "2026-10-19T01:01:01Z"),
        Result("t3", "ok", "2026-10-19T01:01:02Z"),
        Assistant("m4", [ToolUse("t4", "Edit", new { file_path = @"C:\ws\c.cs", old_string = "a", new_string = "b" })], "2026-10-19T01:01:03Z"),
        Result("t4", "The user doesn't want to proceed with this tool use.", "2026-10-19T01:01:04Z"),
        Assistant("m5", [ToolUse("t5", "Edit", new { file_path = @"C:\ws\d.cs", old_string = "zzz", new_string = "b" })], "2026-10-19T01:01:05Z"),
        Result("t5", "String to replace not found", "2026-10-19T01:01:06Z", isError: true),
        Assistant("m6", [ToolUse("t6", "Edit", new { file_path = @"C:\ws\new.cs", old_string = "x", new_string = "y" })], "2026-10-19T01:01:07Z", input: 52_000),
        Result("t6", "ok", "2026-10-19T01:01:08Z"),
        Assistant("m7", [ToolUse("t7", "Bash", new { command = "dotnet test" })], "2026-10-19T01:01:09Z", input: 60_000),
        JsonSerializer.Serialize(new { type = "ai-title", aiTitle = "テストを直す", sessionId = "s1" }),
    ];

    private static ClaudeSessionDigest Digest(string[] lines)
        => ClaudeSessionDigest.Build("s1", @"C:\t\s1.jsonl", DateTime.UtcNow, lines);

    [Fact]
    public void Digest_SessionFiles_OnlySuccessfulWrites_NewestFirst()
    {
        var digest = Digest(SampleSession());
        // Read は触ったうちに入れない・拒否と失敗は書き換えていない
        Assert.Equal([@"C:\ws\new.cs", @"C:\ws\b.cs"], digest.SessionFiles.Select(f => f.Path).ToArray());
        var created = digest.SessionFiles[0];
        Assert.True(created.Created);     // 最初が Write
        Assert.Equal(2, created.Edits);   // ターンを跨いだ Write＋Edit をまとめる
        Assert.False(digest.SessionFiles[1].Created);
    }

    [Fact]
    public void Digest_SplitsCallsIntoTurnsByPrompt()
    {
        var digest = Digest(SampleSession());
        Assert.Equal(2, digest.Turns.Count);

        var first = digest.Turns[0];
        Assert.Equal("一つ目の頼みごと", first.Prompt);
        Assert.Equal(["Read", "Write"], first.Calls.Select(c => c.Tool).ToArray());
        Assert.Equal("new.cs を作りました。", first.Reply);
        Assert.True(Assert.Single(first.Files).Created);

        var current = digest.CurrentTurn!;
        Assert.Equal("二つ目の頼みごと", current.Prompt);
        Assert.Equal(5, current.Calls.Count);
        // このターンで変えたのは new.cs と b.cs（new.cs はこのターンでは Edit なので「新規」ではない）
        Assert.Equal([@"C:\ws\new.cs", @"C:\ws\b.cs"], current.Files.Select(f => f.Path).ToArray());
        Assert.False(current.Files[0].Created);
        Assert.Equal(1, current.Commands);
        Assert.Equal("Edit", Assert.Single(current.Failures).Tool);   // 拒否は失敗に数えない
        Assert.Null(current.Reply);                                   // まだ返事が無い
    }

    [Fact]
    public void Digest_MidTurnInstruction_StartsANewTurn()
    {
        // 作業の途中に足した指示は user の行ではなく queued_command の添付として残る（Claude Code の実記録の形）。
        var lines = SampleSession().Append(JsonSerializer.Serialize(new
        {
            type = "attachment", timestamp = "2026-10-19T01:02:00Z",
            attachment = new { type = "queued_command", prompt = "表示を整理して", commandMode = "prompt", humanTurn = true },
        })).Append(Assistant("m8", [ToolUse("t8", "Read", new { file_path = @"C:\ws\e.cs" })], "2026-10-19T01:02:01Z"))
            .ToArray();
        var digest = Digest(lines);
        Assert.Equal(3, digest.Turns.Count);
        Assert.Equal("表示を整理して", digest.CurrentTurn!.Prompt);
        Assert.Equal("Read", Assert.Single(digest.CurrentTurn.Calls).Tool);
    }

    [Fact]
    public void Digest_TaskNotification_IsNotAPrompt()
    {
        // バックグラウンドの作業が終わると、Claude Code は user の行として通知を差し込む（実記録の形）。人間の頼みごとではない。
        var lines = SampleSession()
            .Append(User("<task-notification>\n<task-id>b5j3</task-id>\n<status>completed</status>\n</task-notification>", "2026-10-19T01:03:00Z"))
            .Append(Reply("r9", "テストが通りました。", "2026-10-19T01:03:01Z"))
            .ToArray();
        var digest = Digest(lines);
        Assert.Equal(2, digest.Turns.Count);
        Assert.Equal("テストが通りました。", digest.CurrentTurn!.Reply);   // 通知のあとの返事は、続いている頼みごとのもの
    }

    [Fact]
    public void Digest_PendingCall_IsTheLastCallWithoutResult()
    {
        var digest = Digest(SampleSession());
        Assert.Equal("dotnet test", digest.PendingCall?.Command);
        Assert.Equal("テストを直す", digest.Title);
        Assert.Equal(60_000, digest.ContextTokens);       // 最後の応答のもの（m6 の 52k と足さない）

        // 結果が来たら実行中ではない
        var done = SampleSession().Append(Result("t7", "ok", "2026-10-19T01:01:30Z")).ToArray();
        Assert.Null(Digest(done).PendingCall);
    }

    [Fact]
    public void Digest_Turns_Capped()
    {
        var lines = Enumerable.Range(0, ClaudeSessionDigest.MaxTurns + 5)
            .Select(i => User($"頼み {i}", "2026-10-19T01:00:00Z"))
            .ToArray();
        var digest = Digest(lines);
        Assert.Equal(ClaudeSessionDigest.MaxTurns, digest.Turns.Count);
        Assert.Equal($"頼み {ClaudeSessionDigest.MaxTurns + 4}", digest.CurrentTurn!.Prompt);
    }

    // ===== 追記分だけ読む（§39.8） =====

    [Fact]
    public void Reader_FedInChunks_EqualsFullParse_AndCountsUsageOncePerMessage()
    {
        var lines = SampleSession().Append(Assistant("m7", [new { type = "text", text = "同じ応答の別ブロック" }],
            "2026-10-19T01:01:10Z", input: 60_000)).ToArray();
        var full = sk0ya.Loomo.Core.Harness.ClaudeTranscriptParser.Parse("s1", "p", lines);

        var reader = new sk0ya.Loomo.Core.Harness.ClaudeTranscriptReader();
        reader.Feed("p", lines.Take(5), isSubagent: false);   // t2 の結果の手前で切る（結果待ちを持ち越す）
        reader.Feed("p", lines.Skip(5), isSubagent: false);
        var chunked = reader.Snapshot("s1", "p");

        Assert.Equal(full.ToolCalls.Select(c => (c.Index, c.LineNumber, c.Tool, c.HasResult, c.IsError, c.IsRejected)),
            chunked.ToolCalls.Select(c => (c.Index, c.LineNumber, c.Tool, c.HasResult, c.IsError, c.IsRejected)));
        Assert.Equal(full.PromptMarks, chunked.PromptMarks);
        Assert.Equal(full.Usage, chunked.Usage);   // m7 は2行に分かれても1回
        Assert.Equal(lines.Length, reader.LinesRead("p"));
    }

    private string WriteTranscript(IEnumerable<string> lines, string name = "s1")
    {
        var path = Path.Combine(_base, name + ".jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private static void Append(string path, string text) => File.AppendAllText(path, text);

    [Fact]
    public void Follower_ReadsOnlyWhatWasAppended_AndMatchesAFullRead()
    {
        var lines = SampleSession();
        var path = WriteTranscript(lines.Take(6));
        var follower = new ClaudeSessionFollower(path);
        var (v1, first) = follower.Refresh();
        Assert.Equal("new.cs を作りました。", Assert.Single(first!.Turns).Reply);

        // 変わっていなければ同じ版・同じ digest
        var (same, again) = follower.Refresh();
        Assert.Equal(v1, same);
        Assert.Same(first, again);

        // 書きかけの行は改行が来るまで読まない
        var rest = lines.Skip(6).ToArray();
        var cut = rest[0].Length / 2;
        Append(path, rest[0][..cut]);
        Assert.Single(follower.Refresh().Digest!.Turns);
        Append(path, rest[0][cut..] + "\n" + string.Join("\n", rest.Skip(1)) + "\n");
        var (v2, latest) = follower.Refresh();
        Assert.True(v2 > v1);

        var expected = ClaudeSessionDigest.Build("s1", path, DateTime.UtcNow, lines);
        Assert.Equal(expected.Turns.Select(t => (t.Prompt, t.PromptLine, t.Calls.Count, t.Files.Count, t.Reply)),
            latest!.Turns.Select(t => (t.Prompt, t.PromptLine, t.Calls.Count, t.Files.Count, t.Reply)));
        Assert.Equal(expected.PendingCall?.Command, latest.PendingCall?.Command);
        Assert.Equal(expected.Title, latest.Title);
        Assert.Equal(expected.ContextTokens, latest.ContextTokens);
    }

    [Fact]
    public void Follower_FileRewrittenShorter_StartsOver()
    {
        var path = WriteTranscript(SampleSession());
        var follower = new ClaudeSessionFollower(path);
        Assert.Equal(2, follower.Refresh().Digest!.Turns.Count);

        WriteTranscript([User("やり直し", "2026-10-19T02:00:00Z")]);
        var digest = follower.Refresh().Digest!;
        Assert.Equal("やり直し", Assert.Single(digest.Turns).Prompt);
        Assert.Null(digest.Title);
    }

    [Fact]
    public void Follower_ReadsSubagentTranscripts_IntoTheTurnByTime()
    {
        var lines = SampleSession().Take(10)   // 二つ目の頼みごと（01:01:00）と、その最初の操作まで
            .Append(Assistant("m9", [ToolUse("a1", "Agent", new { subagent_type = "Explore", prompt = "調べて" })], "2026-10-19T01:01:20Z"))
            .ToArray();
        var path = WriteTranscript(lines);
        var follower = new ClaudeSessionFollower(path);
        Assert.Equal(1, follower.Refresh().Digest!.CurrentTurn!.Subagents);

        // サブエージェントの記録はあとから現れる
        var subFolder = Directory.CreateDirectory(Path.Combine(_base, "s1", "subagents")).FullName;
        File.WriteAllText(Path.Combine(subFolder, "agent-x.jsonl"), string.Join("\n",
            Assistant("sa1", [ToolUse("st1", "Write", new { file_path = @"C:\ws\sub.cs", content = "x" })], "2026-10-19T01:01:30Z"),
            Result("st1", "ok", "2026-10-19T01:01:31Z")) + "\n");
        var turn = follower.Refresh().Digest!.CurrentTurn!;
        Assert.Contains(turn.Calls, c => c.FromSubagent && c.Tool == "Write");
        Assert.Contains(turn.Files, f => f.Path == @"C:\ws\sub.cs");
        Assert.Equal(1, turn.Subagents);   // サブエージェントの中の操作は数に入れない
    }

    [Fact]
    public void Follower_MissingFile_GivesNoDigest()
    {
        var follower = new ClaudeSessionFollower(Path.Combine(_base, "none.jsonl"));
        Assert.Null(follower.Refresh().Digest);
    }

    [Fact]
    public void SplitLines_LikeReadLine()
        => Assert.Equal(["a", "b", "", "c"], ClaudeSessionFollower.SplitLines("a\r\nb\n\nc\n"));

    // ===== タブのセッションを確かめる（推量しない） =====

    private static ClaudeLiveSession Live(int pid, string id, string cwd = @"C:\ws", int startedMinutesAgo = 10)
        => new(pid, id, cwd, DateTimeOffset.UtcNow.AddMinutes(-startedMinutesAgo));

    private static readonly IReadOnlySet<string> NoneClaimed = new HashSet<string>();

    [Fact]
    public void Match_IgnoresClaudeRunningOutsideLoomo()
    {
        // 同じフォルダーで Loomo の外（別の端末）でも claude が動いている——以前の「最新の記録」推量はこちらを出していた
        var sessions = new[] { Live(100, "outside"), Live(200, "inside") };
        var match = ClaudeLiveSessions.Match(sessions, pid => pid == 200, new ClaudeTabClue(@"C:\ws", null), NoneClaimed);
        Assert.Equal("inside", match.Session?.SessionId);
    }

    [Fact]
    public void Match_RequiresSameFolder_AndSkipsSessionsOfOtherTabs()
    {
        var sessions = new[] { Live(1, "other-folder", @"C:\other"), Live(2, "claimed"), Live(3, "mine", @"C:\WS\") };
        var match = ClaudeLiveSessions.Match(sessions, _ => true, new ClaudeTabClue(@"C:\ws", null),
            new HashSet<string> { "claimed" });
        Assert.Equal("mine", match.Session?.SessionId);   // 大文字小文字・末尾の区切りは同じ場所
    }

    [Fact]
    public void Match_SeveralInSameFolder_PicksByWhenClaudeWasTyped()
    {
        var now = DateTime.UtcNow;
        var sessions = new[]
        {
            new ClaudeLiveSession(1, "early", @"C:\ws", now.AddMinutes(-30)),
            new ClaudeLiveSession(2, "typed-here", @"C:\ws", now.AddMinutes(-5).AddSeconds(2)),
        };
        var match = ClaudeLiveSessions.Match(sessions, _ => true, new ClaudeTabClue(@"C:\ws", now.AddMinutes(-5)), NoneClaimed);
        Assert.Equal("typed-here", match.Session?.SessionId);
    }

    [Fact]
    public void Match_Ambiguous_RefusesInsteadOfGuessing()
    {
        // claude を打った時刻が分からない（常駐の復元直後など）。どちらか分からないなら出さない。
        var sessions = new[] { Live(1, "a"), Live(2, "b") };
        var match = ClaudeLiveSessions.Match(sessions, _ => true, new ClaudeTabClue(@"C:\ws", null), NoneClaimed);
        Assert.Null(match.Session);
        Assert.Contains("特定できません", match.Reason);
    }

    [Fact]
    public void Match_NoneFound_SaysSo()
    {
        var match = ClaudeLiveSessions.Match([], _ => true, new ClaudeTabClue(@"C:\ws", null), NoneClaimed);
        Assert.Null(match.Session);
        Assert.NotNull(match.Reason);
    }

    [Fact]
    public void Match_TabShellKnown_PicksItsDescendant_IgnoringFolderAndTime()
    {
        // 同じ場所で二つ動いていて、打った時刻も分からない——場所と時刻では決まらない形。
        var sessions = new[] { Live(12, "mine"), Live(22, "other") };
        var processes = new Dictionary<int, (int Parent, string Name)>
        {
            [11] = (10, "pwsh.exe"), [12] = (11, "claude.exe"),
            [21] = (10, "pwsh.exe"), [22] = (21, "claude.exe"),
        };
        var clue = new ClaudeTabClue(@"C:\elsewhere", null);   // claude の中で場所が変わっていても
        var match = ClaudeLiveSessions.Match(sessions, _ => true, clue, NoneClaimed,
            pid => ClaudeLiveSessions.IsDescendantOf(pid, 11, processes));
        Assert.Equal("mine", match.Session?.SessionId);

        // シェルの中に居なければ、場所が同じでも結び付けない
        var none = ClaudeLiveSessions.Match([Live(22, "other")], _ => true, new ClaudeTabClue(@"C:\ws", null), NoneClaimed,
            pid => ClaudeLiveSessions.IsDescendantOf(pid, 11, processes));
        Assert.Null(none.Session);
        Assert.NotNull(none.Reason);
    }

    [Fact]
    public void IsDescendantOf_WalksParents_StopsOnLoops()
    {
        var processes = new Dictionary<int, (int Parent, string Name)>
        {
            [11] = (10, "pwsh.exe"), [12] = (11, "node.exe"), [13] = (12, "claude.exe"),
            [40] = (41, "a.exe"), [41] = (40, "b.exe"),
        };
        Assert.True(ClaudeLiveSessions.IsDescendantOf(13, 11, processes));
        Assert.False(ClaudeLiveSessions.IsDescendantOf(11, 11, processes));   // 自分自身は含めない
        Assert.False(ClaudeLiveSessions.IsDescendantOf(40, 11, processes));
    }

    [Fact]
    public void InsideLoomoTerminal_WalksParentsToPtyHostOrLoomo()
    {
        var processes = new Dictionary<int, (int Parent, string Name)>
        {
            [10] = (1, "sk0ya.Loomo.Pty.Host.exe"),
            [11] = (10, "pwsh.exe"),
            [12] = (11, "claude.exe"),            // 常駐の端末の中
            [20] = (1, "sk0ya.Loomo.App.exe"),
            [21] = (20, "pwsh.exe"),
            [22] = (21, "claude.exe"),            // 常駐でない端末の中（Loomo 自身の子）
            [30] = (1, "WindowsTerminal.exe"),
            [31] = (30, "pwsh.exe"),
            [32] = (31, "claude.exe"),            // Loomo の外
            [40] = (41, "claude.exe"),            // 親が輪になっている（PID の再利用）
            [41] = (40, "pwsh.exe"),
        };
        Assert.True(ClaudeLiveSessions.IsInsideLoomoTerminal(12, processes, loomoPid: 20));
        Assert.True(ClaudeLiveSessions.IsInsideLoomoTerminal(22, processes, loomoPid: 20));
        Assert.False(ClaudeLiveSessions.IsInsideLoomoTerminal(32, processes, loomoPid: 20));
        Assert.False(ClaudeLiveSessions.IsInsideLoomoTerminal(40, processes, loomoPid: 20));
        Assert.False(ClaudeLiveSessions.IsInsideLoomoTerminal(99, processes, loomoPid: 20));   // もう居ない
    }

    [Fact]
    public void ParseLiveSession_RealShape()
    {
        // Claude Code 2.1.30x の sessions/<pid>.json の実物の形（抜粋）
        var json = """{"pid":22188,"sessionId":"24da4bf8-6c69-445b-92cb-4d0e4de68906","cwd":"C:\\Projects\\Loomo","startedAt":1792407250459,"kind":"interactive","status":"idle"}""";
        var session = ClaudeLiveSessions.Parse(json)!;
        Assert.Equal(22188, session.Pid);
        Assert.Equal("24da4bf8-6c69-445b-92cb-4d0e4de68906", session.SessionId);
        Assert.Equal(@"C:\Projects\Loomo", session.Cwd);
        Assert.Equal(
            Path.Combine(@"C:\root", "C--Projects-Loomo", "24da4bf8-6c69-445b-92cb-4d0e4de68906.jsonl"),
            ClaudeLiveSessions.TranscriptPath(session, @"C:\root"));
        Assert.Null(ClaudeLiveSessions.Parse("""{"pid":1,"sessionId":"x","cwd":"C:\\a","kind":"daemon"}"""));
    }

    // ===== ViewModel =====

    [Fact]
    public void Update_Shell_ListsOnlyThisTabsRuns()
    {
        var vm = new TerminalSupportViewModel();
        var subject = Subject();
        var mine = new TerminalCommandOutput("dotnet test", "ok", 1, DateTime.UtcNow, false, subject.TabId);
        var other = new TerminalCommandOutput("git status", "ok", 0, DateTime.UtcNow, false, Guid.NewGuid());

        vm.Update(subject, [mine, other], output => ReferenceEquals(output, mine), DateTime.UtcNow);

        Assert.True(vm.IsShell);
        var row = Assert.Single(vm.Runs);
        Assert.Equal("dotnet test", row.Title);
        Assert.True(row.Failed);
        Assert.True(row.CanCompare);
    }

    [Fact]
    public void Update_Program_ShowsKeysOfKnownProgram()
    {
        var vm = new TerminalSupportViewModel();
        vm.Update(Subject("lazygit"), [], _ => false, DateTime.UtcNow);
        Assert.True(vm.IsProgram);
        Assert.Equal("lazygit", vm.ProgramName);
        Assert.Contains(vm.Keys, k => k.Keys == "?");

        vm.Update(Subject("dotnet test"), [], _ => false, DateTime.UtcNow);
        Assert.Equal("dotnet", vm.ProgramName);
        Assert.Empty(vm.Keys);
    }

    private static TerminalSupportSubject ClaudeSubject(ClaudeAgentState state)
    {
        var tabId = Guid.NewGuid();
        return new(tabId, "pwsh", @"C:\ws", "claude", DateTime.UtcNow,
            new ClaudeAgentTab(tabId, state, null, null, null, 0));
    }

    // ===== 会話を読み物にする =====

    private static string Body(string[] lines, bool working = false, Func<string, bool>? inWorkspace = null)
        => ClaudeConversationDocument.BuildBody(Digest(lines), inWorkspace ?? (_ => true), p => p, working);

    [Fact]
    public void Document_PromptIsHeading_ReplyIsRenderedMarkdown()
    {
        var lines = SampleSession()
            .Append(Result("t7", "ok", "2026-10-19T01:01:30Z"))
            .Append(Reply("r2", "## 結果\n\n| 件 | 状態 |\n|---|---|\n| test | ok |", "2026-10-19T01:01:31Z"))
            .ToArray();
        var body = Body(lines);

        // 頼みごとは見出し（アウトライン欄に並ぶよう id つき。id は記録の行＝古いターンが落ちても変わらない）
        Assert.Contains("<h2 id=\"cc-turn-1\">一つ目の頼みごと</h2>", body);
        Assert.Contains("<h2 id=\"cc-turn-7\">二つ目の頼みごと</h2>", body);
        // 返事は Markdown として描かれる（表が表になる）。見出しは頼みごとの下へ2段下がる
        Assert.Contains("<table", body);
        Assert.Contains("<h4", body);
        Assert.DoesNotContain("## 結果", body);
    }

    [Fact]
    public void Document_EarlierTextsAreFoldedAsNotes_LastIsTheReply()
    {
        var lines = SampleSession()
            .Append(Reply("r2", "テストを流します。", "2026-10-19T01:01:20Z"))
            .Append(Result("t7", "ok", "2026-10-19T01:01:30Z"))
            .Append(Reply("r3", "テストが通りました。", "2026-10-19T01:01:31Z"))
            .ToArray();
        var body = Body(lines);
        var notes = body.IndexOf("途中のメモ 1 件", StringComparison.Ordinal);
        Assert.True(notes > 0);
        Assert.True(body.IndexOf("テストを流します。", StringComparison.Ordinal) > notes);
        Assert.True(body.IndexOf("テストが通りました。", StringComparison.Ordinal) > body.IndexOf("</details>", notes, StringComparison.Ordinal));
    }

    [Fact]
    public void Document_Working_MarksTheLastTurnInProgress()
    {
        Assert.Contains("Claude · 作業中", Body(SampleSession(), working: true));
        Assert.DoesNotContain("Claude · 作業中", Body(SampleSession(), working: false));
    }

    [Fact]
    public void Document_ChangedFilesAreLinks_OutsideWorkspaceOnlyCounted()
    {
        var body = Body(SampleSession(), inWorkspace: path => !path.EndsWith("new.cs", StringComparison.Ordinal));
        Assert.Contains("href=\"file:///C:/ws/b.cs\"", body);
        Assert.DoesNotContain("file:///C:/ws/new.cs", body);
        Assert.Contains("ワークスペースの外 1 ファイル", body);
        Assert.Contains("✗ 失敗 1", body);
    }

    [Fact]
    public void Document_ReplyIdsAreScopedPerTurn()
    {
        var lines = new[]
        {
            User("一つ目", "2026-10-19T01:00:00Z"),
            Reply("r1", "## まとめ\n\n[上へ](#まとめ)", "2026-10-19T01:00:01Z"),
            User("二つ目", "2026-10-19T01:01:00Z"),
            Reply("r2", "## まとめ", "2026-10-19T01:01:01Z"),
        };
        var body = Body(lines);
        Assert.Contains("id=\"t1-まとめ\"", body);
        Assert.Contains("id=\"t3-まとめ\"", body);
        Assert.Contains("href=\"#t1-まとめ\"", body);   // 同じターンの中のリンクも付いていく
    }

    [Fact]
    public void Document_Cache_RedrawsOnlyTheTurnThatChanged()
    {
        var cache = new ClaudeConversationDocument.RenderCache();
        var before = Digest(SampleSession());
        var after = Digest(SampleSession().Append(Result("t7", "ok", "2026-10-19T01:01:30Z"))
            .Append(Reply("r2", "通りました。", "2026-10-19T01:01:31Z")).ToArray());

        ClaudeConversationDocument.BuildBody(before, _ => true, p => p, working: true, cache);
        Assert.Equal(ClaudeConversationDocument.TurnKey(before.Turns[0], false), ClaudeConversationDocument.TurnKey(after.Turns[0], false));
        Assert.NotEqual(ClaudeConversationDocument.TurnKey(before.Turns[1], true), ClaudeConversationDocument.TurnKey(after.Turns[1], false));

        var cached = ClaudeConversationDocument.BuildBody(after, _ => true, p => p, working: false, cache);
        Assert.Equal(ClaudeConversationDocument.BuildBody(after, _ => true, p => p, working: false), cached);
        Assert.Equal(2, cache.Count);   // 使われなかった前の版は捨てる
    }

    [Fact]
    public void Document_PromptIsEncoded()
    {
        var body = Body([User("<script>alert(1)</script> を直して", "2026-10-19T01:00:00Z")]);
        Assert.DoesNotContain("<script>alert", body);
        Assert.Contains("&lt;script&gt;", body);
    }

    [Theory]
    [InlineData("# 見出し", "### 見出し")]
    [InlineData("## 節\n本文", "#### 節\n本文")]
    [InlineData("##### 深い", "###### 深い")]
    [InlineData("```\n# コメント\n```", "```\n# コメント\n```")]   // コードブロックの中は触らない
    [InlineData("#タグ", "#タグ")]                                 // 空白の無い # は見出しではない
    public void DemoteHeadings(string markdown, string expected)
        => Assert.Equal(expected, ClaudeConversationDocument.DemoteHeadings(markdown));

    // ===== ViewModel（Claude） =====

    private static TerminalSupportSubject ClaudeSubject(ClaudeAgentState state, Guid? tabId = null)
    {
        var id = tabId ?? Guid.NewGuid();
        return new(id, "pwsh", @"C:\ws", "claude", DateTime.UtcNow,
            new ClaudeAgentTab(id, state, null, null, null, 0));
    }

    [Fact]
    public async Task ApplyDigest_BuildsTheConversationBody()
    {
        var vm = new TerminalSupportViewModel();
        vm.Update(ClaudeSubject(ClaudeAgentState.TurnEnded), [], _ => false, DateTime.UtcNow);
        await vm.ApplyDigest(Digest(SampleSession()));
        Assert.Contains("二つ目の頼みごと", vm.ConversationBody);
        Assert.Equal("✳ あなたの番", vm.NowHeadline);
        Assert.Equal(TerminalSupportNowTone.Human, vm.NowTone);
        Assert.Contains("2 ターン", vm.ClaudeFacts);
    }

    [Fact]
    public async Task StateChange_RebuildsTheLastTurnAsInProgress()
    {
        var vm = new TerminalSupportViewModel();
        var tabId = Guid.NewGuid();
        vm.Update(ClaudeSubject(ClaudeAgentState.TurnEnded, tabId), [], _ => false, DateTime.UtcNow);
        await vm.ApplyDigest(Digest(SampleSession()));
        Assert.DoesNotContain("作業中", vm.ConversationBody);

        vm.Update(ClaudeSubject(ClaudeAgentState.Working, tabId), [], _ => false, DateTime.UtcNow);
        for (var i = 0; i < 50 && !vm.ConversationBody.Contains("作業中"); i++)
            await Task.Delay(20);   // 組み直しは裏のスレッド
        Assert.Contains("Claude · 作業中", vm.ConversationBody);
        Assert.Equal("◐ 作業中", vm.NowHeadline);
    }

    [Fact]
    public async Task ClaudeTitle_PrefersSpecificLiveTitle_ThenAiTitle()
    {
        var vm = new TerminalSupportViewModel();
        var tabId = Guid.NewGuid();
        TerminalSupportSubject WithTitle(string? title)
            => new(tabId, "pwsh", @"C:\ws", "claude", DateTime.UtcNow,
                new ClaudeAgentTab(tabId, ClaudeAgentState.Working, title, null, null, 0));

        // 汎用の「Claude Code」は記録の ai-title に負ける
        vm.Update(WithTitle(TerminalSupportViewModel.GenericClaudeTitle), [], _ => false, DateTime.UtcNow);
        await vm.ApplyDigest(Digest(SampleSession()));
        Assert.Equal("テストを直す", vm.ClaudeTitle);

        // 具体的な題がタイトルに出たらそちら（いまの作業に追従する）。読み直しても戻らない。
        vm.Update(WithTitle("Fix src/foo.cs"), [], _ => false, DateTime.UtcNow);
        await vm.ApplyDigest(Digest(SampleSession()));
        Assert.Equal("Fix src/foo.cs", vm.ClaudeTitle);
    }

    [Fact]
    public async Task Unresolved_ShowsNothingAndSaysWhy()
    {
        var vm = new TerminalSupportViewModel();
        vm.Update(ClaudeSubject(ClaudeAgentState.Working), [], _ => false, DateTime.UtcNow);
        await vm.RefreshClaudeAsync(null, "同じ場所で Claude Code が 2 つ動いていて、このタブのセッションを特定できません。");
        Assert.Equal("", vm.ConversationBody);
        Assert.False(vm.HasClaudeSession);
        Assert.Contains("特定できません", vm.ClaudeNotice);
    }

    [Fact]
    public void Update_Claude_HasNoKeyList()
    {
        // 操作キーは TUI の面だけ。Claude の面は会話を読む場所（キーの案内は Claude Code 自身が出している）。
        var vm = new TerminalSupportViewModel();
        vm.Update(ClaudeSubject(ClaudeAgentState.Working), [], _ => false, DateTime.UtcNow);
        Assert.Empty(vm.Keys);
    }

    [Theory]
    [InlineData(5, "0:05")]
    [InlineData(83, "1:23")]
    [InlineData(3725, "1:02:05")]
    public void FormatElapsed(int seconds, string expected)
        => Assert.Equal(expected, TerminalSupportViewModel.FormatElapsed(TimeSpan.FromSeconds(seconds)));
}
