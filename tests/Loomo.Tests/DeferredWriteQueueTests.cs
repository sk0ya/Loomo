using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 書き出しを UI スレッドから外す土台（§31.15）。積んだ順に実行されること、追い越された計画が捨てられること、
/// そして <c>Flush</c> を通せば「書いた直後に読み直せる」という約束が保たれることを固定する。
/// </summary>
public sealed class DeferredWriteQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"loomo-dwq-{Guid.NewGuid():N}");

    public DeferredWriteQueueTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Path_(string name) => System.IO.Path.Combine(_dir, name);

    [Fact]
    public void Flush_waits_for_everything_that_was_queued()
    {
        using var queue = new DeferredWriteQueue(nameof(Flush_waits_for_everything_that_was_queued));
        var path = Path_("a.txt");

        for (var i = 0; i < 20; i++)
        {
            var text = i.ToString();
            queue.Enqueue(new FileWritePlan().WriteAllText(path, text));
        }
        queue.Flush();

        Assert.Equal("19", File.ReadAllText(path));
    }

    [Fact]
    public void Work_runs_in_the_order_it_was_queued()
    {
        using var queue = new DeferredWriteQueue(nameof(Work_runs_in_the_order_it_was_queued));
        var order = new List<int>();

        for (var i = 0; i < 50; i++)
        {
            var n = i;
            queue.Enqueue(() => { lock (order) order.Add(n); });
        }
        queue.Flush();

        Assert.Equal(Enumerable.Range(0, 50), order);
    }

    [Fact]
    public void A_plan_that_rewrites_everything_an_older_one_touched_replaces_it()
    {
        var older = new FileWritePlan().WriteAllText(Path_("a.txt"), "a");
        var newer = new FileWritePlan()
            .WriteAllText(Path_("a.txt"), "a2")
            .WriteAllText(Path_("b.txt"), "b");

        Assert.True(newer.Covers(older));
        Assert.False(older.Covers(newer));   // b.txt を書かない計画は b.txt の計画を代われない
    }

    [Fact]
    public void A_plan_touching_a_different_file_never_replaces_another()
    {
        var a = new FileWritePlan().WriteAllText(Path_("a.txt"), "a");
        var b = new FileWritePlan().WriteAllText(Path_("b.txt"), "b");

        Assert.False(a.Covers(b));
        Assert.False(b.Covers(a));
    }

    [Fact]
    public void Dropping_a_covered_plan_still_leaves_every_file_written()
    {
        using var queue = new DeferredWriteQueue(nameof(Dropping_a_covered_plan_still_leaves_every_file_written));

        queue.Enqueue(new FileWritePlan().WriteAllText(Path_("a.txt"), "old"));
        queue.Enqueue(new FileWritePlan()
            .WriteAllText(Path_("a.txt"), "new")
            .WriteAllText(Path_("b.txt"), "b"));
        queue.Flush();

        Assert.Equal("new", File.ReadAllText(Path_("a.txt")));
        Assert.Equal("b", File.ReadAllText(Path_("b.txt")));
    }

    [Fact]
    public void One_failing_operation_does_not_stop_the_rest_of_the_plan()
    {
        using var queue = new DeferredWriteQueue(nameof(One_failing_operation_does_not_stop_the_rest_of_the_plan));

        queue.Enqueue(new FileWritePlan()
            .WriteAllText(System.IO.Path.Combine(_dir, "missing-folder", "x.txt"), "x")   // 失敗する
            .WriteAllText(Path_("after.txt"), "after"));
        queue.Flush();

        Assert.Equal("after", File.ReadAllText(Path_("after.txt")));
    }

    [Fact]
    public void Work_queued_after_disposal_is_still_carried_out()
    {
        var queue = new DeferredWriteQueue(nameof(Work_queued_after_disposal_is_still_carried_out));
        queue.Dispose();

        queue.Enqueue(new FileWritePlan().WriteAllText(Path_("late.txt"), "late"));

        Assert.Equal("late", File.ReadAllText(Path_("late.txt")));
    }

    [Fact]
    public void Pruning_removes_only_the_files_that_are_no_longer_wanted()
    {
        using var queue = new DeferredWriteQueue(nameof(Pruning_removes_only_the_files_that_are_no_longer_wanted));
        var drafts = System.IO.Path.Combine(_dir, "drafts");
        Directory.CreateDirectory(drafts);
        File.WriteAllText(System.IO.Path.Combine(drafts, "keep.txt"), "keep");
        File.WriteAllText(System.IO.Path.Combine(drafts, "stale.txt"), "stale");
        File.WriteAllText(System.IO.Path.Combine(drafts, "other.md"), "other");

        queue.Enqueue(new FileWritePlan().PruneDirectory(drafts, "*.txt", ["keep.txt"]));
        queue.Flush();

        Assert.True(File.Exists(System.IO.Path.Combine(drafts, "keep.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(drafts, "stale.txt")));
        Assert.True(File.Exists(System.IO.Path.Combine(drafts, "other.md")));   // 対象の拡張子だけ
    }
}
