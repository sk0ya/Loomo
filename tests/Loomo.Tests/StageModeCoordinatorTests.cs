using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public class StageModeCoordinatorTests
{
    [Theory]
    [InlineData(PaneKind.Editor, PaneGroup.Main)]
    [InlineData(PaneKind.Terminal, PaneGroup.Main)]
    [InlineData(PaneKind.Browser, PaneGroup.Main)]
    [InlineData(PaneKind.Git, PaneGroup.Main)]
    [InlineData(PaneKind.EditorSupport, PaneGroup.Sub)]
    [InlineData(PaneKind.Diff, PaneGroup.Sub)]
    [InlineData(PaneKind.Ai, PaneGroup.Sub)]
    [InlineData(PaneKind.Debug, PaneGroup.Sub)]
    [InlineData(PaneKind.TsIde, PaneGroup.Sub)]
    [InlineData(PaneKind.Search, PaneGroup.Sub)]
    [InlineData(PaneKind.Files, PaneGroup.Sub)]
    public void Pane_group_is_fixed_and_covers_every_pane(PaneKind kind, PaneGroup expected)
        => Assert.Equal(expected, StageModeCoordinator.GroupOf(kind));

    [Fact]
    public void Active_wing_tab_selects_which_panes_the_wing_shows()
    {
        var state = new StageModeCoordinator();

        Assert.Equal(WingTab.All, state.ActiveWingTab);   // 既定は「すべて」＝従来どおり全部出す
        Assert.True(state.IsInActiveWingTab(PaneKind.Terminal));
        Assert.True(state.IsInActiveWingTab(PaneKind.Diff));

        state.ActiveWingTab = WingTab.Main;
        Assert.True(state.IsInActiveWingTab(PaneKind.Terminal));
        Assert.False(state.IsInActiveWingTab(PaneKind.Diff));

        state.ActiveWingTab = WingTab.Sub;
        Assert.False(state.IsInActiveWingTab(PaneKind.Terminal));
        Assert.True(state.IsInActiveWingTab(PaneKind.Diff));
    }
}
