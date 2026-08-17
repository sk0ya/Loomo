using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public class StageModeCoordinatorTests
{
    [Fact]
    public void Enter_select_exit_has_explicit_state_transitions()
    {
        var state = new StageModeCoordinator();

        Assert.True(state.Enter(PaneKind.Terminal));
        Assert.True(state.Active);
        Assert.True(state.IsOnStage(PaneKind.Terminal));
        Assert.False(state.Enter(PaneKind.Editor));

        state.Overview = true;
        Assert.True(state.Select(PaneKind.Editor));
        Assert.False(state.Overview);
        Assert.True(state.IsOnStage(PaneKind.Editor));

        Assert.True(state.Exit());
        Assert.False(state.Active);
        Assert.False(state.Overview);
        Assert.False(state.IsOnStage(PaneKind.Editor));
        Assert.False(state.Exit());
    }

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

    [Fact]
    public void Restore_never_keeps_overview_when_stage_is_inactive()
    {
        var state = new StageModeCoordinator();
        state.Restore(active: false, overview: true, pane: PaneKind.Browser);

        Assert.False(state.Active);
        Assert.False(state.Overview);
        Assert.Equal(PaneKind.Browser, state.Pane);
    }
}
