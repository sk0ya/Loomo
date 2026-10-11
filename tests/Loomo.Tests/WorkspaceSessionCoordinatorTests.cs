using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public class WorkspaceSessionCoordinatorTests
{
    [Theory]
    [InlineData(DisplayMode.Solo, false, true)]
    [InlineData(DisplayMode.Layout, true, false)]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    public void ResolveSoloMode_migrates_legacy_stage_state(
        DisplayMode? mode, bool legacyStage, bool expected)
    {
        var workspace = new WorkspaceSnapshot
        {
            Mode = mode,
            Stage = new StageSnapshot { IsActive = legacyStage }
        };
        Assert.Equal(expected, WorkspaceSessionCoordinator.ResolveSoloMode(workspace));
    }

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
