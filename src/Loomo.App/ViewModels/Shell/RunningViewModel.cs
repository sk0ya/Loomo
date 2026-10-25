using System.Collections.ObjectModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>動いているもの面の 1 行（§24.54）。中身は <see cref="RunningEntry"/> を写したもので、同じ鍵の行は
/// 引き直しても同じインスタンスのまま値だけ変える（選択・スクロールを壊さない）。</summary>
public sealed partial class RunningItemViewModel : ObservableObject
{
    public RunningItemViewModel(RunningEntry entry, DateTime firstSeenUtc)
    {
        Key = entry.Key;
        FirstSeenUtc = firstSeenUtc;
        Apply(entry);
    }

    public string Key { get; }

    /// <summary>この面が最初に見た時刻。出所が開始時刻を持たないもの（デバッグ・言語サーバー）の経過はここから数える。</summary>
    public DateTime FirstSeenUtc { get; }

    public RunningEntry Entry { get; private set; } = null!;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _elapsed = "";
    [ObservableProperty] private string? _toolTip;
    [ObservableProperty] private RunningEntryTone _tone;
    [ObservableProperty] private bool _canStop;
    [ObservableProperty] private string _group = "";
    [ObservableProperty] private string _icon = "";

    public int GroupOrder => RunningEntries.GroupOrder(Group);

    /// <summary>「あとで」に回してある（§24.71）。</summary>
    public bool IsSnoozed => Group == RunningEntries.SnoozedGroup;

    internal void Apply(RunningEntry entry)
    {
        Entry = entry;
        Title = entry.Title;
        Detail = entry.Detail;
        ToolTip = entry.ToolTip;
        Tone = entry.Tone;
        CanStop = entry.CanStop;
        Group = entry.Group;
        OnPropertyChanged(nameof(IsSnoozed));
        Icon = entry.Kind switch
        {
            RunningEntryKind.Terminal => "▶",
            RunningEntryKind.Claude => "✳",
            RunningEntryKind.Debug => "🐞",
            RunningEntryKind.IdeTask => "⚙",
            RunningEntryKind.Ai => "✦",
            _ => "◇",
        };
    }

    internal void Tick(DateTime nowUtc)
    {
        // 言語サーバーは「いつから居るか」より状態が大事なので経過は出さない。
        Elapsed = Entry.Kind == RunningEntryKind.LanguageServer
            ? ""
            : TerminalTabSummary.Elapsed(nowUtc - (Entry.SinceUtc ?? FirstSeenUtc));
    }
}

/// <summary>サイドバーの「動いているもの」面（§24.54）。行の組み立ては <see cref="RunningEntries"/>、
/// 材料集めと行き先・停止は ShellWindow（<c>ShellWindow.Running.cs</c>）が持つ。</summary>
public sealed partial class RunningViewModel : ObservableObject
{
    public RunningViewModel()
    {
        View = CollectionViewSource.GetDefaultView(Items);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RunningItemViewModel.Group)));
    }

    public ObservableCollection<RunningItemViewModel> Items { get; } = new();

    /// <summary>グループ見出し（あなたの番／実行中／常駐）つきの一覧。</summary>
    public ICollectionView View { get; }

    /// <summary>人の番の数（ActivityBar のアイコンの印・見出しの件数）。</summary>
    [ObservableProperty] private int _attentionCount;

    /// <summary>言語サーバーを除いた、走っているものの数。</summary>
    [ObservableProperty] private int _runningCount;

    public bool IsEmpty => Items.Count == 0;

    public event EventHandler<RunningItemViewModel>? ActivateRequested;
    public event EventHandler<RunningItemViewModel>? StopRequested;

    /// <summary>「あとで」に回した行（鍵 → その時点の状態の鍵・§24.71）。保存はしない。</summary>
    private readonly Dictionary<string, string> _snoozed = new(StringComparer.Ordinal);

    /// <summary>最後に渡された一覧（「あとで」を切り替えたときに並べ直すため）。</summary>
    private IReadOnlyList<RunningEntry> _lastEntries = Array.Empty<RunningEntry>();

    /// <summary>人の番の数が変わった（「あとで」の切り替えでも変わる。ActivityBar の印を付け直す）。</summary>
    public event EventHandler? AttentionChanged;

    /// <summary>引き直した一覧を入れる。鍵が同じ行は使い回し、並びは渡された順にそろえる。</summary>
    public void Apply(IReadOnlyList<RunningEntry> entries, DateTime nowUtc)
    {
        _lastEntries = entries;
        entries = RunningEntries.ApplySnooze(entries, _snoozed);
        var existing = Items.ToDictionary(i => i.Key, StringComparer.Ordinal);
        var wanted = new List<RunningItemViewModel>(entries.Count);
        var regrouped = false;
        foreach (var entry in entries)
        {
            if (existing.Remove(entry.Key, out var item))
            {
                var oldGroup = item.Group;
                item.Apply(entry);
                regrouped |= !string.Equals(oldGroup, item.Group, StringComparison.Ordinal);
            }
            else
                item = new RunningItemViewModel(entry, nowUtc);
            item.Tick(nowUtc);
            wanted.Add(item);
        }

        foreach (var gone in existing.Values)
            Items.Remove(gone);
        for (var i = 0; i < wanted.Count; i++)
        {
            var index = Items.IndexOf(wanted[i]);
            if (index < 0)
                Items.Insert(i, wanted[i]);
            else if (index != i)
                Items.Move(index, i);
        }
        // ビューはライブグルーピングではないので、使い回した行の Group が変わっても Move が起きなければ
        // 古い見出しの下に残る（先頭 1 行が 実行中→あなたの番 になったときなど）。変わったときだけ組み直す。
        if (regrouped)
            View.Refresh();

        var attention = Items.Count(i => i.Tone == RunningEntryTone.Attention);
        var changed = attention != AttentionCount;
        AttentionCount = attention;
        RunningCount = Items.Count(i => i.Entry.Kind != RunningEntryKind.LanguageServer);
        OnPropertyChanged(nameof(IsEmpty));
        if (changed)
            AttentionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>経過時間だけ進める。</summary>
    public void Tick(DateTime nowUtc)
    {
        foreach (var item in Items)
            item.Tick(nowUtc);
    }

    [RelayCommand]
    private void Activate(RunningItemViewModel? item)
    {
        if (item is not null)
            ActivateRequested?.Invoke(this, item);
    }

    /// <summary>「あとで」に回す。いまの状態を覚え、それが変わったら自動で戻す。</summary>
    [RelayCommand]
    private void Snooze(RunningItemViewModel? item)
    {
        if (item is null || item.IsSnoozed) return;
        _snoozed[item.Key] = RunningEntries.SnoozeSignature(item.Entry);
        Apply(_lastEntries, DateTime.UtcNow);
    }

    [RelayCommand]
    private void Unsnooze(RunningItemViewModel? item)
    {
        if (item is null || !_snoozed.Remove(item.Key)) return;
        Apply(_lastEntries, DateTime.UtcNow);
    }

    [RelayCommand]
    private void Stop(RunningItemViewModel? item)
    {
        if (item is { CanStop: true })
            StopRequested?.Invoke(this, item);
    }
}
