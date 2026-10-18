using System.Windows;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>ターミナルの通知を通知欄へ（§24.49）・軌跡からその頃の出力へ（§24.51）・リンクを運ぶときの行（§24.52）。</summary>
public sealed class TerminalNotificationPresentationTests
{
    [Fact]
    public void 題があれば題と本文をつなぐ()
    {
        Assert.Equal("Build: done", TerminalNotificationPresentation.Message("Build", "done"));
        Assert.Equal("done", TerminalNotificationPresentation.Message(null, "done"));
        Assert.Equal("Build", TerminalNotificationPresentation.Message("Build", ""));
    }

    [Fact]
    public void 出所はタブ名つき()
    {
        Assert.Equal("ターミナル: api", TerminalNotificationPresentation.Source("api"));
        Assert.Equal("ターミナル", TerminalNotificationPresentation.Source(null));
    }

    [Fact]
    public void いまに近い地点では出力へ飛ばない()
    {
        var now = new DateTime(2026, 10, 19, 10, 0, 0);

        Assert.False(TerminalLineTimePolicy.ShouldScrollTo(now.AddSeconds(-30), now));
        Assert.True(TerminalLineTimePolicy.ShouldScrollTo(now.AddMinutes(-10), now));
    }

    [Fact]
    public void 運ぶ行と列を読み戻せる()
    {
        var data = new DataObject();
        FileDragDrop.SetSourceLocation(data, 12, 5);

        Assert.True(FileDragDrop.TryGetSourceLocation(data, out var line, out var column));
        Assert.Equal((12, 5), (line, column));
        Assert.False(FileDragDrop.TryGetSourceLocation(new DataObject(DataFormats.UnicodeText, "x"), out _, out _));
    }
}
