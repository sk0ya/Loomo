namespace sk0ya.Loomo.App.Layout;

/// <summary>ステージ／俯瞰表示のUI非依存な状態を所有する。</summary>
public sealed class StageModeCoordinator
{
    public static readonly PaneKind[] StageOrder =
    [
        PaneKind.Editor, PaneKind.Terminal, PaneKind.Browser, PaneKind.EditorSupport, PaneKind.Git,
        PaneKind.Diff, PaneKind.Ai, PaneKind.Debug, PaneKind.TsIde, PaneKind.Search, PaneKind.Files,
    ];

    public static readonly PaneKind[] DefaultEnabledSessions =
    [
        PaneKind.Editor, PaneKind.Terminal, PaneKind.Browser, PaneKind.EditorSupport, PaneKind.Git, PaneKind.Diff,
    ];

    public bool Active { get; set; }
    public bool Overview { get; set; }
    public PaneKind Pane { get; set; } = PaneKind.Editor;
    public bool IdePaneApplicable { get; set; } = true;
    public bool TsIdePaneApplicable { get; set; } = true;
    public HashSet<PaneKind> EnabledSessions { get; } = new();

    /// <summary>優先順に、現在利用できる舞台ペインを選ぶ。どれも使えなければEditorへ戻す。</summary>
    public static PaneKind ResolvePane(
        IEnumerable<PaneKind?> preferredPanes,
        Func<PaneKind, bool> isAvailable)
    {
        foreach (var candidate in preferredPanes)
            if (candidate is { } pane && isAvailable(pane))
                return pane;
        return PaneKind.Editor;
    }

    /// <summary>ワークスペース復元時の有効セッションを、現在のペイン構成と適用性へ合わせる。</summary>
    public void LoadEnabledSessions(
        IEnumerable<PaneKind>? enabled,
        IEnumerable<PaneKind> availablePanes,
        IReadOnlyList<PaneKind> defaults)
    {
        var available = new HashSet<PaneKind>(availablePanes);
        EnabledSessions.Clear();
        if (enabled is not null)
            foreach (var kind in enabled)
                if (available.Contains(kind))
                    EnabledSessions.Add(kind);
        if (EnabledSessions.Count == 0)
            foreach (var kind in defaults)
                EnabledSessions.Add(kind);
        if (!IdePaneApplicable)
            EnabledSessions.Remove(PaneKind.Debug);
        if (!TsIdePaneApplicable)
            EnabledSessions.Remove(PaneKind.TsIde);
    }

    public static PaneKind CyclePane(IReadOnlyList<PaneKind> order, PaneKind current, int direction)
    {
        if (order.Count == 0) return current;
        var index = -1;
        for (var i = 0; i < order.Count; i++)
            if (order[i] == current)
            {
                index = i;
                break;
            }
        var next = ((index < 0 ? 0 : index) + direction) % order.Count;
        return order[(next + order.Count) % order.Count];
    }

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

    /// <summary>セッションの有効状態を切り替える。戻り値は表示一覧の再構築が必要かを示す。</summary>
    public bool ToggleEnabledSession(
        PaneKind kind,
        Func<bool> isVisible,
        Action hideVisiblePane,
        Func<bool> isHidden,
        Action showHiddenPane)
    {
        if (EnabledSessions.Contains(kind))
        {
            if (isVisible())
            {
                hideVisiblePane();
                if (isVisible())
                    return false;
            }
            EnabledSessions.Remove(kind);
            return true;
        }

        EnabledSessions.Add(kind);
        if (isHidden())
        {
            showHiddenPane();
            return false;
        }
        return true;
    }

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

    public bool ToggleOverview()
    {
        if (!Active)
            return false;
        Overview = !Overview;
        return true;
    }

    public bool CloseOverview()
    {
        if (!Overview)
            return false;
        Overview = false;
        return true;
    }

    public bool SelectWingTab(WingTab tab)
    {
        if (ActiveWingTab == tab)
            return false;
        ActiveWingTab = tab;
        return true;
    }

    public void Restore(bool active, bool overview, PaneKind pane)
    {
        Active = active;
        Overview = active && overview;
        Pane = pane;
    }
}
