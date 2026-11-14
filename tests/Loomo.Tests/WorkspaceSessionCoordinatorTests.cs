using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public class WorkspaceSessionCoordinatorTests
{
    /// <summary>ソロ（撤去済み）と Mode の無い旧データは分割として開く。ドックだけがドック。</summary>
    [Theory]
    [InlineData(DisplayMode.Solo, DisplayMode.Layout)]
    [InlineData(DisplayMode.Layout, DisplayMode.Layout)]
    [InlineData(DisplayMode.Dock, DisplayMode.Dock)]
    [InlineData(null, DisplayMode.Layout)]
    public void ResolveDisplayMode_opens_retired_solo_as_layout(DisplayMode? mode, DisplayMode expected)
        => Assert.Equal(expected, WorkspaceSessionCoordinator.ResolveDisplayMode(new WorkspaceSnapshot { Mode = mode }));

    [Theory]
    [InlineData("", "about:blank")]
    [InlineData("example.com", "https://example.com")]
    [InlineData("localhost", "http://localhost")]
    [InlineData("hello world", "https://www.google.com/search?q=hello%20world")]
    public void NormalizeBrowserAddress_handles_urls_hosts_and_queries(string input, string expected)
        => Assert.Equal(expected, WorkspaceSessionCoordinator.NormalizeBrowserAddress(input, "about:blank"));

    [Fact]
    public void ResolveRestorableTerminalTabs_drops_tabs_whose_shell_is_gone()
    {
        var alive = new TerminalTabSnapshot { Id = Guid.NewGuid(), CustomName = "alive" };
        var gone = new TerminalTabSnapshot { Id = Guid.NewGuid(), CustomName = "gone" };

        var restorable = WorkspaceSessionCoordinator.ResolveRestorableTerminalTabs([gone, alive], [alive.Id]);

        Assert.Equal([alive], restorable);
    }

    [Fact]
    public void ResolveRestorableTerminalTabs_keeps_everything_when_the_host_did_not_answer()
    {
        var tabs = new[] { new TerminalTabSnapshot { Id = Guid.NewGuid() }, new TerminalTabSnapshot { Id = Guid.NewGuid() } };

        Assert.Equal(tabs, WorkspaceSessionCoordinator.ResolveRestorableTerminalTabs(tabs, hostSessions: null));
    }
}
