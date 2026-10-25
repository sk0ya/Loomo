using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>タブをキーボードで並べ替える（§24.77）。ピン留めの組はまたがない。</summary>
public sealed class TabMovePolicyTests
{
    private sealed record Tab(string Name, bool Pinned = false);

    private static string Order(List<Tab> tabs) => string.Join(",", tabs.Select(t => t.Name));

    private static bool Move(List<Tab> tabs, string name, int delta)
    {
        var index = tabs.FindIndex(t => t.Name == name);
        var target = TabMovePolicy.Target(tabs, index, delta, t => t.Pinned);
        if (target < 0)
            return false;
        TabMovePolicy.Move(tabs, index, target);
        return true;
    }

    [Fact]
    public void 隣と入れ替わる()
    {
        List<Tab> tabs = [new("a"), new("b"), new("c")];

        Assert.True(Move(tabs, "b", +1));
        Assert.Equal("a,c,b", Order(tabs));
        Assert.True(Move(tabs, "b", -1));
        Assert.Equal("a,b,c", Order(tabs));
    }

    [Fact]
    public void 端では動かない()
    {
        List<Tab> tabs = [new("a"), new("b")];

        Assert.False(Move(tabs, "a", -1));
        Assert.False(Move(tabs, "b", +1));
        Assert.Equal("a,b", Order(tabs));
    }

    [Fact]
    public void ピン留めの組をまたがない()
    {
        List<Tab> tabs = [new("p1", true), new("a"), new("p2", true), new("b")];

        // ピン留めは間に挟まった外れたタブを飛ばして、次のピン留めと入れ替わる。
        Assert.True(Move(tabs, "p1", +1));
        Assert.Equal("a,p2,p1,b", Order(tabs));
        // 外れたタブも同じ組の相手としか入れ替わらない。
        Assert.True(Move(tabs, "a", +1));
        Assert.Equal("p2,p1,b,a", Order(tabs));
        Assert.False(Move(tabs, "p2", -1));
    }
}
