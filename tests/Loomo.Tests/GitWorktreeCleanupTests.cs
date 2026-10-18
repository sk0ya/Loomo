using System;
using System.IO;
using System.Linq;
using System.Threading;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>ワークツリーの片付け（§24.55）——候補にする理由、最後に触った時刻、大きさの見せ方、選択と空く容量。</summary>
public class GitWorktreeCleanupTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 3, 0, 0, DateTimeKind.Utc);

    private static GitWorktreeInfo Linked(string name = "topic", bool locked = false, bool current = false, int? changes = 0)
        => new()
        {
            Path = Path.Combine(Path.GetTempPath(), "wt-" + name),
            Head = "0123456789abcdef",
            Branch = name,
            IsLocked = locked,
            IsCurrent = current,
            ChangeCount = changes,
        };

    private static GitWorktreeUsage Usage(GitWorktreeInfo w, long size = 1024, int daysAgo = 1, bool? merged = false)
        => new(w.Path, size, Now.AddDays(-daysAgo), merged, "main");

    [Fact]
    public void Merged_and_stale_are_reasons()
    {
        var w = Linked();
        Assert.Equal(GitWorktreeCleanupReason.Merged, GitWorktreeCleanup.Reasons(w, Usage(w, merged: true), null, Now));
        Assert.Equal(GitWorktreeCleanupReason.Stale, GitWorktreeCleanup.Reasons(w, Usage(w, daysAgo: 30), null, Now));
        Assert.Equal(GitWorktreeCleanupReason.None, GitWorktreeCleanup.Reasons(w, Usage(w), null, Now));
    }

    [Fact]
    public void Opening_the_room_recently_keeps_an_old_branch_off_the_list()
    {
        var w = Linked();
        // 最後のコミットは 30 日前でも、部屋を昨日開いていれば「触っている」。
        Assert.Equal(GitWorktreeCleanupReason.None,
            GitWorktreeCleanup.Reasons(w, Usage(w, daysAgo: 30), Now.AddDays(-1), Now));
        Assert.Equal(Now.AddDays(-1), GitWorktreeCleanup.LastActivity(Usage(w, daysAgo: 30), Now.AddDays(-1)));
    }

    [Fact]
    public void Locked_and_current_are_never_candidates()
    {
        var locked = Linked(locked: true);
        var current = Linked(current: true);
        Assert.Equal(GitWorktreeCleanupReason.None, GitWorktreeCleanup.Reasons(locked, Usage(locked, merged: true), null, Now));
        Assert.Equal("ロック中", GitWorktreeCleanup.BlockedReason(locked));
        Assert.False(GitWorktreeCleanup.CanClean(current));
    }

    [Theory]
    [InlineData(500L, "500 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(340L * 1024 * 1024, "340 MB")]
    [InlineData(1288490189L, "1.2 GB")]
    public void FormatSize(long bytes, string expected) => Assert.Equal(expected, GitWorktreeCleanup.FormatSize(bytes));

    [Fact]
    public void ParseMergedBranches_strips_markers()
        => Assert.Equal(["main", "a", "b"], GitWorktreeCleanup.ParseMergedBranches("* main\n+ a\n  b\n\n").ToList());

    [Fact]
    public void MeasureDirectory_sums_nested_files()
    {
        var temp = Directory.CreateTempSubdirectory("loomo-measure-");
        try
        {
            File.WriteAllBytes(Path.Combine(temp.FullName, "a.bin"), new byte[100]);
            Directory.CreateDirectory(Path.Combine(temp.FullName, "sub", "deep"));
            File.WriteAllBytes(Path.Combine(temp.FullName, "sub", "deep", "b.bin"), new byte[250]);
            Assert.Equal(350, GitWorktreeCleanup.MeasureDirectory(temp.FullName, CancellationToken.None));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void View_model_preselects_clean_candidates_and_skips_dirty_unless_discarding()
    {
        var mergedClean = Linked("a");
        var mergedDirty = Linked("b", changes: 3);
        var fresh = Linked("c");
        var rows = new[] { mergedClean, mergedDirty, fresh }
            .Select(w => new WorktreeCleanupRowViewModel(w, null, Now)).ToList();
        var vm = new WorktreeCleanupViewModel(rows);
        rows[0].ApplyUsage(Usage(mergedClean, size: 1024 * 1024, merged: true));
        rows[1].ApplyUsage(Usage(mergedDirty, merged: true));
        rows[2].ApplyUsage(Usage(fresh));

        vm.PreselectCandidates();

        Assert.True(rows[0].IsChecked);
        Assert.False(rows[1].IsChecked);          // 未コミットがあるものは印を付けない
        Assert.False(rows[2].IsChecked);          // 候補でない
        Assert.Equal([mergedClean], vm.ToRemove.Select(r => r.Worktree));
        Assert.Contains("1.0 MB", vm.Summary);

        rows[1].IsChecked = true;
        Assert.Single(vm.ToRemove);               // 選んでも、変更ごと消す印が無ければ飛ばす
        vm.DiscardChanges = true;
        Assert.Equal(2, vm.ToRemove.Count);
    }

    [Fact]
    public void Candidates_only_filter_hides_fresh_rows()
    {
        var fresh = Linked("c");
        var row = new WorktreeCleanupRowViewModel(fresh, null, Now);
        var vm = new WorktreeCleanupViewModel([row]);
        row.ApplyUsage(Usage(fresh));

        Assert.False(vm.View.Cast<object>().Any());
        vm.CandidatesOnly = false;
        Assert.Single(vm.View.Cast<object>());
    }
}
