using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>棚の1行（§24.29）。</summary>
public sealed partial class ShelvedTabEntryViewModel : ObservableObject
{
    public ShelvedTabEntryViewModel(ShelvedTabSnapshot snapshot) => Snapshot = snapshot;

    public ShelvedTabSnapshot Snapshot { get; }

    public bool IsEditor => Snapshot.Kind == ShelvedTabSnapshot.EditorKind;

    public string Title => IsEditor
        ? (Snapshot.FilePath is { Length: > 0 } path ? Path.GetFileName(path) : Snapshot.Title ?? "Untitled")
        : (string.IsNullOrWhiteSpace(Snapshot.Title) ? Snapshot.Url ?? "" : Snapshot.Title!);

    /// <summary>行に添える場所（エディタはフォルダー、ブラウザはホスト名）。</summary>
    public string Detail
    {
        get
        {
            if (IsEditor)
                return Snapshot.FilePath is { Length: > 0 } path ? Path.GetFileName(Path.GetDirectoryName(path)) ?? "" : "";
            return Uri.TryCreate(Snapshot.Url, UriKind.Absolute, out var uri) ? uri.Host : "";
        }
    }

    /// <summary>行のツールチップ（場所と、いつ消えるか）。</summary>
    public string ToolTip
    {
        get
        {
            var place = (IsEditor ? Snapshot.FilePath : Snapshot.Url) ?? Title;
            return TabShelfPolicy.DaysLeft(Snapshot, DateTime.UtcNow) is { } days
                ? $"{place}\n{(days == 0 ? "まもなく" : $"あと {days} 日で")}棚から消えます（ピン留めすると残ります）"
                : $"{place}\nピン留め中（棚から消えません）";
        }
    }

    /// <summary>いつ下げたか（「3時間前」）。</summary>
    public string ShelvedLabel => PegboardItemVm.FormatRelative(Snapshot.ShelvedUtc.ToLocalTime(), DateTime.Now);

    /// <summary>棚の行のピン留め（期限が来ても消さない）。</summary>
    public bool IsPinned => Snapshot.Pinned;

    [ObservableProperty] private ImageSource? _icon;

    public void RefreshTime()
    {
        OnPropertyChanged(nameof(ShelvedLabel));
        OnPropertyChanged(nameof(ToolTip));
    }

    internal void TogglePin()
    {
        Snapshot.Pinned = !Snapshot.Pinned;
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(ToolTip));
    }
}

/// <summary>
/// タブの棚（§24.29）。しばらく見ていないエディタ／ブラウザのタブを帯から下げて置いておく場所。
/// 上限の枚数を超えて自動で閉じたことの<b>取り消し</b>の置き場——戻すと、下げたときの場所（キャレット・
/// スクロール・URL）へ戻る。下げてから 7 日で消える（行をピン留めしたものは残る）。ワークスペースごとに持つ
/// （<see cref="WorkspaceSnapshot.ShelvedTabs"/>）。TABS では「エディタ」「ブラウザ」それぞれの中に畳んで出す。
/// </summary>
public sealed partial class TabShelfViewModel : ObservableObject
{
    private readonly Func<ImageSource?> _browserIcon;

    public TabShelfViewModel(Func<ImageSource?>? browserIcon = null)
    {
        _browserIcon = browserIcon ?? (() => null);
        Items.CollectionChanged += (_, _) =>
        {
            SyncKinds();
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(HasItems));
        };
    }

    /// <summary>新しい順（正本。種類ごとの一覧はここから作る）。</summary>
    public ObservableCollection<ShelvedTabEntryViewModel> Items { get; } = new();

    /// <summary>棚のうちエディタのタブ（新しい順）。TABS の帯と同じく種類で区切って見せる。</summary>
    public ObservableCollection<ShelvedTabEntryViewModel> EditorItems { get; } = new();

    /// <summary>棚のうちブラウザのタブ（新しい順）。</summary>
    public ObservableCollection<ShelvedTabEntryViewModel> BrowserItems { get; } = new();

    public int Count => Items.Count;
    public bool HasItems => Items.Count > 0;
    public bool HasEditorItems => EditorItems.Count > 0;
    public bool HasBrowserItems => BrowserItems.Count > 0;

    private void SyncKinds()
    {
        Fill(EditorItems, Items.Where(i => i.IsEditor));
        Fill(BrowserItems, Items.Where(i => !i.IsEditor));
        OnPropertyChanged(nameof(HasEditorItems));
        OnPropertyChanged(nameof(HasBrowserItems));

        static void Fill(ObservableCollection<ShelvedTabEntryViewModel> target, IEnumerable<ShelvedTabEntryViewModel> source)
        {
            var items = source.ToList();
            if (items.SequenceEqual(target))
                return;
            target.Clear();
            foreach (var item in items)
                target.Add(item);
        }
    }

    /// <summary>行を押した（戻す）。</summary>
    public event EventHandler<IReadOnlyList<ShelvedTabSnapshot>>? RestoreRequested;

    /// <summary>棚の中身が人の操作で変わった（保存の合図）。</summary>
    public event EventHandler? Changed;

    /// <summary>ワークスペース切替で中身を入れ替える（保存は発火しない）。</summary>
    public void Load(IEnumerable<ShelvedTabSnapshot>? snapshots)
    {
        Items.Clear();
        foreach (var snapshot in snapshots ?? [])
            Items.Add(CreateEntry(snapshot));
    }

    public List<ShelvedTabSnapshot> ToSnapshots() => Items.Select(i => i.Snapshot).ToList();

    /// <summary>棚へ足す（新しいものを先頭へ・同じものは1行に・上限を超えた古い分は捨てる）。</summary>
    public void Add(IReadOnlyList<ShelvedTabSnapshot> added)
    {
        if (added.Count == 0)
            return;
        var merged = TabShelfPolicy.Merge(ToSnapshots(), added);
        Load(merged);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>棚から外す（戻した・捨てた）。外したものがあれば true。</summary>
    public bool Remove(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        var removed = false;
        foreach (var item in Items.Where(i => set.Contains(i.Snapshot.Id)).ToList())
        {
            Items.Remove(item);
            removed = true;
        }
        if (removed)
            Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>このファイルの行を外す（棚を通らずに開き直した）。</summary>
    public void ForgetFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || Items.Count == 0)
            return;
        var probe = new ShelvedTabSnapshot { Kind = ShelvedTabSnapshot.EditorKind, FilePath = path };
        Remove(Items.Where(i => TabShelfPolicy.IsSameTarget(i.Snapshot, probe)).Select(i => i.Snapshot.Id).ToList());
    }

    /// <summary>期限切れ（下げてから 7 日・ピン留め以外）を消す。消したものがあれば true。</summary>
    public bool PurgeExpired(DateTime nowUtc) => Remove(TabShelfPolicy.Expired(ToSnapshots(), nowUtc));

    public void RefreshTimes()
    {
        foreach (var item in Items)
            item.RefreshTime();
    }

    public void RefreshIcons()
    {
        foreach (var item in Items)
            item.Icon = IconFor(item.Snapshot);
    }

    [RelayCommand]
    private void Restore(ShelvedTabEntryViewModel? entry)
    {
        if (entry is not null)
            RestoreRequested?.Invoke(this, [entry.Snapshot]);
    }

    [RelayCommand]
    private void RestoreAll()
    {
        if (Items.Count > 0)
            RestoreRequested?.Invoke(this, ToSnapshots());
    }

    [RelayCommand]
    private void Discard(ShelvedTabEntryViewModel? entry)
    {
        if (entry is not null)
            Remove([entry.Snapshot.Id]);
    }

    [RelayCommand]
    private void TogglePin(ShelvedTabEntryViewModel? entry)
    {
        if (entry is null)
            return;
        entry.TogglePin();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>棚を空にする（ピン留めした行は残す——ピン留めは「消さないで」の意思表示なので）。</summary>
    [RelayCommand]
    private void Clear()
        => Remove(Items.Where(i => !i.IsPinned).Select(i => i.Snapshot.Id).ToList());

    private ShelvedTabEntryViewModel CreateEntry(ShelvedTabSnapshot snapshot)
        => new(snapshot) { Icon = IconFor(snapshot) };

    private ImageSource? IconFor(ShelvedTabSnapshot snapshot)
        => snapshot.Kind == ShelvedTabSnapshot.EditorKind
            ? FileIcons.ImageFor(snapshot.FilePath is { Length: > 0 } path
                ? FileIcons.IndexFor(path, isDirectory: false)
                : FileIconData.DefaultFileIndex)
            : _browserIcon();
}
