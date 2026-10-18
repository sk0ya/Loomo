using System.Collections.Generic;
using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>ワークツリー一覧の「そこで Claude Code が動いている」印（§24.17.2）。</summary>
public class WorktreeAgentMarksTests
{
    private const string Main = @"C:\work\app";
    private static readonly string Nested = Path.Combine(Main, ".claude", "worktrees", "fix-login");
    private const string Sibling = @"C:\work\app.worktrees\feature";

    private static readonly IReadOnlyList<GitWorktreeInfo> Worktrees =
    [
        new() { Path = Main, Branch = "main", IsMain = true },
        new() { Path = Nested, Branch = "fix-login" },
        new() { Path = Sibling, Branch = "feature" },
    ];

    private static string TranscriptIn(string folder)
        => Path.Combine(@"C:\Users\me\.claude\projects", ClaudeTranscriptLocator.Slug(folder), "abc.jsonl");

    [Fact]
    public void Transcript_folder_wins_over_the_shell_cwd()
    {
        // claude --worktree：シェルは本体に居たまま、Claude はワークツリーで作業している。
        var marks = WorktreeAgentMarks.For(Worktrees,
            [new WorktreeAgentSighting(ClaudeAgentState.Working, TranscriptIn(Nested), Main)]);

        Assert.Equal("◐ 作業中", marks[Nested]);
        Assert.False(marks.ContainsKey(Main));
    }

    [Fact]
    public void Without_a_transcript_the_deepest_worktree_containing_the_cwd_is_used()
    {
        var marks = WorktreeAgentMarks.For(Worktrees,
            [new WorktreeAgentSighting(ClaudeAgentState.TurnEnded, null, Path.Combine(Nested, "src"))]);

        Assert.Equal("✳ 入力待ち", marks[Nested]);
        Assert.Single(marks);
    }

    [Fact]
    public void Prefix_without_separator_is_not_inside()
    {
        // C:\work\app.worktrees\… は C:\work\app の中ではない。
        var marks = WorktreeAgentMarks.For(Worktrees,
            [new WorktreeAgentSighting(ClaudeAgentState.Working, null, Sibling)]);

        Assert.Equal("◐ 作業中", marks[Sibling]);
        Assert.False(marks.ContainsKey(Main));
    }

    [Fact]
    public void Several_agents_in_one_worktree_show_the_humans_turn_first()
    {
        var marks = WorktreeAgentMarks.For(Worktrees,
        [
            new WorktreeAgentSighting(ClaudeAgentState.Working, null, Main),
            new WorktreeAgentSighting(ClaudeAgentState.NeedsApproval, null, Main),
        ]);

        Assert.Equal("● 承認待ち", marks[Main]);
    }

    [Fact]
    public void Agents_outside_every_worktree_leave_no_mark()
        => Assert.Empty(WorktreeAgentMarks.For(Worktrees,
            [new WorktreeAgentSighting(ClaudeAgentState.Working, null, @"D:\other")]));
}
