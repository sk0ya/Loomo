using System.IO;
using System.Linq;
using System.Text.Json;
using sk0ya.Loomo.Core.Harness;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ハーネスペイン（§37）の解析部分。記録の読み方（usage の重複除去・人間の拒否・逃がされた出力の大きさ）、
/// 規則の検出（§37.5）、指標（§37.4：受け入れ0件では値を作らない）、版の引き方、判定とタスクの書き出し、
/// 記録フォルダーの探し方（slug と cwd の確認）を見る。画面は対象外。
/// </summary>
public sealed class HarnessTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), $"loomo-harness-{Guid.NewGuid():N}");

    public HarnessTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (IOException) { }
    }

    // ===== 記録の行を組む小道具 =====

    private static string User(string text, string at = "2026-10-11T01:00:00Z", bool meta = false)
        => JsonSerializer.Serialize(new
        {
            type = "user", timestamp = at, cwd = @"C:\ws", isMeta = meta,
            message = new { role = "user", content = text },
        });

    private static string Assistant(string id, object[] content, int input = 10, int output = 5, int cacheRead = 100,
        int cacheCreate = 50, string at = "2026-10-11T01:00:01Z")
        => JsonSerializer.Serialize(new
        {
            type = "assistant", timestamp = at,
            message = new
            {
                id, model = "claude-opus-5-5", role = "assistant", content,
                usage = new
                {
                    input_tokens = input, output_tokens = output,
                    cache_read_input_tokens = cacheRead, cache_creation_input_tokens = cacheCreate,
                },
            },
        });

    private static object ToolUse(string id, string name, object input) => new { type = "tool_use", id, name, input };

    private static string Result(string toolUseId, string content, bool isError = false, string at = "2026-10-11T01:00:02Z")
        => JsonSerializer.Serialize(new
        {
            type = "user", timestamp = at,
            message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = toolUseId, content, is_error = isError } } },
        });

    private static HarnessRun Parse(params string[] lines) => ClaudeTranscriptParser.Parse("run-1", @"C:\t\run-1.jsonl", lines);

    // ===== 記録の読み方（§37.2.1） =====

    [Fact]
    public void Usage_is_counted_once_per_message_id_even_when_split_across_lines()
    {
        // 同じ応答が content ブロックごとに3行に分かれ、3行とも同じ usage を持つ（実際の記録の形）。
        var run = Parse(
            User("やって"),
            Assistant("msg_1", [new { type = "thinking", thinking = "" }]),
            Assistant("msg_1", [new { type = "text", text = "はい" }]),
            Assistant("msg_1", [ToolUse("t1", "Read", new { file_path = @"C:\ws\a.cs" })]),
            Result("t1", "x"),
            Assistant("msg_2", [new { type = "text", text = "終わり" }], input: 1, output: 1, cacheRead: 0, cacheCreate: 0));

        Assert.Equal(new RunTokenUsage(11, 6, 100, 50), run.Usage);
        Assert.Single(run.ToolCalls);
    }

    [Fact]
    public void Human_rejection_is_not_counted_as_a_tool_failure()
    {
        var run = Parse(
            User("消して"),
            Assistant("m", [ToolUse("t1", "Bash", new { command = "rm -rf x" })]),
            Result("t1", "The user doesn't want to proceed with this tool use.", isError: true));

        var call = Assert.Single(run.ToolCalls);
        Assert.True(call.IsRejected);
        Assert.False(call.IsError);
        Assert.Equal(0, run.ToolErrorCount);
        Assert.Equal(0, run.ToolCallCountExcludingRejected);
    }

    [Fact]
    public void Persisted_output_size_comes_from_the_cli_note_not_the_preview()
    {
        var run = Parse(
            User("見て"),
            Assistant("m", [ToolUse("t1", "Bash", new { command = "dotnet build" })]),
            Result("t1", "<persisted-output>\nOutput too large (40.5KB). Full output saved to: C:\\x.txt\n\nPreview (first 2KB):\n…"));

        var call = Assert.Single(run.ToolCalls);
        Assert.True(call.OutputPersisted);
        Assert.Equal((int)(40.5 * 1024), call.ResultChars);
    }

    [Fact]
    public void Persisted_error_output_with_leading_whitespace_is_still_a_persisted_result()
    {
        // 失敗したコマンドの巨大な出力も逃がされる。印の前に空白・改行が付いても見落とさない。
        var run = Parse(
            User("見て"),
            Assistant("m", [ToolUse("t1", "Bash", new { command = "dotnet test" })]),
            Result("t1", "\n  <persisted-output>\nOutput too large (1.5MB). Full output saved to: C:\\x.txt\n\nPreview (first 2KB):\n…",
                isError: true));

        var call = Assert.Single(run.ToolCalls);
        Assert.True(call.OutputPersisted);
        Assert.True(call.IsError);
        Assert.Equal((int)(1.5 * 1024 * 1024), call.ResultChars);
    }

    [Fact]
    public void Persisted_output_marker_quoted_inside_file_text_is_not_a_persisted_result()
    {
        // 逃がしの印について書いたファイル（ハーネスの memory など）を読んだだけで、過大な出力と取り違えない。
        var text = "1\t---\n15\t- `<persisted-output>Output too large (40.4KB)` は括弧内の大きさを使う。";
        var run = Parse(
            User("見て"),
            Assistant("m", [ToolUse("t1", "Read", new { file_path = "C:\\m\\harness-pane.md" })]),
            Result("t1", text));

        var call = Assert.Single(run.ToolCalls);
        Assert.False(call.OutputPersisted);
        Assert.Equal(text.Length, call.ResultChars);
    }

    [Fact]
    public void Prompts_skip_meta_lines_and_slash_command_records()
    {
        var run = Parse(
            User("<local-command-caveat>…</local-command-caveat>", meta: true),
            User("<command-name>/clear</command-name>"),
            User("本当の指示 task:fix-login"));

        Assert.Equal(["本当の指示 task:fix-login"], run.Prompts);
        Assert.Equal("本当の指示 task:fix-login", run.Title);
    }

    [Fact]
    public void Ai_title_and_cost_state_are_taken_when_present()
    {
        var run = Parse(
            User("やって"),
            JsonSerializer.Serialize(new { type = "ai-title", aiTitle = "ログイン修正" }),
            JsonSerializer.Serialize(new { type = "cost-state", totalCostUSD = 1.25 }));

        Assert.Equal("ログイン修正", run.Title);
        Assert.Equal(1.25m, run.CostUsd);
    }

    [Fact]
    public void Missing_cost_stays_unknown()
        => Assert.Null(Parse(User("やって")).CostUsd);

    [Fact]
    public void Subagent_calls_and_usage_are_folded_into_the_run()
    {
        var sub = new[]
        {
            Assistant("sub_1", [ToolUse("s1", "Grep", new { pattern = "Foo" })], input: 1, output: 1, cacheRead: 0, cacheCreate: 0),
            Result("s1", "hit"),
        };
        var run = ClaudeTranscriptParser.Parse("run-1", @"C:\t\run-1.jsonl",
            [User("調べて"), Assistant("m", [ToolUse("a1", "Agent", new { subagent_type = "Explore", prompt = "x" })])],
            [(@"C:\t\run-1\subagents\agent-1.jsonl", sub)]);

        Assert.Equal(2, run.ToolCalls.Count);
        Assert.True(run.ToolCalls[1].FromSubagent);
        Assert.Equal(@"C:\t\run-1\subagents\agent-1.jsonl", run.ToolCalls[1].TranscriptPath);
        Assert.Equal(["Explore"], run.AgentsUsed);
        Assert.Equal(1, run.SubagentCount);
        Assert.Equal(10 + 1, run.Usage.Input); // 本体 10 ＋ サブエージェント 1
    }

    [Fact]
    public void Startup_input_is_the_first_main_response_input_skipping_synthetic_zero_usage()
    {
        var sub = new[] { Assistant("sub_1", [new { type = "text", text = "x" }], input: 999, cacheRead: 0, cacheCreate: 0) };
        var run = ClaudeTranscriptParser.Parse("run-1", @"C:\t\run-1.jsonl",
            [
                User("やって"),
                // API エラーの合成応答は usage が 0（開始時の入力にしない）。
                Assistant("err", [new { type = "text", text = "API Error" }], input: 0, output: 0, cacheRead: 0, cacheCreate: 0),
                Assistant("m1", [new { type = "text", text = "はい" }], input: 3, cacheRead: 20000, cacheCreate: 500),
                Assistant("m2", [new { type = "text", text = "終わり" }], input: 1, cacheRead: 90000, cacheCreate: 0),
            ],
            [(@"C:\t\run-1\subagents\agent-1.jsonl", sub)]);

        Assert.Equal(3 + 20000 + 500, run.StartupInput);
        Assert.Null(Parse(User("やって")).StartupInput);
    }

    [Fact]
    public void Startup_input_is_compared_by_median_so_a_resumed_session_does_not_dominate()
    {
        var runs = new[] { 20_000L, 22_000, 900_000 }.Select((v, i) => RunOf($"r{i}") with { StartupInput = v })
            .Append(RunOf("none")).ToList();

        Assert.Equal(22_000, HarnessMetrics.Summarize(runs, new Dictionary<string, HarnessVerdict>()).MedianStartupInput);
        Assert.Null(HarnessMetrics.Summarize([RunOf("none")], new Dictionary<string, HarnessVerdict>()).MedianStartupInput);
    }

    [Fact]
    public void Broken_lines_are_skipped()
    {
        var run = Parse(User("やって"), "{\"type\":\"assistant\",", "not json");
        Assert.Single(run.Prompts);
    }

    // ===== Run・ツール呼び出しを組む小道具 =====

    private static HarnessRun RunOf(string id, params RunToolCall[] calls) => new()
    {
        Id = id, TranscriptPath = $@"C:\t\{id}.jsonl", Title = id,
        StartedAt = DateTimeOffset.Parse("2026-10-11T00:00:00Z"),
        ToolCalls = calls.Select((c, i) => c with { Index = i, LineNumber = i + 1 }).ToList(),
        Prompts = ["x"],
    };

    private static RunToolCall Call(string tool, string? file = null, string? command = null, int chars = 100,
        bool error = false, string? errorHead = null, long? offset = null)
        => new()
        {
            Index = 0, LineNumber = 0, TranscriptPath = "", Tool = tool, Summary = file ?? command ?? tool,
            FilePath = file, Command = command, HasResult = true, ResultChars = chars, IsError = error,
            ErrorHead = errorHead, ReadOffset = offset, InputChars = 40,
        };

    // ===== 指標・版（§37.4・§37.6） =====

    [Fact]
    public void Tokens_per_accepted_include_failed_runs_and_are_undefined_without_acceptance()
    {
        var a = RunOf("a") with { Usage = new RunTokenUsage(100, 0, 0, 0) };
        var b = RunOf("b") with { Usage = new RunTokenUsage(300, 0, 0, 0) };

        Assert.Null(HarnessMetrics.Summarize([a, b], new Dictionary<string, HarnessVerdict>()).TokensPerAccepted);

        var verdicts = new Dictionary<string, HarnessVerdict>
        {
            ["a"] = new(HarnessVerdictKind.Accepted, DateTimeOffset.Now, null),
            ["b"] = new(HarnessVerdictKind.Rejected, DateTimeOffset.Now, null),
        };
        // 差し戻した b のぶんも分子に入る（成功だけの平均にしない）。
        Assert.Equal(400, HarnessMetrics.Summarize([a, b], verdicts).TokensPerAccepted);
    }

    [Fact]
    public void Version_at_a_time_is_the_last_harness_commit_before_it()
    {
        var timeline = HarnessVersionTimeline.ParseGitLog(
            "bbb\u001f" + DateTimeOffset.Parse("2026-10-05T00:00:00Z").ToUnixTimeSeconds() + "\u001fCLAUDE.md を直す\n"
            + "aaa\u001f" + DateTimeOffset.Parse("2026-10-01T00:00:00Z").ToUnixTimeSeconds() + "\u001f最初\n");

        Assert.Null(timeline.VersionAt(DateTimeOffset.Parse("2026-09-30T00:00:00Z")));
        Assert.Equal("aaa", timeline.VersionAt(DateTimeOffset.Parse("2026-10-03T00:00:00Z"))!.Commit);
        Assert.Equal("bbb", timeline.VersionAt(DateTimeOffset.Parse("2026-10-06T00:00:00Z"))!.Commit);
    }

    [Fact]
    public void Versions_are_compared_with_their_own_runs()
    {
        var v1 = new HarnessVersion("aaa", DateTimeOffset.Parse("2026-10-01T00:00:00Z"), "v1");
        var v2 = new HarnessVersion("bbb", DateTimeOffset.Parse("2026-10-05T00:00:00Z"), "v2");
        var runs = new[] { RunOf("old"), RunOf("new1"), RunOf("new2") };
        var rows = HarnessMetrics.CompareVersions(runs, new Dictionary<string, HarnessVerdict>(),
            r => r.Id == "old" ? v1 : v2);

        Assert.Equal(["bbb", "aaa"], rows.Select(r => r.Version!.Commit));
        Assert.Equal(2, rows[0].Summary.RunCount);
    }

    // ===== 分析プロンプト（§37.5） =====

    [Fact]
    public void Analysis_prompt_hands_over_transcripts_and_limits_changes_to_the_harness()
    {
        var v1 = new HarnessVersion("abcdef0123456789", DateTimeOffset.Parse("2026-10-01T00:00:00Z"), "v1");
        var rejected = RunOf("r1", Call("Bash", command: "dotnet build", error: true)) with
        {
            Title = "a|b\n改行", ToolErrorCount = 1, ToolCallCountExcludingRejected = 1, SubagentCount = 2,
        };
        var runs = new[] { rejected, RunOf("r2") };
        var verdicts = new Dictionary<string, HarnessVerdict> { ["r1"] = new(HarnessVerdictKind.Rejected, DateTimeOffset.Now, null) };
        var components = new[] { new HarnessComponent("instructions:CLAUDE.md", HarnessComponentKind.Instructions, "CLAUDE.md", @"C:\ws\CLAUDE.md") };

        var text = HarnessAnalysisPrompt.Build(runs, verdicts, r => r.Id == "r1" ? v1 : null, components,
            DateTimeOffset.Parse("2026-10-01T00:00:00Z"), DateTimeOffset.Parse("2026-10-11T00:00:00Z"), p => p);

        Assert.Contains("2 Run", text);
        Assert.Contains(@"`C:\t\r1.jsonl`", text);   // 記録のパスをそのまま渡す（エージェントが読みに行く）
        Assert.Contains("差し戻し", text);
        Assert.Contains("abcdef01", text);
        Assert.Contains("（サブ 2）", text);
        Assert.Contains("直す先はハーネスだけ", text);
        Assert.Contains("人間が了承したものだけ", text);
        Assert.Contains(@"指示 `C:\ws\CLAUDE.md`", text);
        Assert.Contains("permissions.deny", text);    // 守らせたいことは指示文でなく仕組みで（直し方の優先順位）
        Assert.Contains("| a／b 改行 |", text);       // 題の | と改行は表を壊すので潰す
    }

    [Fact]
    public void Analysis_prompt_states_instruction_length_and_startup_input()
    {
        var run = RunOf("r1") with { StartupInput = 31_000 };
        var components = new[]
        {
            new HarnessComponent("i:c", HarnessComponentKind.Instructions, "CLAUDE.md", @"C:\ws\CLAUDE.md", Lines: 420),
            new HarnessComponent("i:a", HarnessComponentKind.Instructions, "AGENTS.md", @"C:\ws\AGENTS.md", Lines: 12),
        };

        var text = HarnessAnalysisPrompt.Build([run], new Dictionary<string, HarnessVerdict>(), _ => null, components,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, p => p);

        Assert.Contains("CLAUDE.md`（420 行・毎回読まれる）", text);
        Assert.Contains("AGENTS.md`（12 行）", text);  // Claude Code は AGENTS.md を読み込まない
        Assert.Contains("中央値 31.0k", text);
    }

    [Fact]
    public void Analysis_prompt_caps_the_run_list()
    {
        var runs = Enumerable.Range(0, HarnessAnalysisPrompt.MaxRuns + 5).Select(i => RunOf($"r{i}")).ToList();

        var text = HarnessAnalysisPrompt.Build(runs, new Dictionary<string, HarnessVerdict>(), _ => null, [],
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, p => p);

        Assert.Contains($"r{HarnessAnalysisPrompt.MaxRuns - 1}.jsonl", text);
        Assert.DoesNotContain($"r{HarnessAnalysisPrompt.MaxRuns}.jsonl", text);
        Assert.Contains("ほか 5 Run", text);
    }

    // ===== 定義・判定・タスク =====

    [Fact]
    public void Definition_lists_claude_code_parts_and_harness_declarations()
    {
        var ws = Path.Combine(_base, "ws");
        Directory.CreateDirectory(Path.Combine(ws, ".claude", "skills", "review"));
        Directory.CreateDirectory(Path.Combine(ws, ".harness", "tasks"));
        Directory.CreateDirectory(Path.Combine(ws, ".harness", "rules"));
        File.WriteAllText(Path.Combine(ws, "CLAUDE.md"), "# x");
        File.WriteAllText(Path.Combine(ws, ".claude", "skills", "review", "SKILL.md"), "---");
        File.WriteAllText(Path.Combine(ws, ".claude", "settings.json"), "{ \"hooks\": { \"PostToolUse\": [] }, }");
        File.WriteAllText(Path.Combine(ws, ".harness", "harness.yaml"), "id: \"loomo\"\nversion: 3\nother: x\n");
        File.WriteAllText(Path.Combine(ws, ".harness", "tasks", "fix-login.md"), "\n# ログインを直す\n");
        File.WriteAllText(Path.Combine(ws, ".harness", "rules", "no-sleep.md"), "x");

        var def = HarnessDefinitionReader.Read([ws]);

        Assert.Equal("loomo", def.HarnessId);
        Assert.Equal("3", def.DeclaredVersion);
        Assert.Contains(def.Components, c => c.Kind == HarnessComponentKind.Instructions && c.Name == "CLAUDE.md");
        Assert.Contains(def.Components, c => c.Kind == HarnessComponentKind.Skill && c.Name == "review");
        Assert.Contains(def.Components, c => c.Kind == HarnessComponentKind.Hook && c.Name.StartsWith("PostToolUse"));
        Assert.Contains(def.Components, c => c.Kind == HarnessComponentKind.Rule && c.Name == "no-sleep");
        var task = Assert.Single(def.Tasks);
        Assert.Equal(("fix-login", "ログインを直す"), (task.Id, task.Title));
    }

    [Fact]
    public void Instructions_carry_their_line_count_and_auto_memory_is_added_from_the_transcript_folder()
    {
        var ws = Path.Combine(_base, "ws3");
        var transcripts = Path.Combine(_base, "projects", "C--ws3");
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(Path.Combine(transcripts, "memory"));
        File.WriteAllText(Path.Combine(ws, "CLAUDE.md"), "a\nb\nc\n");
        File.WriteAllText(Path.Combine(transcripts, "memory", "MEMORY.md"), "- x\n- y\n");

        var def = HarnessDefinitionReader.WithAutoMemory(HarnessDefinitionReader.Read([ws]), [transcripts, Path.Combine(_base, "none")]);

        Assert.Contains(def.Components, c => c is { Name: "CLAUDE.md", Lines: 3, AlwaysLoaded: true });
        var memory = Assert.Single(def.Components, c => c.Name.StartsWith("MEMORY.md"));
        Assert.Equal((2, false, true), (memory.Lines, memory.UserScope, memory.AlwaysLoaded));
    }

    [Fact]
    public void Claude_code_settings_files_are_listed_with_or_without_hooks_and_user_scope_is_marked()
    {
        var ws = Path.Combine(_base, "ws2");
        var user = Path.Combine(_base, "home", ".claude");
        Directory.CreateDirectory(Path.Combine(ws, ".claude"));
        Directory.CreateDirectory(Path.Combine(user, "commands"));
        File.WriteAllText(Path.Combine(ws, ".claude", "settings.local.json"), "{ \"permissions\": {} }");
        File.WriteAllText(Path.Combine(ws, ".mcp.json"), "{}");
        File.WriteAllText(Path.Combine(user, "settings.json"), "{ \"model\": \"x\" }");
        File.WriteAllText(Path.Combine(user, "CLAUDE.md"), "# 全体");
        File.WriteAllText(Path.Combine(user, "commands", "go.md"), "x");

        var def = HarnessDefinitionReader.Read([ws], user);

        Assert.Contains(def.Components, c => c is { Kind: HarnessComponentKind.Settings, Name: "settings.local.json", UserScope: false });
        Assert.Contains(def.Components, c => c is { Kind: HarnessComponentKind.Settings, Name: ".mcp.json", UserScope: false });
        Assert.Contains(def.Components, c => c is { Kind: HarnessComponentKind.Settings, Name: "settings.json", UserScope: true });
        Assert.Contains(def.Components, c => c is { Kind: HarnessComponentKind.Instructions, Name: "CLAUDE.md", UserScope: true });
        Assert.Contains(def.Components, c => c is { Kind: HarnessComponentKind.Command, Name: "/go", UserScope: true });
        Assert.DoesNotContain(def.Components, c => c.Kind == HarnessComponentKind.Hook); // hooks が無ければ Hook 行は出ない
        Assert.DoesNotContain(HarnessDefinitionReader.Read([ws]).Components, c => c.UserScope); // 渡さなければ読まない
    }

    [Fact]
    public void Runs_link_to_tasks_only_by_explicit_reference()
    {
        var task = new HarnessTask("fix-login", "ログインを直す", @"C:\ws\.harness\tasks\fix-login.md");
        HarnessRun With(string prompt) => RunOf("r") with { Prompts = [prompt] };

        Assert.Single(HarnessTaskLinks.For(With("task:fix-login をやって"), [task]));
        Assert.Single(HarnessTaskLinks.For(With(@"C:\ws\.harness\tasks\fix-login.md を読んで"), [task]));
        Assert.Empty(HarnessTaskLinks.For(With("task:fix-login-2 をやって"), [task]));
        Assert.Empty(HarnessTaskLinks.For(With("ログインを直して"), [task]));
    }

    [Fact]
    public void Verdicts_round_trip_and_clearing_keeps_others()
    {
        var path = HarnessVerdictStore.PathFor(Path.Combine(_base, "ws"));
        HarnessVerdictStore.Set(path, "a", new HarnessVerdict(HarnessVerdictKind.Accepted, DateTimeOffset.Now, null));
        HarnessVerdictStore.Set(path, "b", new HarnessVerdict(HarnessVerdictKind.Rejected, DateTimeOffset.Now, "テスト落ち"));
        HarnessVerdictStore.Set(path, "a", null);

        var loaded = HarnessVerdictStore.Load(path);
        Assert.False(loaded.ContainsKey("a"));
        Assert.Equal(HarnessVerdictKind.Rejected, loaded["b"].Verdict);
        Assert.Equal("テスト落ち", loaded["b"].Note);
    }

    // ===== 記録フォルダーの探し方 =====

    [Fact]
    public void Slug_replaces_every_non_alphanumeric_character()
        => Assert.Equal("C--Projects-Loomo", ClaudeTranscriptLocator.Slug(@"C:\Projects\Loomo\"));

    [Fact]
    public void Project_directories_are_confirmed_by_the_recorded_cwd()
    {
        var ws = Path.Combine(_base, "app");
        Directory.CreateDirectory(Path.Combine(ws, "sub"));
        var projects = Path.Combine(_base, "projects");
        var slug = ClaudeTranscriptLocator.Slug(ws);
        void Session(string dir, string cwd)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "s.jsonl"),
                JsonSerializer.Serialize(new { type = "user", cwd, message = new { content = "x" } }) + "\n");
        }
        Session(Path.Combine(projects, slug), ws);
        Session(Path.Combine(projects, slug + "-sub"), Path.Combine(ws, "sub"));
        // 別フォルダー「app-x」（ワークスペースの外）は slug の前方一致でも採らない。
        Session(Path.Combine(projects, slug + "-x"), ws + "-x");

        var found = ClaudeTranscriptLocator.FindProjectDirectories(projects, new FakeWorkspaceService(ws))
            .Select(Path.GetFileName).OrderBy(n => n).ToList();

        Assert.Equal([slug, slug + "-sub"], found);
    }

    [Fact]
    public async Task Analyzer_reads_sessions_of_the_workspace_and_reuses_parsed_results()
    {
        var ws = Path.Combine(_base, "proj");
        Directory.CreateDirectory(ws);
        var projects = Path.Combine(_base, "claude-projects");
        var dir = Path.Combine(projects, ClaudeTranscriptLocator.Slug(ws));
        Directory.CreateDirectory(dir);
        var now = DateTimeOffset.UtcNow.ToString("o");
        File.WriteAllLines(Path.Combine(dir, "s1.jsonl"),
        [
            JsonSerializer.Serialize(new { type = "user", timestamp = now, cwd = ws, message = new { content = "直して" } }),
            Assistant("m1", [ToolUse("t1", "Bash", new { command = "dotnet build" })], at: now),
            Result("t1", new string('x', 25_000), at: now),
        ]);

        var analyzer = new HarnessAnalyzer(new FakeWorkspaceService(ws), projects);
        var snapshot = await analyzer.AnalyzeAsync(TimeSpan.FromDays(30), null, CancellationToken.None);

        var run = Assert.Single(snapshot.Runs);
        Assert.Equal("s1", run.Id);
        Assert.Equal(25_000, Assert.Single(run.ToolCalls).ResultChars);

        var again = await analyzer.AnalyzeAsync(TimeSpan.FromDays(30), null, CancellationToken.None);
        Assert.Same(run, Assert.Single(again.Runs)); // 長さ・更新時刻が同じなら読み直さない
    }

    // ===== レビュー指摘の再発防止 =====

    [Fact]
    public void Task_path_mention_links_only_the_exact_task()
    {
        var fix = new HarnessTask("fix", "直す", @"C:\ws\.harness\tasks\fix.md");
        var fixLogin = new HarnessTask("fix-login", "ログイン", @"C:\ws\.harness\tasks\fix-login.md");
        var run = RunOf("r1") with { Prompts = [@"C:\ws\.harness\tasks\fix-login.md をやって"] };

        Assert.Equal([fixLogin], HarnessTaskLinks.For(run, [fix, fixLogin]));
    }

    [Fact]
    public void Unreadable_verdicts_file_is_not_overwritten_and_unknown_entries_survive()
    {
        var path = Path.Combine(_base, "verdicts.json");
        const string broken = "{ \"a\": { \"verdict\": \"accepted\" }, oops }";
        File.WriteAllText(path, broken);
        Assert.Throws<InvalidDataException>(() =>
            HarnessVerdictStore.Set(path, "b", new HarnessVerdict(HarnessVerdictKind.Accepted, DateTimeOffset.Now, null)));
        Assert.Equal(broken, File.ReadAllText(path));

        // 手で書いた知らない判定・コメント・末尾のカンマは読み飛ばしても、書き換えで消さない。
        File.WriteAllText(path, "{\n  // 手書き\n  \"a\": { \"verdict\": \"pending\", \"note\": \"保留\" },\n}");
        HarnessVerdictStore.Set(path, "b", new HarnessVerdict(HarnessVerdictKind.Rejected, DateTimeOffset.Now, null));

        var text = File.ReadAllText(path);
        Assert.Contains("pending", text);
        Assert.Equal(HarnessVerdictKind.Rejected, Assert.Single(HarnessVerdictStore.Load(path)).Value.Verdict);
    }

    [Fact]
    public async Task Analyzer_drops_sessions_of_another_folder_sharing_the_slug()
    {
        // C:\…\a_b と C:\…\a-b は同じ slug に潰れ、同じ記録フォルダーを使う。
        var ws = Path.Combine(_base, "a-b");
        var other = Path.Combine(_base, "a_b");
        Directory.CreateDirectory(ws);
        var projects = Path.Combine(_base, "claude-projects");
        var dir = Path.Combine(projects, ClaudeTranscriptLocator.Slug(ws));
        Directory.CreateDirectory(dir);
        var now = DateTimeOffset.UtcNow.ToString("o");
        File.WriteAllLines(Path.Combine(dir, "other.jsonl"),
            [JsonSerializer.Serialize(new { type = "user", timestamp = now, cwd = other, message = new { content = "別" } })]);
        File.WriteAllLines(Path.Combine(dir, "mine.jsonl"),
            [JsonSerializer.Serialize(new { type = "user", timestamp = now, cwd = ws, message = new { content = "こっち" } })]);

        var snapshot = await new HarnessAnalyzer(new FakeWorkspaceService(ws), projects)
            .AnalyzeAsync(TimeSpan.FromDays(30), null, CancellationToken.None);

        Assert.Equal("mine", Assert.Single(snapshot.Runs).Id);
    }
    // ===== チケットをタスクとして扱う（§37.3.2） =====

    [Fact]
    public void Ticket_references_are_found_only_when_written_explicitly()
    {
        var refs = HarnessTickets.Find(
            "ab#12 と https://dev.azure.com/org/Proj/_workitems/edit/34 と https://github.com/o/r/issues/5です、"
            + "AB#12 の再掲、素の #99、xAB#7、PR 1234、対応AB#8です");

        Assert.Equal(["AB#12", "AB#34", "o/r#5", "AB#8"], refs.Select(r => r.Key));
        Assert.Equal("https://dev.azure.com/org/Proj/_workitems/edit/34", refs[1].Url);
        Assert.Equal(34, refs[1].WorkItemId);
        Assert.Null(refs[2].WorkItemId);
    }

    [Fact]
    public void Url_is_kept_when_the_same_ticket_is_also_mentioned_by_number()
    {
        var r = Assert.Single(HarnessTickets.Find("AB#34 → https://x.visualstudio.com/P/_workitems/edit/34"));
        Assert.Equal("https://x.visualstudio.com/P/_workitems/edit/34", r.Url);
    }

    [Fact]
    public void Run_mentioning_a_ticket_links_to_the_task_file_that_declares_it_or_to_the_ticket_itself()
    {
        var tasks = Path.Combine(_base, ".harness", "tasks");
        Directory.CreateDirectory(tasks);
        var file = Path.Combine(tasks, "fix-login.md");
        File.WriteAllText(file, "# ログインを直す\n\n- ticket: 1234\n\n## 受け入れ条件\n");
        var declared = HarnessDefinitionReader.ReadTask(file);
        Assert.Equal("AB#1234", declared.Ticket?.Key);

        var run = RunOf("r1") with { Prompts = ["AB#1234 ログインを直す", "ついでに AB#77 も"] };
        var links = HarnessTaskLinks.For(run, [declared]);

        Assert.Equal(2, links.Count);
        Assert.Same(declared, links[0]);
        Assert.True(links[1].IsTicketOnly);
        Assert.Equal("AB#77", links[1].Id);
    }

    [Theory]
    [InlineData("0012", "AB#12")]
    [InlineData("AB#0012", "AB#12")]
    [InlineData("o/r#05", "o/r#5")]
    [InlineData("https://github.com/o/r/issues/5", "o/r#5")]
    public void Declared_ticket_keys_match_what_the_pane_shows(string declared, string key)
        => Assert.Equal(key, HarnessTickets.ParseDeclared(declared)?.Key);

    [Fact]
    public void All_task_files_declaring_the_same_ticket_are_linked()
    {
        var a = new HarnessTask("a", "A", @"C:\ws\.harness\tasks\a.md", new HarnessTicketRef("AB#1", null));
        var b = new HarnessTask("b", "B", @"C:\ws\.harness\tasks\b.md", new HarnessTicketRef("AB#1", null));
        var run = RunOf("r1") with { Prompts = ["AB#1 をやって"] };

        Assert.Equal([a, b], HarnessTaskLinks.For(run, [a, b]));
    }
}
