using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;
/// <summary>操作地点を日別に表示し、過去の地点への移動を要求する軌跡バー。</summary>
public sealed partial class TrailViewModel : ObservableObject
{
    private readonly Func<DateTime> _now;
    private readonly AiSettings? _settings;
    private readonly AiSettingsStore? _settingsStore;
    private readonly TrailRecordHandler _recorder;
    private readonly TrailHistoryQuery _history;
    private bool _loaded;

    private bool _followingToday = true;

    /// <summary>いま記録が積まれている論理的な「今日」。<see cref="_todayLatest"/> はこの日に属する。
    /// 実行したまま日付を跨いだら、次の記録でこの日を新しい今日へ繰り上げてデデュープを仕切り直す。</summary>

    private string _workspaceKey = "";

    /// <summary>今日の最新エントリ（デデュープと離脱位置上書きの対象）。過去日を表示中でも
    /// 記録は常に今日へ積むため、表示リストとは別に保持する。</summary>

    public TrailViewModel(TrailStore store, Func<DateTime>? clock = null,
        AiSettings? settings = null, AiSettingsStore? settingsStore = null)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _now = clock ?? (() => DateTime.Now);
        _recorder = new TrailRecordHandler(store, _now);
        _history = new TrailHistoryQuery(store);
        _displayDate = Today;
        // 設定に保存された表示状態を初期反映する（field 直接代入なので OnVisibleChanged＝永続化は走らない）。
        _visible = settings?.TrailVisible ?? true;
    }

    public ObservableCollection<TrailEntryViewModel> Entries { get; } = new();

    public event EventHandler<TrailEntryViewModel>? JumpRequested;

    /// <summary>表示中または保存済みの記録があるか。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarVisible))]
    private bool _hasEntries;

    /// <summary>ユーザー設定による軌跡バーの表示ON/OFF。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarVisible))]
    private bool _visible = true;

    public bool BarVisible => Visible && HasEntries;

    partial void OnVisibleChanged(bool value)
    {
        if (_settings is null)
            return;
        _settings.TrailVisible = value;
        try { _settingsStore?.Save(_settings); }
        catch { /* 永続化失敗でも表示切替自体は効かせる */ }
    }

    [RelayCommand]
    private void Hide() => Visible = false;

    /// <summary>表示中の日。記録は常に今日へ追加される。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DateLabel))]
    [NotifyPropertyChangedFor(nameof(DateTimeLabel))]
    private DateOnly _displayDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HourLabel))]
    [NotifyPropertyChangedFor(nameof(DateTimeLabel))]
    private int _currentIndex = -1;

    public ObservableCollection<TrailHourViewModel> Hours { get; } = new();

    private DateOnly Today => DateOnly.FromDateTime(_now());

    public bool IsViewingPast => !_followingToday;

    public string DateLabel => DisplayDate.ToString("M/d (ddd)");

    public string DateTimeLabel => $"{DateLabel}  {HourLabel}";

    public bool HasHours => Hours.Count > 0;

    /// <summary>ライブでは現在時刻、過去地点ではその時間帯を示す。</summary>
    public string HourLabel
    {
        get
        {
            // ライブ（今日を追従していて、かつ軌跡の最新地点＝「今」にいる）なら現在時刻を出す。
            if (_followingToday && CurrentIndex >= Entries.Count - 1)
                return _now().ToString("HH:mm");
            // 過去の地点へスクラブ中、または過去日表示中：その地点の時間帯を HH:00 で表す。
            var band = CurrentEntry?.Timestamp ?? _now();
            return $"{band.Hour:D2}:00";
        }
    }

    public TrailEntryViewModel? CurrentEntry =>
        CurrentIndex >= 0 && CurrentIndex < Entries.Count ? Entries[CurrentIndex] : null;

    public void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;
        ReloadForWorkspace();
    }

    /// <summary>表示・記録対象のワークスペースを切り替える。</summary>
    public void SetWorkspace(string workspaceKey)
    {
        if (string.Equals(_workspaceKey, workspaceKey, StringComparison.Ordinal))
            return;
        _workspaceKey = workspaceKey;
        _recorder.SetWorkspace(workspaceKey);
        if (_loaded)
            ReloadForWorkspace();
    }

    private void ReloadForWorkspace()
    {
        try
        {
            LoadInto(Today);
            _recorder.SetLatest(Today, Entries.Count > 0 ? Entries[^1] : null);
            HasEntries = Entries.Count > 0 || _history.HasAny(_workspaceKey);
        }
        catch
        {
            // DB が読めなくてもメモリ内動作で続行する（以後の記録も best-effort）。
        }
    }

    // 記録 API は入力を正規化し、デデュープと永続化を TrailRecordHandler へ委譲する。
    public void RecordFile(string path, int line = -1, int column = -1,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Record(TrailEntryKind.File, path, Path.GetFileName(path), line, column, displayMode, stagePane, paneLayout);
    }

    public void RecordEdit(string path, int line = -1, int column = -1,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Record(TrailEntryKind.Edit, path, Path.GetFileName(path), line, column, displayMode, stagePane, paneLayout);
    }

    public void RecordGit(string operationKey, string label,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null)
    {
        if (string.IsNullOrWhiteSpace(operationKey)) return;
        Record(TrailEntryKind.Git, operationKey, string.IsNullOrWhiteSpace(label) ? "Git" : label,
            displayMode: displayMode, stagePane: stagePane, paneLayout: null);
    }

    public void RecordBrowser(string url, string? title,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var label = string.IsNullOrWhiteSpace(title) ? HostOf(url) : title.Trim();
        Record(TrailEntryKind.Browser, url, label, displayMode: displayMode, stagePane: stagePane,
            paneLayout: paneLayout);
    }

    public void RecordPane(string paneKindName, string label,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
        => Record(TrailEntryKind.Pane, paneKindName, label, displayMode: displayMode, stagePane: stagePane,
            paneLayout: paneLayout);

    public void RecordPanel(string panelName, string label,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
        => Record(TrailEntryKind.Panel, panelName, label, displayMode: displayMode, stagePane: stagePane,
            paneLayout: paneLayout);

    public void RecordTerminal(Guid tabId, string label,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
        => Record(TrailEntryKind.Terminal, tabId.ToString("D"), label,
            displayMode: displayMode, stagePane: stagePane, paneLayout: paneLayout);

    public void RecordPreview(string path,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Record(TrailEntryKind.Preview, path, Path.GetFileName(path),
            displayMode: displayMode, stagePane: stagePane, paneLayout: paneLayout);
    }

    public void RecordSession(string id, string title,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        Record(TrailEntryKind.Session, id, string.IsNullOrWhiteSpace(title) ? "セッション" : title,
            displayMode: displayMode, stagePane: stagePane, paneLayout: paneLayout);
    }

    public void RecordLayout(string layoutKey, string label,
        DisplayMode displayMode, PaneKind? stagePane, string? paneLayout)
        => Record(TrailEntryKind.Layout, layoutKey, label,
            displayMode: displayMode, stagePane: stagePane, paneLayout: paneLayout);

    /// <summary>全軌跡ソース共通の記録入口。</summary>
    public void Record(TrailEntryKind kind, string target, string label, int line = -1, int column = -1,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
    {
        if (string.IsNullOrWhiteSpace(target))
            return;

        var result = _recorder.Record(new TrailRecordRequest(
            kind, target, label, line, column, displayMode, stagePane, paneLayout));
        if (result is null) return;
        if (result.LiveDayChanged && _followingToday)
        {
            SetCurrent(-1); Entries.Clear(); DisplayDate = Today; OnEntriesChanged();
        }
        HasEntries = true;
        if (IsShowingToday())
        {
            if (result.Added) Entries.Add(result.Entry);
            SetCurrent(Entries.IndexOf(result.Entry));
            OnEntriesChanged();
        }
    }

    /// <summary>時間帯と時刻表示を現在のエントリから再構築する。</summary>
    private void OnEntriesChanged()
    {
        RecomputeHourBands();
        RebuildHours();
        OnPropertyChanged(nameof(HourLabel));
        OnPropertyChanged(nameof(DateTimeLabel));
        OnPropertyChanged(nameof(HasHours));
    }

    private void RecomputeHourBands()
    {
        for (var i = 0; i < Entries.Count; i++)
            Entries[i].StartsNewHour = i > 0 && Entries[i].Timestamp.Hour != Entries[i - 1].Timestamp.Hour;
    }

    private void RebuildHours()
    {
        var hours = Entries.Select(e => e.Timestamp.Hour).Distinct().OrderBy(h => h).ToList();
        Hours.Clear();
        foreach (var hour in hours)
            Hours.Add(new TrailHourViewModel(hour));
        UpdateHourSelection();   // 作り直した項目へ現在地の選択を貼り直す
    }

    public void RefreshHourLabel()
    {
        OnPropertyChanged(nameof(HourLabel));
        OnPropertyChanged(nameof(DateTimeLabel));
    }

    /// <summary>選択時間帯の先頭を現在地にする。</summary>
    public void SelectHour(TrailHourViewModel? hour)
    {
        if (hour is null)
            return;
        for (var i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].Timestamp.Hour == hour.Hour)
            {
                SetCurrent(i);
                return;
            }
        }
    }

    /// <summary>最新ファイル地点の離脱位置を更新する。</summary>
    public void UpdateLatestFilePosition(string path, int line, int column)
    {
        _recorder.UpdateLatestFilePosition(path, line, column);
    }

    /// <summary>最新地点のペイン配置を同期する。</summary>
    public void UpdateLatestPaneLayout(string? paneLayout)
    {
        _recorder.UpdateLatestPaneLayout(paneLayout);
    }

    public string? LatestFileTarget =>
        _recorder.LatestFileTarget;

    /// <summary>現在地を前後に動かし、移動後のエントリを返す。</summary>
    public TrailEntryViewModel? MoveCurrent(int delta)
    {
        if (Entries.Count == 0)
            return null;
        var next = Math.Clamp((CurrentIndex < 0 ? Entries.Count - 1 : CurrentIndex) + delta, 0, Entries.Count - 1);
        if (next == CurrentIndex)
            return null;   // 端で止まった＝移動なし（余計な再ジャンプをしない）
        SetCurrent(next);
        return Entries[next];
    }

    /// <summary>現在地を最新地点へ動かす。</summary>
    public TrailEntryViewModel? MoveToLatest()
    {
        if (Entries.Count == 0)
            return null;
        SetCurrent(Entries.Count - 1);
        return CurrentEntry;
    }

    private void SetCurrent(int index)
    {
        if (CurrentIndex == index)
            return;
        if (CurrentEntry is { } old)
            old.IsCurrent = false;
        CurrentIndex = index;
        if (CurrentEntry is { } entry)
            entry.IsCurrent = true;
        UpdateHourSelection();
    }

    /// <summary>時刻ポップアップのリストで、現在地が属する時間帯の項目だけを選択状態にする
    /// （どの時間帯を見ているのかをリスト上で示す、§27.7.2）。現在地の変化・時間帯一覧の再構築で呼ぶ。</summary>
    private void UpdateHourSelection()
    {
        var hour = CurrentEntry?.Timestamp.Hour;
        foreach (var h in Hours)
            h.IsSelected = hour.HasValue && h.Hour == hour.Value;
    }

    // ===== 日付の切替（過去の軌跡を追う） =====

    public void ShowDate(DateOnly day)
    {
        if (day == DisplayDate)
            return;
        try
        {
            LoadInto(day);
        }
        catch
        {
            // 読めなければ表示を変えない
        }
    }

    [RelayCommand]
    private void BackToToday() => ShowDate(Today);

    private void LoadInto(DateOnly day)
    {
        var records = _history.LoadDay(_workspaceKey, day);
        SetCurrent(-1);
        Entries.Clear();
        foreach (var entry in records) Entries.Add(entry);
        DisplayDate = day;
        var today = Today;
        SetFollowingToday(day == today);
        OnEntriesChanged();
        if (Entries.Count > 0)
            SetCurrent(Entries.Count - 1);
        // 今日に戻ったら、以後の記録が表示へも反映されるようデデュープ対象を差し替える。
        if (_followingToday)
        {
            _recorder.SetLatest(today, Entries.Count > 0 ? Entries[^1] : null);
        }
    }

    private bool IsShowingToday() => _followingToday;

    private void SetFollowingToday(bool value)
    {
        if (_followingToday == value)
            return;
        _followingToday = value;
        OnPropertyChanged(nameof(IsViewingPast));
    }

    private static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : url;

    [RelayCommand]
    private void Jump(TrailEntryViewModel? entry)
    {
        if (entry is null)
            return;
        SetCurrent(Entries.IndexOf(entry));
        JumpRequested?.Invoke(this, entry);
    }
}
