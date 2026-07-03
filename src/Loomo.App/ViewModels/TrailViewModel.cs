using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>軌跡（操作ログ）エントリの種別。値は SQLite へ int で永続化されるため末尾追加のみ可。</summary>
public enum TrailEntryKind
{
    /// <summary>エディタで開いた（アクティブにした）ファイル。</summary>
    File,
    /// <summary>ブラウザで表示したページ。</summary>
    Browser,
    /// <summary>メイン領域のペイン切替（フォーカス移動）。</summary>
    Pane,
    /// <summary>サイドバーのパネル切替。</summary>
    Panel,
    /// <summary>アクティブにしたターミナルタブ。target はワークスペース内で永続化されるタブ ID。</summary>
    Terminal,
    /// <summary>ペイン配置、表示モード、またはソロで舞台に立つペインの変更。</summary>
    Layout
}

/// <summary>軌跡の1エントリ＝一度通過した地点。バーには点（ドット）で表示し、
/// ホバーで詳細（種別・対象・日時）、クリック／ホイールでその地点へ戻る。</summary>
public sealed partial class TrailEntryViewModel : ObservableObject
{
    public TrailEntryViewModel(long id, TrailEntryKind kind, string target, string label, DateTime timestamp,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
    {
        Id = id;
        Kind = kind;
        Target = target;
        _label = label;
        _timestamp = timestamp;
        Mode = displayMode;
        StagePane = stagePane;
        PaneLayout = paneLayout;
    }

    /// <summary>SQLite の行 id（永続化に失敗したメモリ内エントリは -1）。</summary>
    public long Id { get; }

    public TrailEntryKind Kind { get; }

    /// <summary>この地点を記録した表示モード。ジャンプ時に対象より先に復元する。</summary>
    public DisplayMode Mode { get; }

    /// <summary>ソロモードで舞台に立っていたペイン。レイアウトモードでは null。</summary>
    public PaneKind? StagePane { get; }

    /// <summary>この地点でのペイン配置JSON。モード・対象と同じ1件のログに含める。</summary>
    public string? PaneLayout { get; internal set; }

    /// <summary>戻り先の実体（ファイル＝フルパス、ブラウザ＝URL、ペイン／パネル＝enum 名）。</summary>
    public string Target { get; }

    /// <summary>ホバー詳細に出す短い名前（ファイル名／ページタイトル／ペイン名）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private string _label;

    /// <summary>記録時のカーソル行（0始まり。位置情報が無ければ -1）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private int _line = -1;

    /// <summary>記録時のカーソル桁（0始まり。位置情報が無ければ -1）。</summary>
    [ObservableProperty] private int _column = -1;

    /// <summary>最後にこの地点を通過した日時。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private DateTime _timestamp;

    /// <summary>軌跡上の現在地か（ドットを強調表示する）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private bool _isCurrent;

    /// <summary>バー上とツールチップに出す種別記号。すべて単色の幾何記号（絵文字は使わない）で、
    /// 前景色を継承できるためテーマ追従と現在地の色強調が効く。種別ごとに一意（§27.11-D）。</summary>
    public string Glyph => Kind switch
    {
        TrailEntryKind.Browser => "◉",
        TrailEntryKind.Pane => "▦",
        TrailEntryKind.Panel => "◫",
        TrailEntryKind.Terminal => "❯",
        TrailEntryKind.Layout => "⊞",
        _ => "◆"   // File
    };

    /// <summary>スクリーンリーダー／UI Automation がドットの内容と状態を識別するための名前。</summary>
    public string AccessibleName
    {
        get
        {
            var kind = Kind switch
            {
                TrailEntryKind.File => "ファイル",
                TrailEntryKind.Browser => "ブラウザ",
                TrailEntryKind.Pane => "ペイン",
                TrailEntryKind.Panel => "パネル",
                TrailEntryKind.Terminal => "ターミナル",
                TrailEntryKind.Layout => "レイアウト",
                _ => "地点"
            };
            var name = Kind == TrailEntryKind.File && Line >= 0 ? $"{Label}、{Line + 1}行" : Label;
            var current = IsCurrent ? "、現在地" : string.Empty;
            return $"軌跡、{kind}、{name}、{Timestamp:HH:mm:ss}{current}";
        }
    }

    /// <summary>ホバーで出す詳細。1行目＝種別と名前、2行目＝対象の実体、3行目＝日時。</summary>
    public string Tooltip
    {
        get
        {
            var name = Kind == TrailEntryKind.File && Line >= 0 ? $"{Label}:{Line + 1}" : Label;
            var location = Kind switch
            {
                TrailEntryKind.File when Line >= 0 => $"{Target}:{Line + 1}",
                TrailEntryKind.File or TrailEntryKind.Browser => Target,
                _ => null
            };
            var body = location is null ? $"{Glyph} {name}" : $"{Glyph} {name}\n{location}";
            var mode = Mode == DisplayMode.Solo
                ? $"ソロ · {StagePane?.ToString() ?? "不明"}"
                : "レイアウト";
            return $"{body}\n{mode}\n{Timestamp:yyyy-MM-dd HH:mm:ss}";
        }
    }
}

/// <summary>ウィンドウ最下部の「軌跡」バー。エディタ・ブラウザ・ペイン／パネル切替の遷移を
/// 時系列の点（ドット）で並べ、ホバーで詳細、クリック／バー上のホイールでその地点へ戻る。
/// 記録は SQLite（<see cref="TrailStore"/>）へ日別・無制限に永続化し、バー左端の日付クリック
/// →カレンダーで過去の日の軌跡も遡れる（×で今日へ戻る）。
/// アイデア.md「Semantic Depth」構想の Thread Rail の種。実際の遷移（タブ活性化・NavigateTo・
/// ペインフォーカス等）は <see cref="JumpRequested"/> を受けた ShellWindow（ShellWindow.Trail.cs）が行う。</summary>
public sealed partial class TrailViewModel : ObservableObject
{
    private readonly TrailStore _store;
    private readonly Func<DateTime> _now;
    private bool _loaded;

    /// <summary>表示が「今日（ライブ）」を追従しているか。過去日をカレンダーで選ぶと false、
    /// 今日へ戻すと true。日跨ぎ判定に <see cref="DisplayDate"/> と <c>Today</c> の一致は使わない
    /// （深夜0時を越えると一致が崩れ、追従中なのに過去表示扱いになってしまうため）。</summary>
    private bool _followingToday = true;

    /// <summary>いま記録が積まれている論理的な「今日」。<see cref="_todayLatest"/> はこの日に属する。
    /// 実行したまま日付を跨いだら、次の記録でこの日を新しい今日へ繰り上げてデデュープを仕切り直す。</summary>
    private DateOnly _liveDay;

    /// <summary>現在のワークスペースのキー（WorkspaceSnapshot.Id 文字列。未オープンのスクラッチは空文字）。
    /// 記録・表示ともこのワークスペースの軌跡だけを扱う（混ざると別ワークスペースのファイルへ
    /// 飛べてしまい破綻する）。</summary>
    private string _workspaceKey = "";

    /// <summary>今日の最新エントリ（デデュープと離脱位置上書きの対象）。過去日を表示中でも
    /// 記録は常に今日へ積むため、表示リストとは別に保持する。</summary>
    private TrailEntryViewModel? _todayLatest;

    public TrailViewModel(TrailStore store, Func<DateTime>? clock = null)
    {
        _store = store;
        _now = clock ?? (() => DateTime.Now);
        _displayDate = Today;
        _liveDay = Today;
    }

    /// <summary>表示中の日のエントリ（過去日表示中は読み取り専用の履歴）。</summary>
    public ObservableCollection<TrailEntryViewModel> Entries { get; } = new();

    /// <summary>ドットのクリック／ホイール移動で、その地点へ戻りたい。</summary>
    public event EventHandler<TrailEntryViewModel>? JumpRequested;

    /// <summary>バー自体の表示切替（記録が何も無ければバーごと隠して高さを取らない）。</summary>
    [ObservableProperty] private bool _hasEntries;

    /// <summary>表示中の日（記録は常に今日へ積まれる）。IsViewingPast は追従状態
    /// （<see cref="_followingToday"/>）で決まるため、ここからは通知しない。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DateLabel))]
    private DateOnly _displayDate;

    /// <summary>軌跡上の現在地（表示中エントリのインデックス。-1 は無し）。</summary>
    [ObservableProperty] private int _currentIndex = -1;

    private DateOnly Today => DateOnly.FromDateTime(_now());

    public bool IsViewingPast => !_followingToday;

    /// <summary>バー左端の日付表示。クリックでカレンダーを開く。</summary>
    public string DateLabel => DisplayDate.ToString("M/d (ddd)");

    public TrailEntryViewModel? CurrentEntry =>
        CurrentIndex >= 0 && CurrentIndex < Entries.Count ? Entries[CurrentIndex] : null;

    /// <summary>今日の軌跡を SQLite から読み込む（起動クリティカルパスを避けるため遅延で呼ぶ）。</summary>
    public void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;
        ReloadForWorkspace();
    }

    /// <summary>ワークスペース切替に追従する。表示を今日へ戻し、そのワークスペースの軌跡だけを出す。
    /// 読込前（起動中）はキーの記憶だけで済ませ、EnsureLoaded が読む。</summary>
    public void SetWorkspace(string workspaceKey)
    {
        if (string.Equals(_workspaceKey, workspaceKey, StringComparison.Ordinal))
            return;
        _workspaceKey = workspaceKey;
        _todayLatest = null;
        if (_loaded)
            ReloadForWorkspace();
    }

    private void ReloadForWorkspace()
    {
        try
        {
            LoadInto(Today);
            _todayLatest = Entries.Count > 0 ? Entries[^1] : null;
            HasEntries = Entries.Count > 0 || _store.HasAny(_workspaceKey);
        }
        catch
        {
            // DB が読めなくてもメモリ内動作で続行する（以後の記録も best-effort）。
        }
    }

    // ===== 記録（常に「今日」へ積む） =====
    //
    // 記録は種別（TrailEntryKind）を問わず、この Record 一本に集約する。新しい軌跡ソースを
    // 足すときは「enum 値を1つ追加 → 記録したい場所で Record(kind, target, label, …) を呼ぶ」
    // だけでよい（デデュープ・永続化・現在地更新・過去日表示の扱いは共通で効く）。以下の
    // RecordFile/RecordBrowser/RecordPane/RecordPanel は target/label の作り方が決まっている
    // 既存ソース向けの薄い糖衣で、内部はすべて Record に流れる。

    /// <summary>ファイル地点を記録する。直前と同じファイルなら追記せず位置・時刻だけ更新する
    /// （タブ切替の往復やフォーカス移動でドットが増殖しないように）。</summary>
    public void RecordFile(string path, int line = -1, int column = -1,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        Record(TrailEntryKind.File, path, Path.GetFileName(path), line, column, displayMode, stagePane, paneLayout);
    }

    /// <summary>ブラウザ地点を記録する。直前と同じ URL ならタイトル・時刻だけ更新する
    /// （NavigationCompleted 時点ではタイトル未確定のことがあるため、後追いで整う）。</summary>
    public void RecordBrowser(string url, string? title,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        var label = string.IsNullOrWhiteSpace(title) ? HostOf(url) : title.Trim();
        Record(TrailEntryKind.Browser, url, label, displayMode: displayMode, stagePane: stagePane,
            paneLayout: paneLayout);
    }

    /// <summary>ペイン切替（フォーカス移動）を記録する。target は PaneKind の enum 名。</summary>
    public void RecordPane(string paneKindName, string label,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
        => Record(TrailEntryKind.Pane, paneKindName, label, displayMode: displayMode, stagePane: stagePane,
            paneLayout: paneLayout);

    /// <summary>サイドバーのパネル切替を記録する。target は SidebarPanel の enum 名。</summary>
    public void RecordPanel(string panelName, string label,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
        => Record(TrailEntryKind.Panel, panelName, label, displayMode: displayMode, stagePane: stagePane,
            paneLayout: paneLayout);

    /// <summary>ターミナルタブの活性化を記録する。target は再起動後も維持されるタブ ID。</summary>
    public void RecordTerminal(Guid tabId, string label,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
        => Record(TrailEntryKind.Terminal, tabId.ToString("D"), label,
            displayMode: displayMode, stagePane: stagePane, paneLayout: paneLayout);

    /// <summary>表示レイアウトの変更を、それ自体が戻り先になる独立した地点として記録する。</summary>
    public void RecordLayout(string layoutKey, string label,
        DisplayMode displayMode, PaneKind? stagePane, string? paneLayout)
        => Record(TrailEntryKind.Layout, layoutKey, label,
            displayMode: displayMode, stagePane: stagePane, paneLayout: paneLayout);

    /// <summary>あらゆる軌跡ソース共通の記録入口。直前と同一地点（同一 kind かつ同一 target）の
    /// 再通過はドットを増やさず時刻・ラベル・位置だけ上書きし、それ以外は新しい点を積む。
    /// target の同一判定はファイルだけ大文字小文字を無視（Windows のパス）、他は完全一致。</summary>
    public void Record(TrailEntryKind kind, string target, string label, int line = -1, int column = -1,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
    {
        if (string.IsNullOrWhiteSpace(target))
            return;

        var now = _now();
        RollLiveDayIfNeeded(DateOnly.FromDateTime(now));
        var comparison = kind == TrailEntryKind.File
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // 直前と同一地点の再通過はドットを増やさず、その行の時刻・ラベル・位置を上書きする。
        if (_todayLatest is { } last && last.Kind == kind
            && string.Equals(last.Target, target, comparison)
            && last.Mode == displayMode && last.StagePane == stagePane)
        {
            last.Label = label;
            last.Timestamp = now;
            last.PaneLayout = paneLayout;
            if (line >= 0)
            {
                last.Line = line;
                last.Column = column;
            }
            if (last.Id >= 0)
                Try(() => _store.Update(last.Id, now, last.Label, last.Line, last.Column, paneLayout));
            if (IsShowingToday())
                SetCurrent(Entries.IndexOf(last));
            return;
        }

        long id = -1;
        Try(() => id = _store.Append(_workspaceKey, now, (int)kind, target, label, line, column,
            displayMode, stagePane, paneLayout));

        var entry = new TrailEntryViewModel(id, kind, target, label, now, displayMode, stagePane, paneLayout)
            { Line = line, Column = column };
        _todayLatest = entry;
        HasEntries = true;

        if (IsShowingToday())
        {
            Entries.Add(entry);
            SetCurrent(Entries.Count - 1);
        }
    }

    /// <summary>今日の最新エントリがこのファイルなら、離脱時のカーソル位置で上書きする
    /// （新しい地点を積む直前に呼ぶ。「戻る」が到着時でなく離れた時の場所になる）。</summary>
    public void UpdateLatestFilePosition(string path, int line, int column)
    {
        if (line < 0
            || _todayLatest is not { Kind: TrailEntryKind.File } latest
            || !string.Equals(latest.Target, path, StringComparison.OrdinalIgnoreCase))
            return;

        latest.Line = line;
        latest.Column = column;
        if (latest.Id >= 0)
            Try(() => _store.UpdatePosition(latest.Id, line, column));
    }

    /// <summary>現在地点から離れる前、またはワークスペース状態の保存時に、最新地点のペイン配置を
    /// 現在値へ同期する。配置変更後にタブ等を切り替えなくても、戻り先が古い配置のまま残らない。</summary>
    public void UpdateLatestPaneLayout(string? paneLayout)
    {
        if (_todayLatest is not { } latest
            || string.Equals(latest.PaneLayout, paneLayout, StringComparison.Ordinal))
            return;

        latest.PaneLayout = paneLayout;
        if (latest.Id >= 0)
            Try(() => _store.UpdatePaneLayout(latest.Id, paneLayout));
    }

    /// <summary>今日の最新エントリがファイルならそのフルパス（離脱位置の上書き対象の特定用）。</summary>
    public string? LatestFileTarget =>
        _todayLatest is { Kind: TrailEntryKind.File } latest ? latest.Target : null;

    // ===== 現在地の移動（バー上のホイール＝スクラブ） =====

    /// <summary>現在地を前後に動かし、移動後のエントリを返す（端で止まる）。
    /// 実際のジャンプは呼び出し側（ShellWindow）が少し遅らせて行う（連続ホイールを1回に畳む）。</summary>
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

    private void SetCurrent(int index)
    {
        if (CurrentIndex == index)
            return;
        if (CurrentEntry is { } old)
            old.IsCurrent = false;
        CurrentIndex = index;
        if (CurrentEntry is { } entry)
            entry.IsCurrent = true;
    }

    // ===== 日付の切替（過去の軌跡を追う） =====

    /// <summary>カレンダーで選んだ日の軌跡を表示する（記録は引き続き今日へ積まれる）。</summary>
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

    /// <summary>×ボタン：今日の軌跡へ戻る。</summary>
    [RelayCommand]
    private void BackToToday() => ShowDate(Today);

    private void LoadInto(DateOnly day)
    {
        var records = _store.LoadDay(_workspaceKey, day);
        SetCurrent(-1);
        Entries.Clear();
        foreach (var r in records)
        {
            Entries.Add(new TrailEntryViewModel(r.Id, (TrailEntryKind)r.Kind, r.Target, r.Label, r.Timestamp,
                r.DisplayMode, r.StagePane, r.PaneLayout)
            {
                Line = r.Line,
                Column = r.Column
            });
        }
        DisplayDate = day;
        var today = Today;
        SetFollowingToday(day == today);
        if (Entries.Count > 0)
            SetCurrent(Entries.Count - 1);
        // 今日に戻ったら、以後の記録が表示へも反映されるようデデュープ対象を差し替える。
        if (_followingToday)
        {
            _liveDay = today;
            _todayLatest = Entries.Count > 0 ? Entries[^1] : null;
        }
    }

    /// <summary>表示が今日（ライブ）を追従しているか。過去日表示中は false。</summary>
    private bool IsShowingToday() => _followingToday;

    private void SetFollowingToday(bool value)
    {
        if (_followingToday == value)
            return;
        _followingToday = value;
        OnPropertyChanged(nameof(IsViewingPast));
    }

    /// <summary>実行したまま日付を跨いだら、論理的な「今日」を繰り上げる。追従中なら表示も新しい
    /// 空の今日へ切り替え、デデュープ対象（<see cref="_todayLatest"/>）を仕切り直す。過去日を
    /// 表示中なら表示は触らない（記録は新しい日として DB へ積まれる）。</summary>
    private void RollLiveDayIfNeeded(DateOnly today)
    {
        if (today == _liveDay)
            return;
        _liveDay = today;
        _todayLatest = null;   // 新しい日：昨日の最新地点とはデデュープしない
        if (_followingToday && DisplayDate != today)
        {
            SetCurrent(-1);
            Entries.Clear();
            DisplayDate = today;
        }
    }

    private static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : url;

    /// <summary>永続化は best-effort（DB 破損等でも軌跡バー自体は動き続ける）。</summary>
    private static void Try(Action action)
    {
        try { action(); }
        catch { }
    }

    [RelayCommand]
    private void Jump(TrailEntryViewModel? entry)
    {
        if (entry is null)
            return;
        SetCurrent(Entries.IndexOf(entry));
        JumpRequested?.Invoke(this, entry);
    }
}
