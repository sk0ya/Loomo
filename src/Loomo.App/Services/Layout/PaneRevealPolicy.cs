namespace sk0ya.Loomo.App.Services;

internal enum PaneRevealAction { None, SelectStage, OpenDock, PlaceInLayout }
internal readonly record struct PaneRevealPlan(PaneKind Target, PaneRevealAction Action);

/// <summary>ペインを表示する必要があるか、表示モードに応じて選ぶ。</summary>
internal static class PaneRevealPolicy
{
    /// <summary>ファイルを開いたときに Editor／EditorSupport のどちらかを出すか。分割・集中は
    /// 「どちらかが既に見えていれば触らない」——ファイルはその面に映るので、EditorSupport を
    /// 前に出して見ているところへ Editor を割り込ませない。
    /// <para>ドックだけは、EditorSupport が出ていても Editor の領域（中央）に<b>別の面</b>
    /// （Terminal 等）が出ていれば Editor を出す——その面の陰ではファイルが見えない。中央が
    /// 空いている・EditorSupport 自身が居るときは、ファイルはもう見えているので触らない。
    /// <paramref name="otherInEditorDockRegion"/> は Editor の領域に Editor／EditorSupport 以外が出ているか。</para></summary>
    public static PaneRevealPlan ForOpenedFile(
        bool isBinaryFile, bool dockActive, bool stageActive,
        bool editorVisible, bool supportVisible, bool editorOnStage, bool supportOnStage,
        bool editorDockShown, bool supportDockShown, bool otherInEditorDockRegion)
    {
        var target = isBinaryFile ? PaneKind.EditorSupport : PaneKind.Editor;
        var action = dockActive
                ? editorDockShown || (supportDockShown && !otherInEditorDockRegion)
                    ? PaneRevealAction.None : PaneRevealAction.OpenDock
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
