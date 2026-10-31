using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.Tests;

/// <summary>ブレークポイントとウォッチ式を部屋に残す（§28.16）。</summary>
public sealed class DebugRoomStateTests
{
    private static DebugViewModel NewManager()
        => new(new sk0ya.Loomo.Services.Debug.NetcoredbgDebugSessionFactory(),
            new FakeWorkspaceService(),
            new FakeTerminalService(),
            new sk0ya.Loomo.CSharp.Testing.TestDiscoveryService(),
            new DebugLaunchProfileStore(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-launch.json")));

    [Fact]
    public void ブレークポイントを条件ごと写して戻せる()
    {
        var source = NewManager();
        var a = Path.GetFullPath(@"C:\repo\src\A.cs");
        source.Breakpoints.ToggleBreakpoint(a, 9);
        var bp = source.Breakpoints.EnsureBreakpoint(a, 3);
        bp.Condition = "i > 5";
        bp.Enabled = false;

        var captured = source.Breakpoints.Capture();
        Assert.Equal([3, 9], captured.Select(b => b.Line0));
        Assert.Equal("i > 5", captured[0].Condition);
        Assert.False(captured[0].Enabled);
        Assert.Null(captured[1].Condition);

        var restored = NewManager();
        restored.Breakpoints.ToggleBreakpoint(Path.GetFullPath(@"C:\other\B.cs"), 1);   // 前の部屋のもの
        restored.Breakpoints.Load(captured, _ => true);

        Assert.Equal([3, 9], restored.Breakpoints.GetBreakpoints(a));
        Assert.Empty(restored.Breakpoints.GetBreakpoints(@"C:\other\B.cs"));
        Assert.Equal("i > 5", restored.Breakpoints.FindBreakpoint(a, 3)!.Condition);
        Assert.False(restored.Breakpoints.FindBreakpoint(a, 3)!.Enabled);
        Assert.Equal(2, restored.Breakpoints.Breakpoints.Count);
    }

    [Fact]
    public void 消えたファイルのブレークポイントは戻さない()
    {
        var manager = NewManager();
        manager.Breakpoints.Load(
            [new BreakpointSnapshot { Path = @"C:\repo\Gone.cs", Line0 = 1 }, new BreakpointSnapshot { Path = @"C:\repo\Here.cs", Line0 = 2 }],
            path => path.EndsWith("Here.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["Here.cs"], manager.Breakpoints.Breakpoints.Select(b => b.FileName));

        manager.Breakpoints.Load(null);   // 保存したことの無い部屋は空にする
        Assert.False(manager.Breakpoints.HasBreakpoints);
    }

    [Fact]
    public void ウォッチ式はセッションをまたいで残り_部屋で入れ替わる()
    {
        var manager = NewManager();
        manager.LoadWatchExpressions(["count", "  ", "count", "items.Length"]);
        Assert.Equal(["count", "items.Length"], manager.WatchExpressions);

        var first = manager.CreateSession("A", DebugSessionKind.Launch);
        Assert.Equal(["count", "items.Length"], first.Inspection.Watches.Select(w => w.Expression));

        first.Inspection.WatchExpression = "name";
        first.Inspection.AddWatchCommand.Execute(null);
        Assert.Equal(["count", "items.Length", "name"], manager.WatchExpressions);

        // 次に起動したセッションも同じウォッチ式から始まる。
        var second = manager.CreateSession("B", DebugSessionKind.Launch);
        Assert.Equal(["count", "items.Length", "name"], second.Inspection.Watches.Select(w => w.Expression));

        // 別の部屋へ切り替えると、開いているセッションの一覧も入れ替わる。
        manager.LoadWatchExpressions(["other"]);
        Assert.Equal(["other"], first.Inspection.Watches.Select(w => w.Expression));
        Assert.Equal(["other"], second.Inspection.Watches.Select(w => w.Expression));
    }

    [Fact]
    public void ワークツリーの部屋の種ではブレークポイントを同じファイルへ付け替える()
    {
        const string main = @"C:\work\app";
        const string worktree = @"C:\work\app-feature";
        var source = new WorkspaceSnapshot
        {
            RootPath = main,
            DebugState = new DebugRoomSnapshot
            {
                Dotnet = new DebugManagerSnapshot
                {
                    Breakpoints = [new BreakpointSnapshot { Path = main + @"\src\A.cs", Line0 = 4 }],
                    Watches = ["x"],
                },
            },
        };

        var seed = WorktreeRoomSeed.Seed(source, new WorktreeSwitchRequest(worktree, main, main, [main, worktree]), _ => true);

        Assert.Equal(worktree + @"\src\A.cs", Assert.Single(seed.DebugState!.Dotnet.Breakpoints).Path);
        Assert.Equal(["x"], seed.DebugState.Dotnet.Watches);
        Assert.Equal(main + @"\src\A.cs", source.DebugState.Dotnet.Breakpoints[0].Path);   // 元は変えない
    }
}
