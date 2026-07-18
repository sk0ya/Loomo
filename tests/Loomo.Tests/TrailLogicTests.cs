using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

public class TrailLogicTests
{
    [Theory]
    [InlineData("commit", "commit", "コミット")]
    [InlineData("commit --amend", "commit", "コミット（amend）")]
    [InlineData("restore --staged file.cs", "unstage", "アンステージ")]
    [InlineData("switch -c feature", "branch-create", "ブランチ作成: feature")]
    [InlineData("merge --abort", "merge", "マージ中止")]
    public void Git_operations_are_classified(string command, string key, string label)
        => Assert.Equal((key, label), TrailLogic.DescribeGitOperation(command));

    [Theory]
    [InlineData(null, false)]
    [InlineData("about:blank", false)]
    [InlineData("https://example.com", true)]
    public void Browser_URL_filter_is_shared(string? url, bool expected)
        => Assert.Equal(expected, TrailLogic.IsRecordableBrowserUrl(url, "https://www.google.com"));

    [Fact]
    public void Layout_key_ignores_weights_but_includes_mode_and_stage_pane()
    {
        var first = new PaneNodeSnapshot { Kind = PaneKind.Editor, Weight = 1 };
        var second = new PaneNodeSnapshot { Kind = PaneKind.Editor, Weight = 9 };
        Assert.Equal(
            TrailLogic.LayoutKey(DisplayMode.Layout, null, first),
            TrailLogic.LayoutKey(DisplayMode.Layout, null, second));
        Assert.NotEqual(
            TrailLogic.LayoutKey(DisplayMode.Layout, null, first),
            TrailLogic.LayoutKey(DisplayMode.Solo, PaneKind.Editor, first));
    }
}
