using sk0ya.Loomo.App.Services;

using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ファイルを開いたときに Editor／EditorSupport を出すかの固定。どのモードでも「どちらかが既に
/// 見えていれば触らない」。ドックだけ無条件に Editor を開いていたため、EditorSupport を前に出して
/// 見ているところでファイルを開くと Editor が割り込んでいた。
/// </summary>
public class PaneRevealPolicyTests
{
    private static PaneRevealPlan Dock(bool binary, bool editorShown, bool supportShown, bool sameRegion = true, bool targetRegionEmpty = false)
        => PaneRevealPolicy.ForOpenedFile(
            binary, dockActive: true, stageActive: false,
            editorVisible: false, supportVisible: false, editorOnStage: false, supportOnStage: false,
            editorDockShown: editorShown, supportDockShown: supportShown, sameDockRegion: sameRegion, targetDockRegionEmpty: targetRegionEmpty);

    [Fact]
    public void ドックで同じ領域にEditorSupportが出ているときはEditorを前に出さない()
        => Assert.Equal(PaneRevealAction.None, Dock(binary: false, editorShown: false, supportShown: true).Action);

    [Fact]
    public void ドックで中央が空いていて右にEditorSupportが出ているときはEditorを出さない()
        => Assert.Equal(PaneRevealAction.None,
            Dock(binary: false, editorShown: false, supportShown: true, sameRegion: false, targetRegionEmpty: true).Action);

    [Fact]
    public void ドックで右にEditorSupportが出ていても中央に別の面が出ていればEditorを開く()
    {
        var plan = Dock(binary: false, editorShown: false, supportShown: true, sameRegion: false);
        Assert.Equal(PaneRevealAction.OpenDock, plan.Action);
        Assert.Equal(PaneKind.Editor, plan.Target);
    }

    [Fact]
    public void ドックで別の領域にEditorが出ていてもEditorSupportが出ていなければバイナリはEditorSupportを開く()
    {
        var plan = Dock(binary: true, editorShown: true, supportShown: false, sameRegion: false);
        Assert.Equal(PaneRevealAction.OpenDock, plan.Action);
        Assert.Equal(PaneKind.EditorSupport, plan.Target);
    }

    [Fact]
    public void ドックでEditorが出ているときは何もしない()
        => Assert.Equal(PaneRevealAction.None, Dock(binary: false, editorShown: true, supportShown: false).Action);

    [Fact]
    public void ドックでどちらも出ていなければEditorを開く()
    {
        var plan = Dock(binary: false, editorShown: false, supportShown: false);
        Assert.Equal(PaneRevealAction.OpenDock, plan.Action);
        Assert.Equal(PaneKind.Editor, plan.Target);
    }

    [Fact]
    public void ドックでどちらも出ていなければバイナリはEditorSupportを開く()
    {
        var plan = Dock(binary: true, editorShown: false, supportShown: false);
        Assert.Equal(PaneRevealAction.OpenDock, plan.Action);
        Assert.Equal(PaneKind.EditorSupport, plan.Target);
    }

    [Fact]
    public void 分割でEditorSupportが見えているときは何もしない()
        => Assert.Equal(PaneRevealAction.None, PaneRevealPolicy.ForOpenedFile(
            false, dockActive: false, stageActive: false,
            editorVisible: false, supportVisible: true, editorOnStage: false, supportOnStage: false,
            editorDockShown: false, supportDockShown: false, sameDockRegion: false, targetDockRegionEmpty: false).Action);
}
