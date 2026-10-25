using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>ほかのワークツリーにある同じファイル（§24.80）。持ち主はいちばん深いワークツリー。</summary>
public sealed class WorktreeCounterpartsTests
{
    private static readonly (string, string?)[] Worktrees =
    [
        (@"C:\work\app", "main"),
        (@"C:\work\app.worktrees\feature", "feature"),
        (@"C:\work\app.worktrees\detached", null),
    ];

    [Fact]
    public void 持ち主以外のワークツリーの同じ相対パスを並べる()
    {
        var list = WorktreeCounterparts.For(@"C:\work\app\src\Program.cs", Worktrees, _ => true);

        Assert.Equal(2, list.Count);
        Assert.Equal(@"C:\work\app.worktrees\feature\src\Program.cs", list[0].FilePath);
        Assert.Equal("feature（feature）", list[0].Label);
        Assert.Equal(@"C:\work\app.worktrees\detached\src\Program.cs", list[1].FilePath);
        Assert.Equal("detached（ブランチなし）", list[1].Label);
    }

    [Fact]
    public void 中に置いたワークツリーのファイルはいちばん深いものが持ち主()
    {
        (string, string?)[] nested = [(@"C:\work\app", "main"), (@"C:\work\app\.wt\fix", "fix")];

        var list = WorktreeCounterparts.For(@"C:\work\app\.wt\fix\a.txt", nested, _ => true);

        var only = Assert.Single(list);
        Assert.Equal(@"C:\work\app\a.txt", only.FilePath);
    }

    [Fact]
    public void 相手の版に無ければそうと分かる()
    {
        var list = WorktreeCounterparts.For(@"C:\work\app\new.cs", Worktrees,
            path => !path.Contains("feature", StringComparison.Ordinal));

        Assert.False(list[0].Exists);
        Assert.True(list[1].Exists);
    }

    [Fact]
    public void どのワークツリーにも入っていなければ空()
    {
        Assert.Empty(WorktreeCounterparts.For(@"D:\other\x.cs", Worktrees, _ => true));
        Assert.Empty(WorktreeCounterparts.For("relative.cs", Worktrees, _ => true));
    }
}
