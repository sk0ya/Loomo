using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>番号でタブを選ぶ（§24.47）。ピン留めが先頭・9 は最後。</summary>
public sealed class TabNumberPolicyTests
{
    private sealed record Tab(string Name, bool Pinned = false);

    private static string? Pick(Tab[] tabs, int n)
        => TabNumberPolicy.Pick(tabs, n, t => t.Pinned) is >= 0 and var i ? tabs[i].Name : null;

    [Fact]
    public void 並びのとおりに数える()
    {
        Tab[] tabs = [new("a"), new("b"), new("c")];

        Assert.Equal("a", Pick(tabs, 1));
        Assert.Equal("c", Pick(tabs, 3));
        Assert.Null(Pick(tabs, 4));
    }

    [Fact]
    public void ピン留めが先頭に来る()
    {
        Tab[] tabs = [new("a"), new("p1", true), new("b"), new("p2", true)];

        Assert.Equal("p1", Pick(tabs, 1));
        Assert.Equal("p2", Pick(tabs, 2));
        Assert.Equal("a", Pick(tabs, 3));
    }

    [Fact]
    public void 九は最後のタブ()
    {
        var tabs = Enumerable.Range(1, 12).Select(i => new Tab("t" + i)).ToArray();

        Assert.Equal("t12", Pick(tabs, 9));
        Assert.Equal("t8", Pick(tabs, 8));
        Assert.Equal("only", Pick([new("only")], 9));
    }

    [Fact]
    public void タブが無ければ無し()
        => Assert.Null(Pick([], 1));
}
