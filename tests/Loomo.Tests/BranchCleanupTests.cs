using System;
using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>§24.69 ブランチの片付け——候補の理由・消せないもの・最初の印・消す範囲。</summary>
public sealed class BranchCleanupTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 3, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlySet<string> NoWorktrees = new HashSet<string>();

    private static GitBranchInfo Branch(string name, int daysAgo = 1, bool gone = false, bool current = false,
                                        bool remote = false, string? upstream = null)
        => new(name, current, remote, upstream ?? (gone ? $"origin/{name}" : null))
        {
            UpstreamGone = gone,
            LastCommit = new DateTimeOffset(Now.AddDays(-daysAgo)),
        };

    [Fact]
    public void Reasons_cover_merged_gone_and_stale()
    {
        Assert.Equal(GitBranchCleanupReason.Merged, GitBranchCleanup.Reasons(Branch("a"), isMerged: true, Now));
        Assert.Equal(GitBranchCleanupReason.UpstreamGone, GitBranchCleanup.Reasons(Branch("b", gone: true), false, Now));
        Assert.Equal(GitBranchCleanupReason.Stale, GitBranchCleanup.Reasons(Branch("c", daysAgo: 45), false, Now));
        Assert.Equal(GitBranchCleanupReason.None, GitBranchCleanup.Reasons(Branch("d", daysAgo: 10), false, Now));
    }

    [Fact]
    public void Reason_text_says_why()
    {
        var reasons = GitBranchCleanupReason.Merged | GitBranchCleanupReason.UpstreamGone;
        Assert.Equal("origin/main に取り込み済み・上流 origin/x が消えた",
            GitBranchCleanup.ReasonText(reasons, "origin/main", "origin/x"));
        Assert.Contains("git branch --merged origin/main", GitBranchCleanup.ReasonDetail(reasons, "origin/main", "origin/x"));
    }

    [Fact]
    public void Current_default_worktree_and_remote_branches_cannot_be_cleaned()
    {
        var worktrees = new HashSet<string> { "feature" };
        Assert.Equal("いまのブランチ", GitBranchCleanup.BlockedReason(Branch("x", current: true), "main", worktrees));
        Assert.Equal("既定ブランチ", GitBranchCleanup.BlockedReason(Branch("main"), "main", worktrees));
        Assert.Equal("ワークツリーで使用中", GitBranchCleanup.BlockedReason(Branch("feature"), "main", worktrees));
        Assert.Equal("リモートブランチ", GitBranchCleanup.BlockedReason(Branch("origin/y", remote: true), "main", worktrees));
        Assert.Null(GitBranchCleanup.BlockedReason(Branch("done"), "main", worktrees));
        Assert.Equal("main", GitBranchCleanup.LocalName("origin/main"));
    }

    [Fact]
    public void Blocked_rows_are_never_candidates()
    {
        var row = new BranchCleanupRowViewModel(Branch("main", daysAgo: 90), "既定ブランチ", false, Now);
        row.ApplyMerged(true, "main");
        Assert.False(row.IsCandidate);
        Assert.Equal("既定ブランチ", row.ReasonText);
    }

    [Fact]
    public void Preselect_skips_branches_with_an_open_pull_request()
    {
        var withPr = new BranchCleanupRowViewModel(Branch("pr", gone: true), null, hasOpenPullRequest: true, Now);
        var done = new BranchCleanupRowViewModel(Branch("done"), null, false, Now);
        var fresh = new BranchCleanupRowViewModel(Branch("fresh"), null, false, Now);
        var model = new BranchCleanupViewModel([withPr, done, fresh]);
        done.ApplyMerged(true, "main");

        model.PreselectCandidates();

        Assert.False(withPr.IsChecked);
        Assert.True(done.IsChecked);
        Assert.False(fresh.IsChecked);   // 理由が無い
    }

    [Fact]
    public void Unmerged_branches_are_removed_only_when_forced()
    {
        var gone = new BranchCleanupRowViewModel(Branch("gone", gone: true), null, false, Now);
        var done = new BranchCleanupRowViewModel(Branch("done"), null, false, Now);
        done.ApplyMerged(true, "main");
        var model = new BranchCleanupViewModel([gone, done]);
        model.PreselectCandidates();

        Assert.Equal(new[] { "done" }, model.ToRemove.Select(r => r.Name));
        Assert.Contains("取り込まれていない 1 本は残します", model.Summary);

        model.ForceUnmerged = true;
        Assert.Equal(new[] { "gone", "done" }, model.ToRemove.Select(r => r.Name));
        Assert.True(model.CanRemove);
    }
}
