using System;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services.Lsp;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>動いているもの面（§24.54）——何を拾い何を捨てるか、並び（人の番 → 実行中 → 常駐）、
/// 引き直しで行のインスタンスを使い回すこと。</summary>
public class RunningEntriesTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 3, 0, 0, DateTimeKind.Utc);

    private static RunningTerminalSource Idle(Guid id) => new(id, "pwsh", TerminalTabStatus.Empty, null);

    private static RunningTerminalSource Running(Guid id, string command)
        => new(id, "pwsh", new TerminalTabStatus(RunningCommand: command, RunningSinceUtc: Now.AddMinutes(-2)), null);

    [Fact]
    public void Idle_terminals_and_stopped_servers_are_skipped()
    {
        var entries = RunningEntries.Build(
            [Idle(Guid.NewGuid())], [], [],
            [new LspServerRuntimeStatus("marksman.exe", @"C:\w", LspServerRuntimeState.Stopped, null, 0)],
            aiBusy: false, aiStatus: null);

        Assert.Empty(entries);
    }

    [Fact]
    public void Attention_comes_first_then_running_then_resident()
    {
        var claudeTab = Guid.NewGuid();
        var entries = RunningEntries.Build(
            [Running(Guid.NewGuid(), "npm run dev"),
             new RunningTerminalSource(claudeTab, "claude", TerminalTabStatus.Empty, ClaudeAgentState.NeedsApproval)],
            [new RunningDebugSource("IDE", Guid.NewGuid(), "App", IsBusy: true, IsStopped: false, Status: "実行中")],
            [],
            [new LspServerRuntimeStatus(@"C:\tools\csharp-ls.exe", @"C:\work\app", LspServerRuntimeState.Ready, null, 0)],
            aiBusy: true, aiStatus: "考え中");

        Assert.Equal(
            [RunningEntryKind.Claude, RunningEntryKind.Terminal, RunningEntryKind.Debug, RunningEntryKind.Ai,
             RunningEntryKind.LanguageServer],
            entries.Select(e => e.Kind));
        Assert.Equal(RunningEntries.AttentionGroup, entries[0].Group);
        Assert.Equal(RunningEntryTone.Attention, entries[0].Tone);
        Assert.False(entries[0].CanStop);          // 承認待ちの Claude に Ctrl+C は送らない
        Assert.Equal("npm run dev", entries[1].Title);
        Assert.True(entries[1].CanStop);
        Assert.Equal("csharp-ls", entries[4].Title);
        Assert.Equal("app · 準備完了", entries[4].Detail);
        Assert.Equal(RunningEntries.ResidentGroup, entries[4].Group);
    }

    [Fact]
    public void Stopped_debug_session_is_your_turn()
    {
        var entries = RunningEntries.Build([], [new RunningDebugSource("TS IDE", Guid.NewGuid(), "web", false, true, "")],
            [], [], false, null);

        var entry = Assert.Single(entries);
        Assert.Equal(RunningEntries.AttentionGroup, entry.Group);
        Assert.Contains("停止中", entry.Detail);
    }

    [Fact]
    public void Failed_server_is_marked_failed_with_error_in_tooltip()
    {
        var entries = RunningEntries.Build([], [], [],
            [new LspServerRuntimeStatus("pyright.exe", @"C:\w", LspServerRuntimeState.Failed, "not found", 0)],
            false, null);

        var entry = Assert.Single(entries);
        Assert.Equal(RunningEntryTone.Failed, entry.Tone);
        Assert.Contains("not found", entry.ToolTip);
    }

    [Fact]
    public void View_model_reuses_rows_by_key_and_counts()
    {
        var vm = new RunningViewModel();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        vm.Apply(RunningEntries.Build([Running(a, "make"), Running(b, "test")], [], [], [], false, null), Now);
        var rowA = vm.Items.Single(i => i.Key == $"terminal:{a}");
        Assert.Equal(2, vm.RunningCount);
        Assert.Equal("2分00秒", rowA.Elapsed);

        vm.Apply(RunningEntries.Build([Running(a, "make all")], [], [], [], false, null), Now.AddSeconds(5));
        Assert.Same(rowA, Assert.Single(vm.Items));
        Assert.Equal("make all", rowA.Title);
        Assert.Equal("2分05秒", rowA.Elapsed);

        vm.Apply([], Now);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public void Stop_command_is_ignored_for_rows_that_cannot_stop()
    {
        var vm = new RunningViewModel();
        vm.Apply(RunningEntries.Build([], [], [], [], aiBusy: true, aiStatus: null), Now);
        var stopped = false;
        vm.StopRequested += (_, _) => stopped = true;

        vm.StopCommand.Execute(vm.Items[0]);

        Assert.False(stopped);
    }
}
