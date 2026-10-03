using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Input;
using sk0ya.Loomo.App.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ターミナルの「最近のコマンド」（§24.20）——実行1回分の組み立て（E→C→D）、一覧の更新規則
/// （新しい順・重複まとめ・上限・copy-on-write）、パレットでの見せ方、ワークスペースへの永続化の検証。
/// </summary>
public class RecentTerminalCommandsTests
{
    private static readonly DateTime T0 = new(2026, 10, 3, 1, 0, 0, DateTimeKind.Utc);

    private static RecentTerminalCommandRun Run(string command, int? exit = 0, int minutes = 0)
        => new(command, exit, T0.AddMinutes(minutes));

    // ===== 1回分の組み立て =====

    [Fact]
    public void Tracker_reports_run_only_after_command_line_executed_and_done()
    {
        var tracker = new TerminalCommandRunTracker();
        tracker.OnPromptStart();
        tracker.OnCommandLine("dotnet build");
        tracker.OnExecuted();

        var run = tracker.OnDone(1, T0);

        Assert.NotNull(run);
        Assert.Equal("dotnet build", run!.Command);
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(T0, run.FinishedUtc);
    }

    [Fact]
    public void Tracker_ignores_done_without_executed()
    {
        // シェル統合の初期化・プロンプト再描画が出す D（§24.1 の偽バッジと同じ）。
        var tracker = new TerminalCommandRunTracker();
        tracker.OnCommandLine("git status");

        Assert.Null(tracker.OnDone(0, T0));
    }

    [Fact]
    public void Tracker_does_not_reuse_stale_command_line_for_unreported_repeat()
    {
        // 現行の Terminal は直前と同じコマンドの連続実行では E 由来の通知を出さない。
        // 前回の行を使い回すと別の実行を記録しかねないので、その回は記録しない。
        var tracker = new TerminalCommandRunTracker();
        tracker.OnCommandLine("npm test");
        tracker.OnExecuted();
        Assert.NotNull(tracker.OnDone(0, T0));

        tracker.OnPromptStart();
        tracker.OnExecuted();
        Assert.Null(tracker.OnDone(1, T0));
    }

    [Fact]
    public void Tracker_uses_command_line_carried_by_activity_when_available()
    {
        // ShellCommandActivityEventArgs.CommandLine を持つ版のライブラリなら、連続実行も毎回届く。
        var tracker = new TerminalCommandRunTracker();
        tracker.OnExecuted("npm test");

        Assert.Equal("npm test", tracker.OnDone(2, T0, "npm test")!.Command);
    }

    [Fact]
    public void Tracker_ignores_blank_submission()
    {
        var tracker = new TerminalCommandRunTracker();
        tracker.OnCommandLine("   ");
        tracker.OnExecuted();

        Assert.Null(tracker.OnDone(0, T0));
    }

    // ===== 一覧の更新 =====

    [Fact]
    public void Record_puts_newest_first_and_merges_duplicates()
    {
        var list = RecentTerminalCommands.Record(null, Run("git status"))!;
        list = RecentTerminalCommands.Record(list, Run("dotnet build", 1, 1))!;
        list = RecentTerminalCommands.Record(list, Run("git status", 0, 2))!;

        Assert.Equal(new[] { "git status", "dotnet build" }, list.Select(c => c.Command));
        Assert.Equal(2, list[0].RunCount);
        Assert.Equal(T0.AddMinutes(2), list[0].LastRunUtc);
        Assert.Equal(1, list[1].ExitCode);
    }

    [Fact]
    public void Record_replaces_exit_code_with_latest_run()
    {
        var list = RecentTerminalCommands.Record(null, Run("dotnet test", 1))!;
        list = RecentTerminalCommands.Record(list, Run("dotnet test", 0, 1))!;

        Assert.Equal(0, Assert.Single(list).ExitCode);
    }

    [Fact]
    public void Record_trims_and_rejects_empty()
    {
        Assert.Null(RecentTerminalCommands.Record(null, Run("  ")));
        Assert.Equal("ls", RecentTerminalCommands.Record(null, Run("  ls  "))![0].Command);
    }

    [Fact]
    public void Record_caps_count()
    {
        List<RecentTerminalCommandSnapshot>? list = null;
        for (var i = 0; i < RecentTerminalCommands.MaxCount + 10; i++)
            list = RecentTerminalCommands.Record(list, Run($"echo {i}", 0, i));

        Assert.Equal(RecentTerminalCommands.MaxCount, list!.Count);
        Assert.Equal($"echo {RecentTerminalCommands.MaxCount + 9}", list[0].Command);
    }

    [Fact]
    public void Record_is_copy_on_write()
    {
        // 保存の直列化と取り合わないよう、元のリストも要素も書き換えない。
        var original = RecentTerminalCommands.Record(null, Run("git status"))!;
        var first = original[0];

        var updated = RecentTerminalCommands.Record(original, Run("git status", 1, 1))!;

        Assert.NotSame(original, updated);
        Assert.Single(original);
        Assert.Same(first, original[0]);
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(1, first.RunCount);
    }

    [Fact]
    public void RecordAll_applies_runs_in_order_and_returns_null_when_nothing_recorded()
    {
        var list = RecentTerminalCommands.RecordAll(null, new[] { Run("a"), Run("b", 0, 1) })!;
        Assert.Equal(new[] { "b", "a" }, list.Select(c => c.Command));

        Assert.Null(RecentTerminalCommands.RecordAll(list, Array.Empty<RecentTerminalCommandRun>()));
    }

    // ===== 見せ方 =====

    [Fact]
    public void Title_folds_multiline_and_badge_shows_outcome()
    {
        Assert.Equal("cd src ⏎ dotnet build", RecentTerminalCommands.Title("cd src\r\n  dotnet build\n"));
        Assert.True(RecentTerminalCommands.IsMultiline("a\nb"));
        Assert.False(RecentTerminalCommands.IsMultiline("a b"));
        Assert.Equal("✓", RecentTerminalCommands.Badge(0));
        Assert.Equal("✗ 1", RecentTerminalCommands.Badge(1));
        Assert.Equal("", RecentTerminalCommands.Badge(null));
    }

    [Fact]
    public void Detail_says_it_only_sends_and_does_not_run()
    {
        var item = new RecentTerminalCommandSnapshot
        {
            Command = "dotnet build", ExitCode = 1, LastRunUtc = T0, RunCount = 3,
        };

        var detail = RecentTerminalCommands.Detail(item, T0.AddHours(2));

        Assert.Contains("終了コード: 1（失敗）", detail);
        Assert.Contains("2 時間前", detail);
        Assert.Contains("実行回数: 3", detail);
        Assert.Contains("ターミナルへ送る（実行はしない）", detail);

        var multi = RecentTerminalCommands.Detail(
            new RecentTerminalCommandSnapshot { Command = "a\nb", LastRunUtc = T0 }, T0);
        Assert.Contains("コンポーザへ送る", multi);
    }

    [Fact]
    public void Palette_row_shows_badge_instead_of_shortcut_and_preview_uses_detail()
    {
        var command = new PaletteCommand("最近のコマンド", "dotnet build", () => { }, Shortcut: "Ctrl+B")
        {
            Badge = "✗ 1",
            Detail = "説明",
        };
        Assert.Equal("✗ 1", command.RightText);
        Assert.Equal("Ctrl+B", new PaletteCommand("c", "t", () => { }, "Ctrl+B").RightText);

        var preview = PaletteCommandPreview.Create(command);
        Assert.Equal("説明", preview.Message);
        Assert.Equal("dotnet build", preview.Header);
    }

    [Fact]
    public void Palette_filter_finds_recent_command_by_category_or_text()
    {
        var commands = new List<PaletteCommand>
        {
            new("タブ", "新しいターミナルタブ", () => { }),
            new("最近のコマンド", "dotnet build", () => { }),
        };

        Assert.Equal("dotnet build", Assert.Single(PaletteFilter.Filter(commands, "dotnet")).Title);
        Assert.Equal("dotnet build", Assert.Single(PaletteFilter.Filter(commands, "最近")).Title);
    }

    [Fact]
    public void Catalog_has_recent_commands_entry_without_default_key()
    {
        var descriptor = CommandCatalog.Find("terminal.recentCommands");

        Assert.NotNull(descriptor);
        Assert.Null(descriptor!.DefaultBinding);
    }

    // ===== 永続化 =====

    [Fact]
    public void Workspace_snapshot_roundtrips_recent_terminal_commands()
    {
        var dir = Path.Combine(Path.GetTempPath(), "loomo-recent-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var store = new WorkspaceStateStore(Path.Combine(dir, "workspaces.json"));
            var state = new WorkspaceState();
            state.Workspaces.Add(new WorkspaceSnapshot
            {
                RootPath = @"C:\Projects\Loomo",
                RecentTerminalCommands = RecentTerminalCommands.RecordAll(
                    null, new[] { Run("git status"), Run("dotnet build", 1, 1) })!,
            });

            // 定期保存と同じ遅延経路で書き、読み直しの前に積んだ書き出しを片付ける。
            store.SaveDeferred(state);
            var loaded = store.Load();

            var ws = Assert.Single(loaded.Workspaces);
            Assert.Equal(new[] { "dotnet build", "git status" }, ws.RecentTerminalCommands.Select(c => c.Command));
            Assert.Equal(1, ws.RecentTerminalCommands[0].ExitCode);
            Assert.Equal(T0.AddMinutes(1), ws.RecentTerminalCommands[0].LastRunUtc);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
