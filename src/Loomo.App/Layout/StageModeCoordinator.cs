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

    /// <summary>メイングループのペイン。ここに無い面はすべてサブ。所属は<b>固定</b>で、ユーザー設定でも
    /// ワークスペース状態でもない——袖が10枚を超えると一覧として読めなくなるため、常時そばに置く道具
    /// （書く・動かす・見る・履歴）だけをメインに残し、用があるときに呼ぶ面はサブのタブへ畳む。</summary>
    public static readonly IReadOnlySet<PaneKind> MainGroup = new HashSet<PaneKind>
    {
        PaneKind.Editor, PaneKind.Terminal, PaneKind.Browser, PaneKind.Git
    };

    /// <summary>袖で選択中のタブ。袖にはこのタブの顔ぶれだけが並ぶ。既定は「すべて」＝従来どおり全部。</summary>
    public WingTab ActiveWingTab { get; set; } = WingTab.All;

    public bool IsOnStage(PaneKind kind) => Active && Pane == kind;

    public static PaneGroup GroupOf(PaneKind kind) => MainGroup.Contains(kind) ? PaneGroup.Main : PaneGroup.Sub;

    /// <summary>袖の選択中タブに出るペインか。</summary>
    public bool IsInActiveWingTab(PaneKind kind) => Shows(ActiveWingTab, kind);

    /// <summary><paramref name="tab"/> に出るペインか（件数の数え上げにも使う）。</summary>
    public static bool Shows(WingTab tab, PaneKind kind) => tab switch
    {
        WingTab.Main => GroupOf(kind) == PaneGroup.Main,
        WingTab.Sub => GroupOf(kind) == PaneGroup.Sub,
        _ => true,   // すべて
    };

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
