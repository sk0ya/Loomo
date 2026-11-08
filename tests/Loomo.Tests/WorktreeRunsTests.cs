using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>§24.104 ワークツリーの行から決まったコマンドを回す——行の印・コマンドの覚え方・行への付け方。</summary>
public sealed class WorktreeRunsTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 8, 3, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-wtrun-test-" + Guid.NewGuid().ToString("N"));

    public WorktreeRunsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Marks_say_running_passed_or_failed()
    {
        var running = new WorktreeRun(@"C:\work\api-fix", "dotnet test", T0);
        Assert.Equal("▶ dotnet test", WorktreeRuns.Mark(running));
        Assert.Equal("✓ dotnet test", WorktreeRuns.Mark(running with { ExitCode = 0, FinishedUtc = T0 }));
        Assert.Equal("✗ dotnet test", WorktreeRuns.Mark(running with { ExitCode = 1, FinishedUtc = T0 }));
        // シェル統合が終了コードを言わなかったら、通ったとは言わない。
        Assert.Equal("✗ dotnet test", WorktreeRuns.Mark(running with { ExitCode = null, FinishedUtc = T0 }));
    }

    [Fact]
    public void Long_commands_are_cut_in_the_mark()
    {
        var mark = WorktreeRuns.Mark(new WorktreeRun(@"C:\w", "dotnet test --filter FullyQualifiedName~Something", T0));
        Assert.EndsWith("…", mark);
        Assert.True(mark.Length <= WorktreeRuns.MarkCommandLength + 2);
    }

    [Fact]
    public void The_last_run_of_each_worktree_wins_and_trailing_separators_do_not_matter()
    {
        var marks = WorktreeRuns.Marks([
            new WorktreeRun(@"C:\work\api-fix\", "dotnet build", T0, 0, T0),
            new WorktreeRun(@"C:\work\api-fix", "dotnet test", T0.AddMinutes(1), 1, T0.AddMinutes(2)),
            new WorktreeRun(@"C:\work\web", "npm test", T0, 0, T0),
        ]);
        Assert.Equal("✗ dotnet test", marks[@"C:\work\api-fix"]);
        Assert.Equal("✓ npm test", marks[@"c:\WORK\web"]);
    }

    [Fact]
    public void The_command_is_remembered_per_repository()
    {
        var store = Path.Combine(_root, "worktree-run-commands.json");
        var api = Path.Combine(_root, "api");
        var web = Path.Combine(_root, "web");
        Assert.Null(WorktreeRuns.RememberedCommand(store, api));

        WorktreeRuns.Remember(store, api, "  dotnet test  ");
        WorktreeRuns.Remember(store, web, "npm test");
        Assert.Equal("dotnet test", WorktreeRuns.RememberedCommand(store, api + Path.DirectorySeparatorChar));
        Assert.Equal("npm test", WorktreeRuns.RememberedCommand(store, web.ToUpperInvariant()));

        File.WriteAllText(store, "{ broken");
        Assert.Null(WorktreeRuns.RememberedCommand(store, api));   // 読めないファイルは無いものとして扱う
    }

    [Fact]
    public void The_mark_goes_on_the_branch_row_that_holds_the_worktree()
    {
        var here = new GitWorktreeInfo { Path = @"C:\work\app", Branch = "main", IsMain = true, IsCurrent = true };
        var holder = new GitWorktreeInfo { Path = @"C:\work\api-fix", Branch = "fix/api" };
        var tree = BranchTreeBuilder.Build([new GitBranchInfo("main", true, false, null), new GitBranchInfo("fix/api", false, false, null)]);
        GitPaneTreeComposer.ApplyWorktreeMarks(tree, [here, holder], new Dictionary<string, string>(),
            WorktreeRuns.Marks([new WorktreeRun(@"C:\work\api-fix\", "dotnet test", T0, 0, T0)]));
        var branchRow = Leaves(tree).Single(n => n.Branch?.Name == "fix/api");
        Assert.Equal("✓ dotnet test", branchRow.RunMark);
        Assert.Equal("", Leaves(tree).Single(n => n.Branch?.Name == "main").RunMark);

        GitPaneTreeComposer.ApplyWorktreeMarks(tree, [here, holder], new Dictionary<string, string>(),
            new Dictionary<string, string>());
        Assert.Equal("", branchRow.RunMark);
    }

    private static IEnumerable<BranchTreeNode> Leaves(IEnumerable<BranchTreeNode> nodes)
        => nodes.SelectMany(n => n.Branch is not null ? [n] : Leaves(n.Children));
}
