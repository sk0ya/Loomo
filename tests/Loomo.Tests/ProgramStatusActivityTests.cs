using System;
using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using Terminal.Tabs;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>プログラムが自分で言う状態（OSC 7501）を部屋へ（§24.61）。Claude Code の申告はタイトルの印より優先し、
/// Claude 以外の「人の番」は動いているもの面と袖へ。</summary>
public sealed class ProgramStatusActivityTests
{
    private static readonly Guid Tab = Guid.NewGuid();
    private static readonly DateTimeOffset T = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    private static ProgramStatusRecord Claude(ProgramStatusState state, ProgramStatusKind kind = ProgramStatusKind.None, string id = "")
        => new(id, state, kind, null, "claude-code", null, null, T);

    private static ProgramStatusRecord Program(string app, ProgramStatusState state,
        ProgramStatusKind kind = ProgramStatusKind.None, int? progress = null, string? title = null, string? msg = null)
        => new("", state, kind, progress, app, title, msg, T);

    [Fact]
    public void Claude_working_then_idle_ends_a_turn_like_the_title_does()
    {
        var sut = new PaneActivityTracker();
        sut.TerminalProgramStatusChanged(Tab, [Claude(ProgramStatusState.Working)], paneWatched: false);
        Assert.Equal(PaneActivityKind.AgentWorking, sut.AggregateTerminal(out _));

        var ended = sut.TerminalProgramStatusChanged(Tab, [Claude(ProgramStatusState.Idle)], paneWatched: false);
        Assert.True(ended.Claude.TurnEnded);
        Assert.Equal(PaneActivityKind.AgentWaiting, sut.AggregateTerminal(out _));
    }

    [Fact]
    public void Claude_blocked_on_permission_is_waiting_for_approval_without_hooks()
    {
        var sut = new PaneActivityTracker();
        sut.TerminalProgramStatusChanged(Tab, [Claude(ProgramStatusState.Working)], paneWatched: false);
        sut.TerminalProgramStatusChanged(Tab, [Claude(ProgramStatusState.Blocked, ProgramStatusKind.Permission)], paneWatched: false);

        Assert.Equal(ClaudeAgentState.NeedsApproval, sut.AgentTabs().Single().State);
        Assert.Equal(PaneActivityKind.AgentApproval, sut.AggregateTerminal(out _));

        // 承認したら作業に戻る——申告が正確なので印も解く
        sut.TerminalProgramStatusChanged(Tab, [Claude(ProgramStatusState.Working)], paneWatched: false);
        Assert.Equal(ClaudeAgentState.Working, sut.AgentTabs().Single().State);
    }

    [Fact]
    public void While_reporting_the_title_does_not_move_the_state()
    {
        var sut = new PaneActivityTracker();
        sut.TerminalProgramStatusChanged(Tab, [Claude(ProgramStatusState.Blocked, ProgramStatusKind.Permission)], paneWatched: false);

        // 承認ダイアログ中もタイトルは ✳（入力待ちと区別できない）——申告がある間はタイトルで書き換えない
        var t = sut.TerminalTitleChanged(Tab, "✳ Fix tests", paneWatched: false);
        Assert.False(t.ShouldRefresh);
        Assert.Equal(ClaudeAgentState.NeedsApproval, sut.AgentTabs().Single().State);
        Assert.Equal("Fix tests", sut.AgentTabs().Single().Title);   // 題はタイトルから拾う
    }

    [Fact]
    public void While_reporting_a_title_without_a_mark_is_still_the_subject()
    {
        // 申告できる端末では Claude Code はタイトルに印を付けない（2.1.310 を同梱の ConPTY 越しに採取・2026-10-25）
        var sut = new PaneActivityTracker();
        sut.TerminalCommandChanged(Tab, commandExecuted: true, commandDone: false, exitCode: null, paneWatched: true);
        sut.TerminalProgramStatusChanged(Tab, [Claude(ProgramStatusState.Working)], paneWatched: true);

        sut.TerminalTitleChanged(Tab, "Reply with OK", paneWatched: true);
        Assert.Equal("Reply with OK", sut.AgentTabs().Single().Title);
    }

    [Fact]
    public void The_header_gets_the_mark_back_from_the_report()
    {
        var sut = new PaneActivityTracker();
        Assert.Null(sut.TerminalAgentHeaderStatus(Tab));   // 申告していなければ Claude が自分で印を付けている

        sut.TerminalProgramStatusChanged(Tab, [Claude(ProgramStatusState.Working)], paneWatched: true);
        Assert.Equal("◐ Reply with OK", ClaudeCodeTitle.WithMark("Reply with OK", sut.TerminalAgentHeaderStatus(Tab)!.Value));

        sut.TerminalProgramStatusChanged(Tab, [Claude(ProgramStatusState.Done)], paneWatched: true);
        Assert.Equal("✳ Reply with OK", ClaudeCodeTitle.WithMark("Reply with OK", sut.TerminalAgentHeaderStatus(Tab)!.Value));

        // 印が既にあれば二重に付けない
        Assert.Equal("◑ Fix", ClaudeCodeTitle.WithMark("◑ Fix", ClaudeCodeStatus.Working));

        sut.TerminalProgramStatusChanged(Tab, [], paneWatched: true);
        Assert.Null(sut.TerminalAgentHeaderStatus(Tab));
    }

    [Fact]
    public void When_the_report_disappears_claude_has_left()
    {
        var sut = new PaneActivityTracker();
        sut.TerminalProgramStatusChanged(Tab, [Claude(ProgramStatusState.Idle)], paneWatched: true);
        Assert.Single(sut.AgentTabs());

        var gone = sut.TerminalProgramStatusChanged(Tab, [], paneWatched: true);
        Assert.True(gone.Claude.ShouldRefresh);
        Assert.Empty(sut.AgentTabs());
        Assert.False(sut.IsTerminalBusy(Tab));
    }

    [Fact]
    public void Done_ends_the_turn_like_idle()   // Claude Code 2.1.310 はターンの終わりに done を送る（実測）
    {
        var sut = new PaneActivityTracker();
        sut.TerminalProgramStatusChanged(Tab, [Claude(ProgramStatusState.Working)], paneWatched: false);
        var ended = sut.TerminalProgramStatusChanged(Tab, [Claude(ProgramStatusState.Done)], paneWatched: false);
        Assert.True(ended.Claude.TurnEnded);
        Assert.Equal(ClaudeAgentState.TurnEnded, sut.AgentTabs().Single().State);
    }

    [Fact]
    public void A_done_left_behind_by_a_crashed_claude_is_ignored_after_the_command_ends()
    {
        var sut = new PaneActivityTracker();
        var stale = new ProgramStatusRecord("", ProgramStatusState.Done, ProgramStatusKind.None, null, "claude-code", null, null,
            DateTimeOffset.Now.AddMinutes(-1));
        sut.TerminalCommandChanged(Tab, commandExecuted: true, commandDone: false, null, paneWatched: true);  // claude を起動（C）
        sut.TerminalProgramStatusChanged(Tab, [stale], paneWatched: true);
        sut.TerminalCommandChanged(Tab, commandExecuted: false, commandDone: true, 1, paneWatched: true);   // claude が落ちてプロンプトへ（D）

        sut.TerminalProgramStatusChanged(Tab, [stale], paneWatched: true);   // done は端末に残っている
        Assert.Empty(sut.AgentTabs());
    }

    [Fact]
    public void A_blocked_subagent_makes_the_whole_session_wait()
    {
        var status = ProgramStatusPresentation.Claude([
            Claude(ProgramStatusState.Working),
            Claude(ProgramStatusState.Blocked, ProgramStatusKind.Question, id: "agent-1"),
        ]);
        Assert.True(status!.Value.Blocked);
        Assert.Equal(ProgramStatusKind.Question, status.Value.Kind);
    }

    [Fact]
    public void Another_program_waiting_unwatched_starts_a_human_turn_once()
    {
        var sut = new PaneActivityTracker();
        var first = sut.TerminalProgramStatusChanged(Tab,
            [Program("terraform", ProgramStatusState.Blocked, ProgramStatusKind.Permission)], paneWatched: false);
        Assert.True(first.HumanTurnStarted);
        Assert.True(first.ProgramsChanged);

        var again = sut.TerminalProgramStatusChanged(Tab,
            [Program("terraform", ProgramStatusState.Blocked, ProgramStatusKind.Permission)], paneWatched: false);
        Assert.False(again.HumanTurnStarted);
        Assert.False(again.ProgramsChanged);
        Assert.Single(sut.ProgramsOf(Tab));
        Assert.Empty(sut.AgentTabs());   // Claude ではない
    }

    [Fact]
    public void Watching_the_pane_does_not_leave_a_mark()
    {
        var sut = new PaneActivityTracker();
        var t = sut.TerminalProgramStatusChanged(Tab,
            [Program("brew", ProgramStatusState.Blocked, ProgramStatusKind.Auth)], paneWatched: true);
        Assert.False(t.HumanTurnStarted);
    }

    [Fact]
    public void Running_panel_lists_waiting_programs_first_and_working_ones_with_progress()
    {
        var waiting = Program("terraform", ProgramStatusState.Blocked, ProgramStatusKind.Permission, title: "Apply", msg: "3 to add");
        var working = Program("cargo", ProgramStatusState.Working, progress: 42);
        var entries = RunningEntries.Build(
            [new RunningTerminalSource(Tab, "pwsh", TerminalTabStatus.Empty, null, [working, waiting])],
            [], [], [], aiBusy: false, aiStatus: null);

        Assert.Equal(2, entries.Count);
        Assert.Equal(RunningEntries.AttentionGroup, entries[0].Group);
        Assert.Equal("Apply", entries[0].Title);
        Assert.Equal("● 許可を待っています · 3 to add · pwsh", entries[0].Detail);
        Assert.Equal(RunningEntryTone.Attention, entries[0].Tone);
        Assert.Equal("cargo", entries[1].Title);
        Assert.StartsWith("作業中 42%", entries[1].Detail);
    }
}
