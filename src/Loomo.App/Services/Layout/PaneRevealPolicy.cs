namespace sk0ya.Loomo.App.Services;

internal enum PaneRevealAction { None, SelectStage, OpenDock, PlaceInLayout }
internal readonly record struct PaneRevealPlan(PaneKind Target, PaneRevealAction Action);

/// <summary>ペインを表示する必要があるか、表示モードに応じて選ぶ。</summary>
internal static class PaneRevealPolicy
{
    /// <summary>ファイルを開いたときに Editor／EditorSupport のどちらかを出すか。分割・集中は
    /// 「どちらかが既に見えていれば触らない」——ファイルはその面に映るので、EditorSupport を
    /// 前に出して見ているところへ Editor を割り込ませない。
    /// <para>ドックは領域が分かれているので、もう片方が出ていても控えるのは次のときだけ：
    /// 出す面の領域をもう片方が占めている（出せば押し退ける）か、出す面の領域が空いている
    /// （中央を畳んで EditorSupport で見ている——ファイルはもう見えているので中央を開かない）。
    /// EditorSupport が右に出ていても、中央に Terminal 等の<b>別の面</b>が出ていれば Editor を
    /// 中央に出す——右の EditorSupport は押し退けないし、その面の陰ではファイルが見えない。
    /// <paramref name="sameDockRegion"/> は2つが同じ領域に居るか、
    /// <paramref name="targetDockRegionEmpty"/> は出す面の領域に何も出ていないか。</para></summary>
    public static PaneRevealPlan ForOpenedFile(
        bool isBinaryFile, bool dockActive, bool stageActive,
        bool editorVisible, bool supportVisible, bool editorOnStage, bool supportOnStage,
        bool editorDockShown, bool supportDockShown, bool sameDockRegion, bool targetDockRegionEmpty)
    {
        var target = isBinaryFile ? PaneKind.EditorSupport : PaneKind.Editor;
        var targetDockShown = isBinaryFile ? supportDockShown : editorDockShown;
        var otherDockShown = isBinaryFile ? editorDockShown : supportDockShown;
        var action = dockActive
                ? targetDockShown || (otherDockShown && (sameDockRegion || targetDockRegionEmpty))
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
