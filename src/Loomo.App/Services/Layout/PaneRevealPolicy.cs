namespace sk0ya.Loomo.App.Services;

internal enum PaneRevealAction { None, SelectStage, OpenDock, PlaceInLayout }
internal readonly record struct PaneRevealPlan(PaneKind Target, PaneRevealAction Action);

/// <summary>ペインを表示する必要があるか、表示モードに応じて選ぶ。</summary>
internal static class PaneRevealPolicy
{
    public static PaneRevealPlan ForOpenedFile(
        bool isBinaryFile, bool dockActive, bool stageActive,
        bool editorVisible, bool supportVisible, bool editorOnStage, bool supportOnStage)
    {
        var target = isBinaryFile ? PaneKind.EditorSupport : PaneKind.Editor;
        var action = dockActive ? PaneRevealAction.OpenDock
            : stageActive
                ? editorOnStage || supportOnStage ? PaneRevealAction.None : PaneRevealAction.SelectStage
                : editorVisible || supportVisible ? PaneRevealAction.None : PaneRevealAction.PlaceInLayout;
        return new PaneRevealPlan(target, action);
    }

    public static PaneRevealAction ForPane(
        bool stageActive, bool dockActive, bool targetVisible, bool targetOnStage)
    {
        if (stageActive) return targetOnStage ? PaneRevealAction.None : PaneRevealAction.SelectStage;
        if (dockActive) return PaneRevealAction.OpenDock;
        return targetVisible ? PaneRevealAction.None : PaneRevealAction.PlaceInLayout;
    }
}
