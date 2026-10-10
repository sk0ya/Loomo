using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using sk0ya.Loomo.App.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

/// <summary>§24.26 ターミナルの出力・選択を出典付きのペグボード素材にする。</summary>
public sealed class TerminalPegboardMaterialTests
{
    private static readonly DateTime T0 = new(2026, 10, 10, 3, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TabA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TabB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static TerminalCommandOutput Output(string command, string text, int? exit = 0, int minutes = 0,
                                                Guid? tab = null, string? cwd = @"C:\work\app", bool headOmitted = false)
        => new(command, text, exit, T0.AddMinutes(minutes), headOmitted, tab ?? TabA, cwd);

    // ===== コマンド1回分の出力 =====

    [Fact]
    public void Output_becomes_a_card_titled_by_its_command_with_full_provenance()
    {
        var item = TerminalPegboardMaterial.FromOutput(
            Output("dotnet test", "\r\nFailed!  - Failed: 1, Passed: 3\r\n", exit: 1, minutes: 5), "pwsh");

        Assert.NotNull(item);
        Assert.Equal("Failed!  - Failed: 1, Passed: 3", item!.Content);
        Assert.Equal("dotnet test の出力", item.Title);
        Assert.Equal(TabA, item.Source.TabId);
        Assert.Equal("pwsh", item.Source.TabTitle);
        Assert.Equal("dotnet test", item.Source.Command);
        Assert.Equal(@"C:\work\app", item.Source.WorkingDirectory);
        Assert.Equal(1, item.Source.ExitCode);
        Assert.Equal(T0.AddMinutes(5), item.Source.CapturedUtc);
        Assert.False(item.Source.HeadOmitted);
    }

    [Fact]
    public void Empty_output_is_not_a_card()
        => Assert.Null(TerminalPegboardMaterial.FromOutput(Output("cls", " \r\n "), "pwsh"));

    [Fact]
    public void Oversized_output_keeps_the_tail_from_a_line_start()
    {
        var lines = Enumerable.Range(0, 5000).Select(i => $"line {i:D5} ........").ToList();
        var text = string.Join("\n", lines);

        var item = TerminalPegboardMaterial.FromOutput(Output("dotnet build", text), "pwsh")!;

        Assert.True(item.Content.Length <= TerminalPegboardMaterial.MaxChars);
        Assert.True(item.Source.HeadOmitted);
        Assert.StartsWith("line ", item.Content);              // 行の途中から始めない
        Assert.EndsWith(lines[^1], item.Content);              // 末尾（集計・エラー）は残る
    }

    [Fact]
    public void Head_lost_in_the_scrollback_is_carried_over()
    {
        var item = TerminalPegboardMaterial.FromOutput(Output("git log", "abc", headOmitted: true), null)!;
        Assert.True(item.Source.HeadOmitted);
        Assert.Null(item.Source.TabTitle);
    }

    // ===== 選択 =====

    [Fact]
    public void Selection_inside_an_output_is_attributed_to_the_newest_run_of_that_tab()
    {
        var outputs = new List<TerminalCommandOutput>
        {
            Output("dotnet test", "  Passed A\n  Failed B   \n  Passed C", exit: 1, minutes: 1),
            Output("dotnet test", "  Passed A\n  Failed B\n  Passed C", exit: 0, minutes: 3),
            Output("dotnet test", "  Passed A\n  Failed B\n  Passed C", exit: 2, minutes: 9, tab: TabB),   // 別のタブ
            Output("git status", "nothing to commit", minutes: 5),
        };

        // ターミナルの選択は行末の埋め草・改行の形が切り出しと違う
        var found = TerminalPegboardMaterial.FindContaining(outputs, TabA, "Failed B  \r\n  Passed C");

        Assert.NotNull(found);
        Assert.Equal(T0.AddMinutes(3), found!.FinishedUtc);
        Assert.Equal(0, found.ExitCode);
    }

    [Fact]
    public void Selection_not_in_any_output_of_the_tab_has_no_command()
    {
        var outputs = new[] { Output("dotnet test", "Passed A\nFailed B"), Output("ls", "Failed B", tab: TabB) };

        Assert.Null(TerminalPegboardMaterial.FindContaining(outputs, TabA, "PS C:\\work> dotnet test"));
        // 各行は出力にあっても、並びが違えば同じ出力の一節ではない
        Assert.Null(TerminalPegboardMaterial.FindContaining(outputs, TabA, "Failed B\nPassed A"));
        Assert.Null(TerminalPegboardMaterial.FindContaining(outputs, TabA, "  \n "));
    }

    [Fact]
    public void Selection_with_a_known_command_takes_its_provenance()
    {
        var run = Output("npm run lint", "src/a.ts: error", exit: 1, minutes: 2, cwd: @"C:\web");

        var item = TerminalPegboardMaterial.FromSelection(
            "  src/a.ts: error  ", TabA, "pwsh", @"C:\elsewhere", run, T0.AddMinutes(30))!;

        Assert.Equal("src/a.ts: error", item.Content);
        Assert.Null(item.Title);                                  // 見出しは引用の先頭行（ブラウザの抜き書きと同じ）
        Assert.Equal("npm run lint", item.Source.Command);
        Assert.Equal(@"C:\web", item.Source.WorkingDirectory);   // 実行した場所（いまのタブの場所ではなく）
        Assert.Equal(1, item.Source.ExitCode);
        Assert.Equal(T0.AddMinutes(2), item.Source.CapturedUtc);
    }

    [Fact]
    public void Selection_without_a_command_keeps_the_tab_place_and_time()
    {
        var item = TerminalPegboardMaterial.FromSelection("hello", TabA, " ", @"C:\work", null, T0)!;

        Assert.Null(item.Source.Command);
        Assert.Null(item.Source.ExitCode);
        Assert.Null(item.Source.TabTitle);
        Assert.Equal(@"C:\work", item.Source.WorkingDirectory);
        Assert.Equal(T0, item.Source.CapturedUtc);
        Assert.Null(TerminalPegboardMaterial.FromSelection("  ", TabA, "pwsh", null, null, T0));
    }

    // ===== 見え方・戻り方 =====

    [Fact]
    public void Label_and_tooltip_describe_where_it_came_from()
    {
        var source = new PegboardTerminalSource
        {
            TabId = TabA, TabTitle = "pwsh", Command = "dotnet test", WorkingDirectory = @"C:\work\app",
            ExitCode = 1, CapturedUtc = T0, HeadOmitted = true,
        };

        Assert.Equal("dotnet test ✗ 1 · ターミナル「pwsh」", TerminalPegboardMaterial.Label(source));
        var tip = TerminalPegboardMaterial.ToolTip(source);
        Assert.Contains("コマンド: dotnet test", tip);
        Assert.Contains(@"場所: C:\work\app", tip);
        Assert.Contains("終了コード: 1", tip);
        Assert.Contains("先頭を省略", tip);

        Assert.Equal("ターミナル「Terminal」", TerminalPegboardMaterial.Label(new PegboardTerminalSource { TabId = TabA }));
    }

    [Theory]
    [InlineData("\n\n   first line  \nsecond", "first line")]
    [InlineData("  ", null)]
    public void Anchor_is_the_head_of_the_first_non_blank_line(string content, string? expected)
        => Assert.Equal(expected, TerminalPegboardMaterial.Anchor(content));

    [Fact]
    public void Anchor_is_short_enough_to_fit_in_one_terminal_row()
    {
        var anchor = TerminalPegboardMaterial.Anchor(new string('x', 200))!;
        Assert.Equal(TerminalPegboardMaterial.AnchorChars, anchor.Length);
    }

    [Fact]
    public void Document_name_matches_the_command_output_naming()
    {
        var name = TerminalPegboardMaterial.DocumentName(
            new PegboardTerminalSource { Command = "dotnet test --no-build", CapturedUtc = T0 });
        Assert.StartsWith("出力-dotnet-", name);
        Assert.EndsWith(".log", name);
        Assert.StartsWith("出力-terminal-", TerminalPegboardMaterial.DocumentName(new PegboardTerminalSource()));
    }

    // ===== ペグボードのカード =====

    private static PegboardTerminalSource Source(string? command = "dotnet test", int? exit = 1)
        => new() { TabId = TabA, TabTitle = "pwsh", Command = command, WorkingDirectory = @"C:\work\app", ExitCode = exit, CapturedUtc = T0 };

    [Fact]
    public void Card_shows_the_terminal_source_below_the_quote()
    {
        var vm = new PegboardViewModel();
        vm.AddContent("Failed B", type: "text", terminalSource: Source());

        var card = Assert.Single(vm.Items);
        Assert.True(card.HasSource);
        Assert.True(card.HasTerminalSource);
        Assert.False(card.HasWebSource);
        Assert.Null(card.SourceLink);                                  // ブラウザで開く URL は持たない
        Assert.Equal("出典: dotnet test ✗ 1 · ターミナル「pwsh」", card.Subtitle);
        Assert.Contains(@"C:\work\app", card.SourceToolTip);
        Assert.Contains("ターミナル", card.SourceOpenToolTip);
    }

    [Fact]
    public void Terminal_source_is_kept_only_for_text_and_a_page_source_wins()
    {
        var vm = new PegboardViewModel();
        vm.AddContent("https://example.com/", type: "url", terminalSource: Source());
        Assert.Null(vm.Items[0].Snapshot.TerminalSource);

        vm.AddContent("一節", type: "text", sourceUrl: "https://example.com/a", terminalSource: Source());
        Assert.Null(vm.Items[0].Snapshot.TerminalSource);
        Assert.True(vm.Items[0].HasWebSource);
    }

    [Fact]
    public void Terminal_source_is_searchable_by_command_and_place()
    {
        var vm = new PegboardViewModel();
        vm.AddContent("メモ");
        vm.AddContent("Failed B", type: "text", terminalSource: Source());

        vm.Filter = "dotnet";
        Assert.Equal("Failed B", Assert.Single(vm.ItemsView.Cast<PegboardItemVm>()).Content);
        vm.Filter = @"work\app";
        Assert.Single(vm.ItemsView.Cast<PegboardItemVm>());
    }

    [Fact]
    public void Open_source_is_requested_for_terminal_cards_too()
    {
        var vm = new PegboardViewModel();
        vm.AddContent("メモ");
        vm.AddContent("Failed B", type: "text", terminalSource: Source());
        var requested = new List<PegboardItemVm>();
        vm.OpenSourceRequested += (_, item) => requested.Add(item);

        foreach (var item in vm.Items.ToList())
            vm.OpenSourceCommand.Execute(item);

        Assert.Equal("Failed B", Assert.Single(requested).Content);
    }

    [Fact]
    public void Preview_of_a_long_output_passes_only_the_first_lines()
    {
        var text = string.Join("\n", Enumerable.Range(0, 300).Select(i => $"row {i}"));
        var vm = new PegboardViewModel();
        vm.AddContent(text, type: "text", title: "dotnet test の出力", terminalSource: Source());

        var preview = vm.Items[0].Preview;
        Assert.Equal(PegboardItemVm.PreviewMaxLines, preview.Split('\n').Length);
        Assert.StartsWith("row 0", preview);
    }

    // ===== 永続化 =====

    [Fact]
    public void Terminal_source_survives_save_and_restore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "loomo-peg-term-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var store = new WorkspaceStateStore(Path.Combine(dir, "workspaces.json"));
            var vm = new PegboardViewModel();
            var source = Source();
            source.HeadOmitted = true;
            vm.AddContent("Failed B", type: "text", title: "dotnet test の出力", terminalSource: source);
            var state = new WorkspaceState();
            state.Workspaces.Add(new WorkspaceSnapshot { RootPath = @"C:\Projects\Loomo", Pegboard = vm.ToSnapshots() });

            store.SaveDeferred(state);
            var loaded = store.Load();

            var restored = new PegboardViewModel();
            restored.LoadItems(Assert.Single(loaded.Workspaces).Pegboard);
            var card = Assert.Single(restored.Items);
            var back = card.Snapshot.TerminalSource!;
            Assert.Equal(TabA, back.TabId);
            Assert.Equal("pwsh", back.TabTitle);
            Assert.Equal("dotnet test", back.Command);
            Assert.Equal(@"C:\work\app", back.WorkingDirectory);
            Assert.Equal(1, back.ExitCode);
            Assert.Equal(T0, back.CapturedUtc);
            Assert.True(back.HeadOmitted);
            Assert.Equal("出典: dotnet test ✗ 1 · ターミナル「pwsh」", card.Subtitle);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Old_pegboard_json_without_terminal_source_still_reads()
    {
        const string json = """
            { "workspaces": [ { "rootPath": "C:\\x", "pegboard": [
                { "type": "text", "content": "memo" },
                { "type": "text", "content": "quote", "sourceUrl": "https://example.com/", "sourceTitle": "E" } ] } ] }
            """;

        var state = JsonSerializer.Deserialize(json, WorkspaceStateJsonContext.Default.WorkspaceState)!;

        var items = state.Workspaces[0].Pegboard;
        Assert.All(items, i => Assert.Null(i.TerminalSource));
        Assert.Equal("https://example.com/", items[1].SourceUrl);
    }

    // ===== 入口 =====

    [Theory]
    [InlineData("terminal.pinOutput")]
    [InlineData("terminal.pinSelection")]
    public void Catalog_has_pin_entries_without_default_key(string id)
    {
        var descriptor = CommandCatalog.Find(id);
        Assert.NotNull(descriptor);
        Assert.Null(descriptor!.DefaultBinding);
    }
}
