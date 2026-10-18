using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>前に出ていたタブを閉じたあとに出すタブ（§24.44）。隣ではなく直前に見ていたタブ。</summary>
public sealed class TabCloseSuccessorTests
{
    private static readonly DateTime T0 = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

    private static DateTime Seen(int minutesAgo) => T0.AddMinutes(-minutesAgo);

    [Fact]
    public void 隣ではなく直前に見ていたタブを選ぶ()
    {
        // 閉じたのは 1 番目。隣（残りの 1 番目）は 30 分前、0 番目は 1 分前に見ていた
        var remaining = new[] { Seen(1), Seen(30), Seen(60) };

        Assert.Equal(0, TabCloseSuccessor.Pick(remaining, removedIndex: 1, t => t));
    }

    [Fact]
    public void どれも見ていなければ隣()
    {
        var remaining = new[] { default(DateTime), default, default };

        Assert.Equal(1, TabCloseSuccessor.Pick(remaining, removedIndex: 1, t => t));
        Assert.Equal(2, TabCloseSuccessor.Pick(remaining, removedIndex: 5, t => t));
    }

    [Fact]
    public void 見ていないタブより見たタブを選ぶ()
    {
        var remaining = new[] { default, default, Seen(120) };

        Assert.Equal(2, TabCloseSuccessor.Pick(remaining, removedIndex: 0, t => t));
    }

    [Fact]
    public void 同じ時刻なら閉じた位置に近いもの()
    {
        var remaining = new[] { Seen(5), Seen(5), Seen(5), Seen(5) };

        Assert.Equal(2, TabCloseSuccessor.Pick(remaining, removedIndex: 2, t => t));
    }

    [Fact]
    public void 残りが無ければ負()
        => Assert.Equal(-1, TabCloseSuccessor.Pick(Array.Empty<DateTime>(), 0, t => t));
}
