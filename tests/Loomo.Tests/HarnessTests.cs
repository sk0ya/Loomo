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
    public void Broken_lines_are_skipped()
    {
        var run = Parse(User("やって"), "{\"type\":\"assistant\",", "not json");
        Assert.Single(run.Prompts);
    }

    // ===== 規則（§37.5） =====

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

    [Fact]
    public void Reread_needs_three_reads_without_a_write_in_between()
    {
        const string a = @"C:\ws\a.cs";
        var twiceThenEdit = RunOf("r1", Call("Read", a), Call("Read", a), Call("Edit", a), Call("Read", a), Call("Bash", command: "dotnet test"));
        Assert.DoesNotContain(HarnessDetectors.Detect([twiceThenEdit]), c => c.RuleId == HarnessDetectors.RuleReread);

        var thrice = RunOf("r2", Call("Read", a, chars: 400), Call("Read", a, chars: 400), Call("Read", a, chars: 400));
        var candidate = Assert.Single(HarnessDetectors.Detect([thrice]), c => c.RuleId == HarnessDetectors.RuleReread);
        // 根拠は2回目以降の読込だけ（1回目は必要な読込）。
        Assert.Equal(2, candidate.Evidence.Count);
        Assert.Equal(800 / HarnessDetectors.CharsPerToken, candidate.EstimatedTokens);
    }

    [Fact]
    public void Reading_different_ranges_of_one_file_is_not_a_reread()
    {
        const string a = @"C:\ws\big.cs";
        var run = RunOf("r1", Call("Read", a, offset: 1), Call("Read", a, offset: 400), Call("Read", a, offset: 800));
        Assert.DoesNotContain(HarnessDetectors.Detect([run]), c => c.RuleId == HarnessDetectors.RuleReread);
    }

    [Fact]
    public void Large_build_output_points_to_output_filtering()
    {
        var run = RunOf("r1", Call("Bash", command: "cd C:/ws && dotnet build -v n", chars: 50_000));
        var candidate = Assert.Single(HarnessDetectors.Detect([run]), c => c.RuleId == HarnessDetectors.RuleLargeOutput);
        Assert.Equal("cmd:dotnet build", candidate.Key);
        Assert.Contains(HarnessComponentKind.Hook, candidate.RelatedKinds);
        Assert.Equal(50_000 / HarnessDetectors.CharsPerToken, candidate.EstimatedTokens);
    }

    [Fact]
    public void Candidates_aggregate_across_runs_and_sort_by_frequency()
    {
        var big = Call("Bash", command: "git log", chars: 30_000);
        var once = RunOf("r1", Call("Read", @"C:\ws\huge.json", chars: 900_000));
        var runs = new[] { RunOf("r2", big), RunOf("r3", big), once };

        var candidates = HarnessDetectors.Detect(runs).Where(c => c.RuleId == HarnessDetectors.RuleLargeOutput).ToList();
        // トークンは huge.json が大きいが、頻度（2 Run）が先。
        Assert.Equal("cmd:git log", candidates[0].Key);
        Assert.Equal(2, candidates[0].RunCount);
    }

    [Fact]
    public void Same_failing_command_twice_is_a_retry_candidate()
    {
        var run = RunOf("r1",
            Call("Bash", command: "dotnet  build", error: true),
            Call("Edit", @"C:\ws\a.cs"),
            Call("Bash", command: "dotnet build", error: true),
            Call("Bash", command: "dotnet build"));
        var candidate = Assert.Single(HarnessDetectors.Detect([run]), c => c.RuleId == HarnessDetectors.RuleRetry);
        Assert.Equal(2, candidate.Evidence.Count);
    }

    [Fact]
    public void Edit_that_cannot_find_its_target_is_reported()
    {
        var run = RunOf("r1", Call("Edit", @"C:\ws\a.cs", error: true, errorHead: "<tool_use_error>String to replace not found in file."));
        Assert.Single(HarnessDetectors.Detect([run]), c => c.RuleId == HarnessDetectors.RuleEditMismatch);
    }

    [Fact]
    public void Source_change_without_a_later_build_or_test_is_unverified()
    {
        var verified = RunOf("r1", Call("Edit", @"C:\ws\a.cs"), Call("Bash", command: "npm run test"));
        var docsOnly = RunOf("r2", Call("Write", @"C:\ws\README.md"));
        var testedBefore = RunOf("r3", Call("Bash", command: "dotnet test"), Call("Edit", @"C:\ws\a.cs"));

        var candidates = HarnessDetectors.Detect([verified, docsOnly, testedBefore]);
        var unverified = Assert.Single(candidates, c => c.RuleId == HarnessDetectors.RuleUnverified);
        Assert.Equal("r3", Assert.Single(unverified.Evidence).RunId);
        Assert.Equal(HarnessCandidateCategory.Quality, unverified.Category);
    }

    [Theory]
    [InlineData("dotnet build sk0ya.Loomo.sln", "dotnet build")]
    [InlineData("cd C:/Projects/Loomo && git log --oneline", "git log")]
    [InlineData("cat docs/a.md | head", "cat")]
    [InlineData("\"C:\\Program Files\\Git\\bin\\git.exe\" status", "git.exe status")]
    public void Command_key_is_the_first_two_words_without_cd(string command, string expected)
        => Assert.Equal(expected, HarnessDetectors.CommandKey(command));

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
            r => r.Id == "old" ? v1 : v2, []);

        Assert.Equal(["bbb", "aaa"], rows.Select(r => r.Version!.Commit));
        Assert.Equal(2, rows[0].Summary.RunCount);
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

    [Fact]
    public void Task_file_separates_observation_from_guess_and_never_overwrites()
    {
        var ws = Path.Combine(_base, "ws");
        var run = RunOf("r1", Call("Bash", command: "dotnet build", chars: 50_000));
        var candidate = HarnessDetectors.Detect([run]).First(c => c.RuleId == HarnessDetectors.RuleLargeOutput);
        var now = DateTimeOffset.Parse("2026-10-11T03:00:00Z");

        var first = HarnessTaskWriter.Write(ws, candidate, now, p => p);
        var second = HarnessTaskWriter.Write(ws, candidate, now, p => p);

        Assert.NotEqual(first, second);
        var text = File.ReadAllText(first);
        Assert.Contains("## 観測事実", text);
        Assert.Contains("## 改善の方向（推測）", text);
        Assert.Contains("推定", text);
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
        Assert.Contains(snapshot.Candidates, c => c.RuleId == HarnessDetectors.RuleLargeOutput);

        var again = await analyzer.AnalyzeAsync(TimeSpan.FromDays(30), null, CancellationToken.None);
        Assert.Same(run, Assert.Single(again.Runs)); // 長さ・更新時刻が同じなら読み直さない
    }

    // ===== 点検（§37.10） =====

    private static HarnessCheckItem CheckOf(IReadOnlyList<HarnessCheckItem> items, string id) => Assert.Single(items, i => i.Id == id);

    [Fact]
    public void Check_on_empty_folder_reports_everything_missing_and_creates_nothing()
    {
        var items = HarnessStructureCheck.Inspect(_base, [], [], 0);

        Assert.All(items, i => Assert.Equal(HarnessCheckStatus.Missing, i.Status));
        Assert.Equal(".harness/ が無い。", CheckOf(items, HarnessStructureCheck.ItemManifest).Finding);
        // 点検するだけでリポジトリにフォルダーを生やさない（§37.3）。
        Assert.False(Directory.Exists(Path.Combine(_base, ".harness")));
        // 判定は Run に付けるもので、雛形は無い。
        Assert.Empty(CheckOf(items, HarnessStructureCheck.ItemVerdicts).Scaffold);
    }

    [Fact]
    public void Scaffold_uses_observed_successful_build_command_and_never_overwrites()
    {
        var run = RunOf("r1",
            Call("Edit", @"C:\ws\a.cs"),
            Call("Bash", command: "cd C:/ws && dotnet build x.sln 2>&1 | tail -5"),
            Call("Bash", command: "dotnet test", error: true));
        var candidates = HarnessDetectors.Detect([run]);
        var workflows = CheckOf(HarnessStructureCheck.Inspect(_base, [run], candidates, 1), HarnessStructureCheck.ItemWorkflows);

        var content = Assert.Single(workflows.Scaffold).Content;
        Assert.Contains("`dotnet build x.sln`（観測: 1 Run で成功）", content);
        Assert.DoesNotContain("dotnet test", content); // 失敗したコマンドは「検証のコマンド」にしない

        var path = Path.Combine(_base, ".harness", "workflows", "verify-before-done.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "人間が書いた");
        Assert.Empty(HarnessStructureCheck.WriteScaffold(_base, workflows));
        Assert.Equal("人間が書いた", File.ReadAllText(path));
    }

    [Fact]
    public void Written_scaffold_makes_the_item_present_and_manifest_is_readable()
    {
        var folder = Path.Combine(_base, "My App");
        Directory.CreateDirectory(folder);
        var manifest = CheckOf(HarnessStructureCheck.Inspect(folder, [], [], 0), HarnessStructureCheck.ItemManifest);
        var written = Assert.Single(HarnessStructureCheck.WriteScaffold(folder, manifest));

        Assert.Equal(("my-app", "0.1.0"), HarnessDefinitionReader.ReadManifest(written));
        var again = CheckOf(HarnessStructureCheck.Inspect(folder, [], [], 0), HarnessStructureCheck.ItemManifest);
        Assert.Equal(HarnessCheckStatus.Present, again.Status);
    }

    [Fact]
    public void Tasks_without_acceptance_section_are_incomplete()
    {
        var tasks = Path.Combine(_base, ".harness", "tasks");
        Directory.CreateDirectory(tasks);
        File.WriteAllText(Path.Combine(tasks, "good.md"), "# 良い\n\n## 受け入れ条件\n\n- [ ] 通る\n");
        File.WriteAllText(Path.Combine(tasks, "bad.md"), "# 悪い\n\n## 目的\n");
        var linked = RunOf("r1") with { Prompts = ["task:good をやって"] };

        var item = CheckOf(HarnessStructureCheck.Inspect(_base, [linked, RunOf("r2")], [], 1), HarnessStructureCheck.ItemTasks);

        Assert.Equal(HarnessCheckStatus.Incomplete, item.Status);
        Assert.Contains("bad", item.Finding);
        Assert.DoesNotContain("good", item.Finding);
        Assert.Equal("期間内の Run 2 件のうち、タスクに結び付いたのは 1 件。", item.Evidence);
    }

    [Fact]
    public void Rules_evidence_and_scaffold_come_from_repeated_failures_of_this_folder_only()
    {
        var fail = Call("Bash", command: "npm run lint", error: true, errorHead: "boom");
        var mine = RunOf("r1", fail, fail);
        var other = RunOf("r9", Call("Bash", command: "cargo fmt", error: true), Call("Bash", command: "cargo fmt", error: true));
        var candidates = HarnessDetectors.Detect([mine, other]);

        var rules = CheckOf(HarnessStructureCheck.Inspect(_base, [mine], candidates, 1), HarnessStructureCheck.ItemRules);

        Assert.Equal("同じコマンドの失敗反復 1 種（1 Run）（改善候補）。", rules.Evidence);
        var content = Assert.Single(rules.Scaffold).Content;
        Assert.Contains("npm run", content);
        Assert.DoesNotContain("cargo", content); // 別フォルダーの Run の候補は混ぜない
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
    public void Check_counts_only_runs_of_its_own_folder()
    {
        var edit = Call("Edit", @"C:\ws\a.cs");
        var mine = RunOf("r1", edit);
        var others = Enumerable.Range(2, 4).Select(i => RunOf($"r{i}", edit)).ToList();
        var candidates = HarnessDetectors.Detect([mine, .. others]);
        Assert.Equal(5, Assert.Single(candidates, c => c.RuleId == HarnessDetectors.RuleUnverified).RunCount);

        var workflows = CheckOf(HarnessStructureCheck.Inspect(_base, [mine], candidates, 0), HarnessStructureCheck.ItemWorkflows);

        Assert.Equal("検証なしの変更が 1 Run で起きている（改善候補）。", workflows.Evidence);
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

    [Fact]
    public void Ticket_linked_runs_satisfy_the_tasks_check_without_task_files()
    {
        Directory.CreateDirectory(Path.Combine(_base, ".harness"));
        var linked = RunOf("r1") with { Prompts = ["AB#1234 をやって"] };

        var item = CheckOf(HarnessStructureCheck.Inspect(_base, [linked, RunOf("r2")], [], 1), HarnessStructureCheck.ItemTasks);

        Assert.Equal(HarnessCheckStatus.Present, item.Status);
        Assert.Empty(item.Scaffold);
        Assert.Contains("AB#1234", item.Evidence);
    }
    [Fact]
    public void Scaffold_comment_is_not_read_as_a_ticket_declaration()
    {
        var file = Path.Combine(_base, "t.md");
        // 雛形から ticket: の行だけ消したもの。コメントの説明に「ticket:」と AB#1234 が残る。
        var template = HarnessStructureCheck.Inspect(_base, [], [], 0)
            .Single(i => i.Id == HarnessStructureCheck.ItemTasks).Scaffold.Single().Content;
        File.WriteAllText(file, string.Join("\n", template.Split('\n').Where(l => l.Trim() != "ticket:")));

        Assert.Null(HarnessDefinitionReader.ReadTicket(file));
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
