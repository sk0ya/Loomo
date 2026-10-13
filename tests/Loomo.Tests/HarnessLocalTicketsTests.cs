using System.IO;
using System.Linq;
using System.Text.Json;
using sk0ya.Loomo.Core.Harness;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// チケット型タスク（§37.11）：<c>.harness/</c> を持たず <c>tickets/&lt;群&gt;/&lt;ID&gt;/&lt;ID&gt;-detail.md</c> で仕事を回す
/// ワークスペースの読み方。入力は実在のチケット群の構成だけを縮めたダミー（中身は架空）。
/// </summary>
public sealed class HarnessLocalTicketsTests : IDisposable
{
    private const string Group = "sample-import";
    private readonly string _ws = Path.Combine(Path.GetTempPath(), $"loomo-tickets-{Guid.NewGuid():N}");

    public HarnessLocalTicketsTests()
    {
        Directory.CreateDirectory(_ws);
        File.WriteAllText(Path.Combine(_ws, "AGENTS.md"), "# 指示");
        File.WriteAllText(Path.Combine(_ws, "CLAUDE.md"), "@AGENTS.md");
        Write(Path.Combine("doc", "tickets-format", "contents", "ワークフロー仕様.md"), "# ワークフロー仕様");

        WriteGroup(Path.Combine("tickets", Group),
            "ID\tStatus\tType\tTitle\tPriority\tSummary\tDependIDs\tLastUpdated\n"
            + "VCI-001\tworking\tfeature\t取り込みの土台\tHigh\t概要\t\t2026-10-01\n"
            + "VCI-002\tnot-started\ttest\t取り込みの試験\tMid\t概要\tVCI-001\t2026-10-02\n",
            ("VCI-001", "# VCI-001 見出しの題\n\n## 目的\n\n- 架空\n\n## 要件・受け入れ条件\n\n- [x] 一つ目\n- [ ] 二つ目\n- [ ] 三つ目\n\n## タスク\n\n- [ ] 対象外の項目\n"),
            ("VCI-002", "# VCI-002 見出しだけの題\n\n## 目的\n\n- 架空\n"));
        WriteGroup(Path.Combine("meta", "workspace-upkeep"),
            "ID\tStatus\tType\tTitle\tPriority\tSummary\tDependIDs\tLastUpdated\n"
            + "MTA-001\tdone\tdoc\t作業場の手入れ\tLow\t概要\t\t2026-09-01\n",
            ("MTA-001", "# MTA-001\n\n## 要件・受け入れ条件\n\n- [x] 済み\n"));
        WriteGroup(Path.Combine("archive", "old-theme", "old-group"),
            "ID\tStatus\tType\tTitle\tPriority\tSummary\tDependIDs\tLastUpdated\n"
            + "OLD-001\tdone\tfeature\t昔の仕事\tLow\t概要\t\t2026-01-01\n"
            + "OLD-002\tdone\tfeature\t誰も言及しない仕事\tLow\t概要\t\t2026-01-01\n",
            ("OLD-001", "# OLD-001\n\n## 要件・受け入れ条件\n\n- [x] 済み\n"),
            ("OLD-002", "# OLD-002\n\n## 要件・受け入れ条件\n\n- [x] 済み\n"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch (IOException) { }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_ws, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteGroup(string groupRelative, string table, params (string Id, string Detail)[] tickets)
    {
        Write(Path.Combine(groupRelative, "tickets.tsv"), table);
        foreach (var (id, detail) in tickets)
        {
            Write(Path.Combine(groupRelative, id, $"{id}-detail.md"), detail);
            Write(Path.Combine(groupRelative, id, $"{id}-discussion.md"), "# 議論\n\n## [AIレビュー]\n");
        }
    }

    private static HarnessRun RunWith(string id, params string[] prompts) => new()
    {
        Id = id, TranscriptPath = $@"C:\t\{id}.jsonl", Title = id,
        StartedAt = DateTimeOffset.Parse("2026-10-11T00:00:00Z"),
        Prompts = prompts,
    };

    private static string[] Ids(IEnumerable<HarnessTask> tasks) => tasks.Select(t => t.Id).ToArray();

    // ===== 列挙 =====

    [Fact]
    public void Tickets_and_meta_are_tasks_with_table_values_and_archive_is_not_listed()
    {
        var def = HarnessDefinitionReader.Read([_ws]);

        Assert.Equal(["MTA-001", "VCI-001", "VCI-002"], Ids(def.Tasks).Order().ToArray());
        Assert.False(def.HasHarnessFolder); // .harness/ を作らないし、無いまま読める

        var first = def.Tasks.Single(t => t.Id == "VCI-001");
        Assert.Equal("取り込みの土台", first.Title); // tickets.tsv の Title が見出しより優先
        Assert.Equal(HarnessTicketOrigin.Tickets, first.LocalTicket!.Origin);
        Assert.Equal((Group, "working", "feature"), (first.LocalTicket.Group, first.LocalTicket.Status, first.LocalTicket.Type));
        Assert.Equal((3, 1), (first.LocalTicket.AcceptanceTotal!.Value, first.LocalTicket.AcceptanceDone)); // 「タスク」節の項目は数えない

        var second = def.Tasks.Single(t => t.Id == "VCI-002");
        Assert.Equal(["VCI-001"], second.LocalTicket!.DependIds);
        Assert.False(second.LocalTicket.HasAcceptanceSection);

        Assert.Equal(HarnessTicketOrigin.Meta, def.Tasks.Single(t => t.Id == "MTA-001").LocalTicket!.Origin);
        Assert.Contains(def.Components, c => c.Kind == HarnessComponentKind.Workflow && c.Name == "ワークフロー仕様");
    }

    [Fact]
    public void Title_falls_back_to_the_first_heading_when_the_table_has_no_row()
    {
        Write(Path.Combine("tickets", Group, "VCI-003", "VCI-003-detail.md"), "# 表に無いチケット\n");

        var task = HarnessDefinitionReader.Read([_ws]).Tasks.Single(t => t.Id == "VCI-003");

        Assert.Equal("表に無いチケット", task.Title);
        Assert.Null(task.LocalTicket!.Status);
    }

    [Fact]
    public void Archive_is_read_only_for_ids_that_runs_mention()
    {
        var def = HarnessDefinitionReader.Read([_ws]);
        var runs = new[] { RunWith("r1", "OLD-001 の続き"), RunWith("r2", "VCI-001 をやって") };

        var withArchive = HarnessLocalTickets.WithArchived(def, [_ws], runs);

        var archived = Assert.Single(withArchive.Tasks, t => t.LocalTicket?.Origin == HarnessTicketOrigin.Archive);
        Assert.Equal(("OLD-001", "昔の仕事", "done"), (archived.Id, archived.Title, archived.LocalTicket!.Status));
        Assert.Same(def, HarnessLocalTickets.WithArchived(def, [_ws], [RunWith("r3", "VCI-002")])); // 現役で足りれば archive を探さない
        Assert.Single(HarnessTaskLinks.For(runs[0], withArchive.Tasks));
    }

    // ===== 結び付け =====

    [Fact]
    public void Bare_id_links_only_when_it_is_a_known_ticket()
    {
        var tasks = HarnessDefinitionReader.Read([_ws]).Tasks;

        Assert.Equal(["VCI-001"], Ids(HarnessTaskLinks.For(RunWith("r", "VCI-001をやって"), tasks)));
        Assert.Empty(HarnessTaskLinks.For(RunWith("r", "ABC-123 のような語と VCI-999"), tasks)); // ID の形でも定義に無い
        Assert.Empty(HarnessTaskLinks.For(RunWith("r", "XVCI-001 や VCI-0012 や vci-001"), tasks)); // 別の語の一部・大小違い
    }

    [Fact]
    public void Path_mentions_link_to_the_ticket_of_that_group()
    {
        Write(Path.Combine("tickets", "other-group", "VCI-001", "VCI-001-detail.md"), "# 別の群の同じ ID\n");
        var tasks = HarnessDefinitionReader.Read([_ws]).Tasks;

        var byPath = HarnessTaskLinks.For(RunWith("r", $@"tickets\{Group}\VCI-001\VCI-001-detail.md を読んで"), tasks);
        var link = Assert.Single(byPath);
        Assert.Equal(Group, link.LocalTicket!.Group);

        Assert.Equal(["MTA-001"], Ids(HarnessTaskLinks.For(RunWith("r", "meta/workspace-upkeep/MTA-001/ を見て"), tasks)));
        Assert.Equal(2, HarnessTaskLinks.For(RunWith("r", "VCI-001 をやって"), tasks).Count); // 群の分からない素の ID は両方に
    }

    [Fact]
    public void Several_ids_in_one_run_link_to_all_of_them()
    {
        var tasks = HarnessDefinitionReader.Read([_ws]).Tasks;

        var links = HarnessTaskLinks.For(RunWith("r", "VCI-001 と VCI-002、ついでに MTA-001"), tasks);

        Assert.Equal(["MTA-001", "VCI-001", "VCI-002"], Ids(links).Order().ToArray());
    }

    [Fact]
    public void Slash_command_arguments_link_the_run()
    {
        var command = JsonSerializer.Serialize(new
        {
            type = "user", timestamp = "2026-10-11T01:00:00Z", cwd = @"C:\ws",
            message = new { role = "user", content = "<command-message>ticket-run</command-message>\n<command-name>/ticket-run</command-name>\n<command-args>VCI-002</command-args>" },
        });
        var run = ClaudeTranscriptParser.Parse("run-1", @"C:\t\run-1.jsonl", [command]);

        Assert.Empty(run.Prompts); // 指示（題）にはしない
        Assert.Equal(new HarnessCommandInvocation("/ticket-run", "VCI-002"), Assert.Single(run.Commands));
        Assert.Equal(["VCI-002"], Ids(HarnessTaskLinks.For(run, HarnessDefinitionReader.Read([_ws]).Tasks)));
    }

    // ===== 作業の跡（指示に ID が無いとき） =====

    private static RunToolCall Tool(string tool, string file, string? oldText = null, string? newText = null, bool error = false) => new()
    {
        Index = 0, LineNumber = 1, TranscriptPath = @"C:\t\r.jsonl", Tool = tool, Summary = file,
        FilePath = file, EditOld = oldText, EditNew = newText, HasResult = true, IsError = error,
    };

    private HarnessRun Working(params RunToolCall[] calls) => RunWith("r", "続きをお願い") with { ToolCalls = calls };

    private string TicketFile(string id, string name = "detail") => Path.Combine(_ws, "tickets", Group, id, $"{id}-{name}.md");

    [Fact]
    public void Editing_a_ticket_file_links_the_run_as_a_trace_even_without_the_id_in_the_prompt()
    {
        var tasks = HarnessDefinitionReader.Read([_ws]).Tasks;
        var run = Working(
            Tool("Read", TicketFile("VCI-002")), // 読んだだけ（依存先の確認など）は数えない
            Tool("Edit", TicketFile("VCI-001")),
            Tool("Write", TicketFile("VCI-001", "discussion")));

        var link = Assert.Single(HarnessTaskLinks.Links(run, tasks));

        Assert.Equal(("VCI-001", HarnessTaskLinkBasis.Observed), (link.Task.Id, link.Basis));
        Assert.Equal("VCI-001-detail.md ほか1件を編集 2回", link.Evidence);
    }

    [Fact]
    public void Status_change_in_the_table_is_a_trace_but_adding_a_row_is_not()
    {
        var tasks = HarnessDefinitionReader.Read([_ws]).Tasks;
        var table = Path.Combine(_ws, "tickets", Group, "tickets.tsv");
        var run = Working(
            Tool("Edit", table, "VCI-001\tworking\tfeature\t取り込みの土台", "VCI-001\tdone\tfeature\t取り込みの土台"),
            Tool("Edit", table, "VCI-002\tnot-started\ttest\tx", "VCI-002\tnot-started\ttest\tx\nVCI-003\tnot-started\ttest\ty"));

        var link = Assert.Single(HarnessTaskLinks.Links(run, tasks));

        Assert.Equal("VCI-001", link.Task.Id);
        Assert.Equal("状態 working→done", link.Evidence);
    }

    [Fact]
    public void Traces_never_link_failed_edits_or_unknown_tickets_and_never_override_the_prompt()
    {
        var tasks = HarnessDefinitionReader.Read([_ws]).Tasks;

        Assert.Empty(HarnessTaskLinks.Links(Working(Tool("Edit", TicketFile("VCI-001"), error: true)), tasks));
        Assert.Empty(HarnessTaskLinks.Links(Working(Tool("Write", TicketFile("ABC-123"))), tasks)); // 定義に無い ID

        var both = RunWith("r", "VCI-001 をやって") with { ToolCalls = [Tool("Edit", TicketFile("VCI-001"))] };
        Assert.Equal(HarnessTaskLinkBasis.Declared, Assert.Single(HarnessTaskLinks.Links(both, tasks)).Basis);
    }

    [Fact]
    public void Archived_ticket_is_loaded_when_a_run_edits_it()
    {
        var def = HarnessDefinitionReader.Read([_ws]);
        var run = Working(Tool("Edit", Path.Combine(_ws, "archive", "old-theme", "old-group", "OLD-001", "OLD-001-detail.md")));

        var withArchive = HarnessLocalTickets.WithArchived(def, [_ws], [run]);

        Assert.Equal(["OLD-001"], Ids(HarnessTaskLinks.For(run, withArchive.Tasks)));
    }

    [Fact]
    public void Parser_keeps_edit_strings_for_reading_table_changes()
    {
        var assistant = JsonSerializer.Serialize(new
        {
            type = "assistant", timestamp = "2026-10-11T01:00:01Z",
            message = new
            {
                id = "m1", role = "assistant",
                content = new object[] { new { type = "tool_use", id = "t1", name = "Edit",
                    input = new { file_path = @"C:\ws\tickets\g\tickets.tsv", old_string = "A\tworking", new_string = "A\tdone" } } },
            },
        });

        var call = Assert.Single(ClaudeTranscriptParser.Parse("run-1", @"C:\t\run-1.jsonl", [assistant]).ToolCalls);

        Assert.Equal(("A\tworking", "A\tdone"), (call.EditOld, call.EditNew));
    }

    // ===== 点検 =====

    [Fact]
    public void Check_does_not_report_tasks_or_workflows_missing_for_a_ticket_workspace()
    {
        File.AppendAllText(Path.Combine(_ws, "tickets", Group, "VCI-002", "VCI-002-detail.md"), "\n## 要件・受け入れ条件\n\n- [ ] 足した\n");
        var runs = new[] { RunWith("r1", "VCI-001 をやって"), RunWith("r2", "雑談") };

        var items = HarnessStructureCheck.Inspect(_ws, runs, [], 1);

        var tasks = items.Single(i => i.Id == HarnessStructureCheck.ItemTasks);
        Assert.Equal(HarnessCheckStatus.Present, tasks.Status);
        Assert.Contains("チケット型タスク 3 件", tasks.Finding);
        Assert.Contains("結び付いたのは 1 件", tasks.Evidence);
        Assert.Equal(HarnessCheckStatus.Present, items.Single(i => i.Id == HarnessStructureCheck.ItemWorkflows).Status);
        Assert.Equal(HarnessCheckStatus.Missing, items.Single(i => i.Id == HarnessStructureCheck.ItemManifest).Status); // 必須の欠けのまま
        Assert.False(Directory.Exists(Path.Combine(_ws, HarnessDefinitionReader.HarnessFolderName))); // 点検だけでは作らない
    }

    [Fact]
    public void Ticket_without_the_requirements_section_is_incomplete()
    {
        var item = HarnessStructureCheck.Inspect(_ws, [], [], 0).Single(i => i.Id == HarnessStructureCheck.ItemTasks);

        Assert.Equal(HarnessCheckStatus.Incomplete, item.Status);
        Assert.Contains("VCI-002", item.Finding);
        Assert.DoesNotContain("VCI-001", item.Finding);
        Assert.Contains("要件・受け入れ条件", item.Finding);
    }
}
