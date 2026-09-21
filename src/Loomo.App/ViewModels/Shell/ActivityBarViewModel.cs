using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services.Settings;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ActivityBar（左端の縦帯）の段。上段は画面の上から、中段は画面の中ほどから並ぶ。
/// 段はそれぞれ自分のサイドバー区画を持つので、2つのパネルを同時に見ていられる。</summary>
public enum ActivityBarSlot
{
    /// <summary>上段バー（ウィンドウ上端から下へ並ぶ）。</summary>
    Primary,
    /// <summary>中段バー（ウィンドウの縦中央から下へ並ぶ）。</summary>
    Secondary
}

/// <summary>ActivityBar のアイコン1つ。どの段に住むかは人間がドラッグで決める。</summary>
public sealed partial class ActivityBarItemViewModel : ObservableObject
{
    public ActivityBarItemViewModel(string id, SidebarPanel panel, string icon, string label,
        string automationId)
    {
        Id = id;
        Panel = panel;
        Icon = icon;
        Label = label;
        AutomationId = automationId;
    }

    /// <summary>設定へ保存する安定した識別子（表示名やアイコンを変えても配置が壊れないように）。</summary>
    public string Id { get; }

    /// <summary>このアイコンが開くサイドバーパネル。</summary>
    public SidebarPanel Panel { get; }

    /// <summary>アイコン文字。</summary>
    public string Icon { get; }

    /// <summary>ツールチップ／読み上げ名。</summary>
    public string Label { get; }

    /// <summary>UI オートメーションの識別子。<see cref="Id"/>（保存用）とは別に持つ——保存の Id は
    /// 並びの永続化に使う都合で変えられない一方、実機検証が掴む名前は画面の語彙で付けたい。
    /// 兼ねさせると、どちらかの都合でもう一方が黙って壊れる。</summary>
    public string AutomationId { get; }

    /// <summary>いま住んでいる段。</summary>
    [ObservableProperty] private ActivityBarSlot _slot;

    /// <summary>この段で表示中のパネルか（アクセントの印）。</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>いまの部屋で出せる面か。出せないものはアイコンごと消す
    /// （C# の無い部屋のソリューションなど）。</summary>
    [ObservableProperty] private bool _isAvailable = true;
}

/// <summary>項目が動いたときの通知。段をまたいだのか、同じ段での並べ替えだけなのかを分けて伝える
/// ——並べ替えただけの項目を開き直すと、人間がしていないナビゲーションを軌跡へ書いてしまう（§27）。</summary>
/// <param name="Item">動いた項目。</param>
/// <param name="SlotChanged">段が変わったか（false＝同じ段での並べ替え）。</param>
public sealed record ActivityBarItemMoved(ActivityBarItemViewModel Item, bool SlotChanged);

/// <summary>ActivityBar 2本ぶんの項目配置。並びと段はドラッグ＆ドロップで人間が決め、
/// settings.json（<see cref="ActivityBarSettings"/>）へ持ち越す。</summary>
public sealed partial class ActivityBarViewModel : ObservableObject
{
    private readonly LoomoSettings? _settings;
    private readonly SettingsStore? _settingsStore;
    private readonly List<ActivityBarItemViewModel> _items;

    /// <summary>既定で上段に並ぶ項目 Id（上から順）。</summary>
    public static readonly string[] DefaultPrimaryIds = ["explorer", "git", "solution", "pegboard"];

    /// <summary>既定で中段に並ぶ項目 Id。中段は「タブ一覧だけ」から始める。</summary>
    public static readonly string[] DefaultSecondaryIds = ["tabs"];

    public ActivityBarViewModel(LoomoSettings? settings = null, SettingsStore? settingsStore = null)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _items =
        [
            new("explorer", SidebarPanel.Explorer, "🗂", "エクスプローラ", "ExplorerPanelButton"),
            new("git", SidebarPanel.Git, "⎇", "Git", "GitPanelButton"),
            new("solution", SidebarPanel.Solution, "◈", "ソリューション（C#）", "SolutionPanelButton"),
            new("pegboard", SidebarPanel.Pegboard, "📌", "ペグボード（スニペット・URL・パスの作業台）",
                "PegboardPanelButton"),
            new("tabs", SidebarPanel.Tabs, "▤", "タブ一覧", "TabsPanelButton"),
        ];

        var layout = Arrange(
            _items.Select(i => i.Id).ToList(),
            settings?.ActivityBar.Primary ?? [],
            settings?.ActivityBar.Secondary ?? []);
        PrimaryItems = new ObservableCollection<ActivityBarItemViewModel>(layout.Primary.Select(ItemById));
        SecondaryItems = new ObservableCollection<ActivityBarItemViewModel>(layout.Secondary.Select(ItemById));
        foreach (var item in PrimaryItems) item.Slot = ActivityBarSlot.Primary;
        foreach (var item in SecondaryItems) item.Slot = ActivityBarSlot.Secondary;
    }

    /// <summary>上段バーの項目（上から順）。</summary>
    public ObservableCollection<ActivityBarItemViewModel> PrimaryItems { get; }

    /// <summary>中段バーの項目（上から順）。</summary>
    public ObservableCollection<ActivityBarItemViewModel> SecondaryItems { get; }

    /// <summary>全項目（段をまたぐ）。</summary>
    public IReadOnlyList<ActivityBarItemViewModel> Items => _items;

    /// <summary>項目が段を移った・並べ替えられた。ホストは区画の作り直しに使う。</summary>
    public event EventHandler<ActivityBarItemMoved>? ItemMoved;

    public ObservableCollection<ActivityBarItemViewModel> ItemsIn(ActivityBarSlot slot)
        => slot == ActivityBarSlot.Primary ? PrimaryItems : SecondaryItems;

    public ActivityBarItemViewModel? ItemFor(SidebarPanel panel)
        => _items.FirstOrDefault(i => i.Panel == panel);

    /// <summary>そのパネルのアイコンが住んでいる段。</summary>
    public ActivityBarSlot SlotOf(SidebarPanel panel)
        => ItemFor(panel)?.Slot ?? ActivityBarSlot.Primary;

    /// <summary>その段でいま開ける最初のパネル。<paramref name="excluding"/> は数えない。
    /// 段が空（あるいは出せる面が無い）なら null＝その区画は畳む。</summary>
    public SidebarPanel? FirstAvailablePanel(ActivityBarSlot slot, SidebarPanel? excluding = null)
        => ItemsIn(slot).FirstOrDefault(i => i.IsAvailable && i.Panel != excluding)?.Panel;

    /// <summary>そのパネルがその段に住んでいて、いま出せるか。</summary>
    public bool Holds(ActivityBarSlot slot, SidebarPanel panel)
        => ItemsIn(slot).Any(i => i.Panel == panel && i.IsAvailable);

    /// <summary>出せる面かどうかを切り替える（C# の有無でソリューションのアイコンが出入りする）。</summary>
    public void SetAvailable(SidebarPanel panel, bool available)
    {
        if (ItemFor(panel) is { } item)
            item.IsAvailable = available;
    }

    /// <summary>項目を段の指定位置へ移す（同じ段なら並べ替え）。実際に動いたら true。
    /// <paramref name="index"/> は「何番目の前に入れるか」＝0〜件数の挿入位置で、件数ちょうどが末尾。
    /// 同じ段の並べ替えでは上限を件数-1に丸めてはいけない——末尾へ落としたつもりが
    /// 最後から2番目に入り、一番下へは二度と置けなくなる。</summary>
    public bool Move(ActivityBarItemViewModel item, ActivityBarSlot slot, int index)
    {
        var from = ItemsIn(item.Slot);
        var to = ItemsIn(slot);
        var oldIndex = from.IndexOf(item);
        if (oldIndex < 0) return false;

        var sameSlot = ReferenceEquals(from, to);
        var target = Math.Clamp(index, 0, to.Count);
        // 自分の前と自分の直後は、どちらも「いまと同じ場所」。
        if (sameSlot && (target == oldIndex || target == oldIndex + 1)) return false;

        from.RemoveAt(oldIndex);
        if (sameSlot && target > oldIndex) target--;
        to.Insert(Math.Clamp(target, 0, to.Count), item);
        item.Slot = slot;
        Persist();
        ItemMoved?.Invoke(this, new ActivityBarItemMoved(item, SlotChanged: !sameSlot));
        return true;
    }

    private ActivityBarItemViewModel ItemById(string id) => _items.First(i => i.Id == id);

    private void Persist()
    {
        if (_settings is null) return;
        _settings.ActivityBar.Primary = PrimaryItems.Select(i => i.Id).ToList();
        _settings.ActivityBar.Secondary = SecondaryItems.Select(i => i.Id).ToList();
        try { _settingsStore?.Save(_settings); }
        catch { /* 永続化に失敗しても並べ替え自体は効かせる */ }
    }

    /// <summary>保存された並びを今の項目一覧へ突き合わせる（純ロジック）。未知の Id は捨て、
    /// 重複は先に出た方を採り、どちらの段にも現れなかった項目は既定の段の末尾へ落とす。
    /// 保存が空（初回起動）なら既定配置をそのまま使う。</summary>
    public static (List<string> Primary, List<string> Secondary) Arrange(
        IReadOnlyList<string> known,
        IReadOnlyList<string> savedPrimary,
        IReadOnlyList<string> savedSecondary)
    {
        var primary = new List<string>();
        var secondary = new List<string>();
        if (savedPrimary.Count == 0 && savedSecondary.Count == 0)
        {
            primary.AddRange(DefaultPrimaryIds.Where(known.Contains));
            secondary.AddRange(DefaultSecondaryIds.Where(known.Contains));
            return (primary, secondary);
        }

        var placed = new HashSet<string>();
        foreach (var id in savedPrimary)
            if (known.Contains(id) && placed.Add(id)) primary.Add(id);
        foreach (var id in savedSecondary)
            if (known.Contains(id) && placed.Add(id)) secondary.Add(id);
        // 保存後に増えた項目は消さずに既定の段へ。
        foreach (var id in known.Where(id => !placed.Contains(id)))
            (DefaultSecondaryIds.Contains(id) ? secondary : primary).Add(id);
        return (primary, secondary);
    }
}
