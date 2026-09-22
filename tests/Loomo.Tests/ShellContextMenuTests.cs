using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>Explorer のメニューは同じフォルダーの項目しかまとめて扱えない。</summary>
public sealed class ShellContextMenuTests
{
    [Fact]
    public void 同じフォルダーの複数選択はまとめて渡す()
    {
        var targets = ShellContextMenu.SameParentTargets([@"C:\work\a.txt", @"C:\work\b.txt", @"C:\WORK\a.txt"]);

        Assert.Equal([@"C:\work\a.txt", @"C:\work\b.txt"], targets);
    }

    [Fact]
    public void 親が揃わなければ右クリックした先頭だけにする()
    {
        var targets = ShellContextMenu.SameParentTargets([@"C:\work\a.txt", @"C:\work\sub\b.txt"]);

        Assert.Equal([@"C:\work\a.txt"], targets);
    }

    [Fact]
    public void 末尾区切りのフォルダーも親で比べる()
    {
        var targets = ShellContextMenu.SameParentTargets([@"C:\work\src\", @"C:\work\a.txt"]);

        Assert.Equal(2, targets.Count);
    }
}

/// <summary>ZIP はフォルダーだけに出す（ファイルの ZIP 化は Explorer のメニューへ寄せた）。</summary>
public sealed class CompressMenuVisibilityTests
{
    [Fact]
    public void フォルダーだけを選んでいるときZIPを出す()
    {
        var state = new FileContextMenuState { SelectionCount = 2, FileCount = 0 };

        Assert.True(FileContextMenuPolicy.IsVisible("DirSelection", state, false));
    }

    [Fact]
    public void ファイルが混ざっていればZIPを出さない()
    {
        Assert.False(FileContextMenuPolicy.IsVisible(
            "DirSelection", new FileContextMenuState { SelectionCount = 2, FileCount = 1 }, false));
        Assert.False(FileContextMenuPolicy.IsVisible("DirSelection", default, false));
    }
}
