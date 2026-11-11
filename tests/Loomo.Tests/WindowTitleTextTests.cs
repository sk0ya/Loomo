using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>OS に見せるウィンドウタイトル（PC の作業記録用）：「ペイン - タブ - Loomo」。</summary>
public sealed class WindowTitleTextTests
{
    [Fact]
    public void ペインとタブを並べる()
        => Assert.Equal("エディタ - Program.cs - Loomo", WindowTitleText.Format(PaneKind.Editor, "Program.cs"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void タブが無ければペインだけ(string? tab)
        => Assert.Equal("Git - Loomo", WindowTitleText.Format(PaneKind.Git, tab));

    [Fact]
    public void ペインの外ならアプリ名だけ()
        => Assert.Equal("Loomo", WindowTitleText.Format(null, "Program.cs"));
}
