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
    public void Done_or_error_only_means_claude_is_not_here()
        => Assert.Null(ProgramStatusPresentation.Claude([Claude(ProgramStatusState.Done)]));

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
