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
