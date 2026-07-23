using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Layout;

/// <summary>ステージ／俯瞰表示のUI非依存な状態を所有する。</summary>
public sealed class StageModeCoordinator
{
    public bool Active { get; set; }
    public bool Overview { get; set; }
    public PaneKind Pane { get; set; } = PaneKind.Editor;
    public bool IdePaneApplicable { get; set; } = true;
    public bool TsIdePaneApplicable { get; set; } = true;
    public HashSet<PaneKind> EnabledSessions { get; } = new();

    public bool IsOnStage(PaneKind kind) => Active && Pane == kind;

    public bool Enter(PaneKind pane)
    {
        if (Active)
            return false;
        Active = true;
        Overview = false;
        Pane = pane;
        return true;
    }

    public bool Exit()
    {
        if (!Active)
            return false;
        Active = false;
        Overview = false;
        return true;
    }

    public bool Select(PaneKind pane)
    {
        if (!Active)
            return false;
        Overview = false;
        Pane = pane;
        return true;
    }

    public void Restore(bool active, bool overview, PaneKind pane)
    {
        Active = active;
        Overview = active && overview;
        Pane = pane;
    }
}
