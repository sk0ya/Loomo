namespace sk0ya.Loomo.App.Services;

internal enum PaneRevealAction { None, SelectStage, OpenDock, PlaceInLayout }
internal readonly record struct PaneRevealPlan(PaneKind Target, PaneRevealAction Action);

/// <summary>ペインを表示する必要があるか、表示モードに応じて選ぶ。</summary>
internal static class PaneRevealPolicy
{
    /// <summary>ファイルを開いたときに Editor／EditorSupport のどちらかを出すか。分割・集中は
    /// 「どちらかが既に見えていれば触らない」——ファイルはその面に映るので、EditorSupport を
    /// 前に出して見ているところへ Editor を割り込ませない。
    /// <para>ドックは領域が分かれているので、控えるのは<b>出す面の領域をもう片方が占めている</b>
    /// ときだけ（出せば押し退ける）。EditorSupport が右に出ていても、中央が Terminal 等なら
    /// Editor を中央に出す——右の EditorSupport は押し退けないし、中央にファイルが見えない
    /// ままでは開いた意味が無い。<paramref name="sameDockRegion"/> は2つが同じ領域に居るか。</para></summary>
    public static PaneRevealPlan ForOpenedFile(
        bool isBinaryFile, bool dockActive, bool stageActive,
        bool editorVisible, bool supportVisible, bool editorOnStage, bool supportOnStage,
        bool editorDockShown, bool supportDockShown, bool sameDockRegion)
    {
        var target = isBinaryFile ? PaneKind.EditorSupport : PaneKind.Editor;
        var targetDockShown = isBinaryFile ? supportDockShown : editorDockShown;
        var otherDockShown = isBinaryFile ? editorDockShown : supportDockShown;
        var action = dockActive
                ? targetDockShown || (sameDockRegion && otherDockShown) ? PaneRevealAction.None : PaneRevealAction.OpenDock
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
