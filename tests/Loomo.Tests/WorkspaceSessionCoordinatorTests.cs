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
}
