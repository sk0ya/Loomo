using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>時間のかかったコマンドが見ていない間に終わったら知らせる（§24.97）。</summary>
public sealed class LongCommandNoticeTests
{
    [Fact]
    public void 見ていない間に10秒以上かかったものだけ知らせる()
    {
        Assert.True(LongCommandNotice.ShouldNotify(true, TimeSpan.FromSeconds(10), watched: false));
        Assert.False(LongCommandNotice.ShouldNotify(true, TimeSpan.FromSeconds(9.9), watched: false));
        Assert.False(LongCommandNotice.ShouldNotify(true, TimeSpan.FromMinutes(3), watched: true));
        Assert.False(LongCommandNotice.ShouldNotify(false, TimeSpan.FromMinutes(3), watched: false));
        // 始まりを見ていない（時間が分からない）コマンドは知らせない。
        Assert.False(LongCommandNotice.ShouldNotify(true, null, watched: false));
    }

    [Fact]
    public void 成否と所要時間を一文にする()
    {
        Assert.Equal("dotnet test が失敗しました（終了コード 1・2分14秒）",
            LongCommandNotice.Message("dotnet test", 1, TimeSpan.FromSeconds(134)));
        Assert.Equal("npm run build が終わりました（12秒）",
            LongCommandNotice.Message("npm run build", 0, TimeSpan.FromSeconds(12)));
        Assert.Equal("コマンド が終わりました（1時間3分）",
            LongCommandNotice.Message(null, null, TimeSpan.FromMinutes(63)));
        Assert.Equal(NotificationKind.Error, LongCommandNotice.Kind(2));
        Assert.Equal(NotificationKind.Success, LongCommandNotice.Kind(null));
    }

    [Fact]
    public void 長いコマンドは頭だけにする()
    {
        var message = LongCommandNotice.Message(new string('a', 100), 0, TimeSpan.FromSeconds(10));
        Assert.StartsWith(new string('a', 59) + "…", message);
    }
}
