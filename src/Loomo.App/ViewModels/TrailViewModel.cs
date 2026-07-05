using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
    Layout,
    /// <summary>EditorSupport（プレビュー）ペインで表示したファイル。target はプレビュー元ファイルのフルパス
    /// （ペインではなくプレビュー対象を戻り先にするので、戻ると同じファイルのプレビューが開き直す）。</summary>
    Preview,
    /// <summary>アクティブにした AI 会話セッション。target は保存済みセッションの ID で、
    /// 戻るとそのセッションを復元して AI ペインを開き直す。</summary>
    Session,
    /// <summary>エディタでファイルを編集した地点。target は編集したファイルのフルパス、line は
    /// 編集時のカーソル行。内容は復元せず、戻ると同じ file:line を開き直すだけ（ファイル地点と同じ扱い）。</summary>
    Edit,
    /// <summary>Git 操作（コミット・プッシュ・ブランチ切替など）。target は操作の種別キー、label は
    /// 表示名。ログ専用で戻り先を持たない（クリックしても復元しない）。</summary>
    Git
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
    [NotifyPropertyChangedFor(nameof(HourBandLabel))]
    private DateTime _timestamp;

    /// <summary>この地点が属する時間帯の開始時刻（HH:00）。バーでは時間帯の先頭ドット
    /// （<see cref="StartsNewHour"/>）の区切りに、区切り線と一緒に出す（§27.7.3）。</summary>
    public string HourBandLabel => $"{Timestamp.Hour:D2}:00";

    /// <summary>軌跡上の現在地か（ドットを強調表示する）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private bool _isCurrent;

    /// <summary>この地点が直前の地点と別の「時間帯（時）」に入る先頭か。バーでは時間帯の
    /// 境目に細い区切り（|）を1本入れるために使う（§27.7.3）。先頭エントリは常に false。</summary>
    [ObservableProperty] private bool _startsNewHour;

    /// <summary>バー上とツールチップに出す種別記号。すべて単色の幾何記号（絵文字は使わない）で、
    /// 前景色を継承できるためテーマ追従と現在地の色強調が効く。種別ごとに一意（§27.11-D）。</summary>
    public string Glyph => Kind switch
    {
        TrailEntryKind.Browser => "◉",
        TrailEntryKind.Pane => "▦",
        TrailEntryKind.Panel => "◫",
        TrailEntryKind.Terminal => "❯",
        TrailEntryKind.Layout => "⊞",
        TrailEntryKind.Preview => "◈",
        TrailEntryKind.Session => "✦",
        TrailEntryKind.Edit => "◇",
        TrailEntryKind.Git => "❖",
        _ => "◆"   // File
    };

    /// <summary>バーに描く単色ベクターアイコンのジオメトリ（WPF パスミニ言語・16×16 座標）。
    /// <c>Path.Data</c> に文字列でバインドすると型コンバータで <c>Geometry</c> に変換される。
    /// 抽象記号の <see cref="Glyph"/>（ツールチップ・種別一意性テスト用に併存）と違い、
    /// 「書類・鉛筆・地球儀・ブランチ」等の絵姿で “何の地点か” をパッと見で示す。線画（塗り無し）
    /// で統一し、Stroke に前景ブラシを張ってテーマ追従・現在地の色強調を効かせる（§27.11-D）。
    /// 種別を足すときは <see cref="Glyph"/> と対で1本追加する。</summary>
    public string IconGeometry => Kind switch
    {
        // ブラウザ：地球儀（円＋赤道＋経線）
        TrailEntryKind.Browser => "M2.4,8 A5.6,5.6 0 1 0 13.6,8 A5.6,5.6 0 1 0 2.4,8 Z M2.4,8 L13.6,8 " +
            "M8,2.4 A2.7,5.6 0 0 0 8,13.6 A2.7,5.6 0 0 0 8,2.4",
        // ペイン：ウィンドウを中央で二分割（フォーカス移動）
        TrailEntryKind.Pane => "M2,3 L14,3 L14,13 L2,13 Z M8,3 L8,13",
        // パネル：サイドバー（左に細い列＋項目線）
        TrailEntryKind.Panel => "M2,3 L14,3 L14,13 L2,13 Z M5.6,3 L5.6,13 " +
            "M3.2,6 L4.6,6 M3.2,8 L4.6,8 M3.2,10 L4.6,10",
        // ターミナル：枠＋プロンプト（>_）
        TrailEntryKind.Terminal => "M2,3 L14,3 L14,13 L2,13 Z M4.8,6.4 L6.9,8.2 L4.8,10 M8.4,11 L11.4,11",
        // レイアウト：2×2 グリッド
        TrailEntryKind.Layout => "M2,2.4 L14,2.4 L14,13.6 L2,13.6 Z M8,2.4 L8,13.6 M2,8 L14,8",
        // プレビュー：目（読み取り専用の“見る”）
        TrailEntryKind.Preview => "M1.6,8 Q8,2.6 14.4,8 Q8,13.4 1.6,8 Z " +
            "M6.1,8 A1.9,1.9 0 1 0 9.9,8 A1.9,1.9 0 1 0 6.1,8 Z",
        // セッション：吹き出し（AI 会話）
        TrailEntryKind.Session => "M2.6,3.4 L13.4,3.4 L13.4,10 L7.2,10 L4.4,12.4 L4.4,10 L2.6,10 Z " +
            "M5.4,6 L10.6,6 M5.4,7.9 L8.8,7.9",
        // 編集：鉛筆
        TrailEntryKind.Edit => "M11,2.3 L13.7,5 L6.3,12.4 L3,13 L3.6,9.7 Z M10.4,2.9 L13.1,5.6",
        // Git：ブランチ（3ノード＋分岐）
        TrailEntryKind.Git => "M2.9,3.9 A1.7,1.7 0 1 0 6.3,3.9 A1.7,1.7 0 1 0 2.9,3.9 Z " +
            "M2.9,12.1 A1.7,1.7 0 1 0 6.3,12.1 A1.7,1.7 0 1 0 2.9,12.1 Z " +
            "M9.7,6.4 A1.7,1.7 0 1 0 13.1,6.4 A1.7,1.7 0 1 0 9.7,6.4 Z " +
            "M4.6,5.6 L4.6,10.4 M4.6,8 C7.8,8 8.4,6.9 10,6.7",
        // ファイル：書類（折れ角＋本文行）
        _ => "M4,1.8 L8.6,1.8 L12,5 L12,14.2 L4,14.2 Z M8.6,1.8 L8.6,5 L12,5 " +
            "M6,8.4 L10,8.4 M6,10.8 L10,10.8"
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
                TrailEntryKind.Preview => "プレビュー",
                TrailEntryKind.Session => "セッション",
                TrailEntryKind.Edit => "編集",
                TrailEntryKind.Git => "Git",
                _ => "地点"
            };
            var name = Kind is TrailEntryKind.File or TrailEntryKind.Edit && Line >= 0
                ? $"{Label}、{Line + 1}行"
                : Label;
            var current = IsCurrent ? "、現在地" : string.Empty;
            return $"軌跡、{kind}、{name}、{Timestamp:HH:mm:ss}{current}";
        }
    }

    /// <summary>ホバーで出す詳細。1行目＝種別と名前、2行目＝対象の実体、3行目＝日時。</summary>
    public string Tooltip
    {
        get
        {
            var name = Kind is TrailEntryKind.File or TrailEntryKind.Edit && Line >= 0
                ? $"{Label}:{Line + 1}"
                : Label;
            var location = Kind switch
            {
                TrailEntryKind.File or TrailEntryKind.Edit when Line >= 0 => $"{Target}:{Line + 1}",
                TrailEntryKind.File or TrailEntryKind.Edit or TrailEntryKind.Browser or TrailEntryKind.Preview => Target,
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

/// <summary>時刻ポップアップの1項目＝その日に記録のある「時間帯（時）」。ラベルは常に HH:00
/// （09:00 は 09:00〜09:59 を表す）。クリックでその時間帯の先頭ドットへ現在地を移す（§27.7.2）。</summary>
public sealed class TrailHourViewModel
{
    public TrailHourViewModel(int hour) => Hour = hour;

    /// <summary>0〜23 の時。</summary>
    public int Hour { get; }

    /// <summary>ポップアップに縦並びで出す表示（HH:00）。</summary>
    public string Label => $"{Hour:D2}:00";
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HourLabel))]
    private int _currentIndex = -1;

    /// <summary>時刻ポップアップに縦並びで出す「その日に記録のある時間帯」（HH:00、昇順）。</summary>
    public ObservableCollection<TrailHourViewModel> Hours { get; } = new();

    private DateOnly Today => DateOnly.FromDateTime(_now());

    public bool IsViewingPast => !_followingToday;

    /// <summary>バー左端の日付表示。クリックでカレンダーを開く。</summary>
    public string DateLabel => DisplayDate.ToString("M/d (ddd)");

    /// <summary>日付ボタンの右に出す時刻インジケータ。軌跡の最新地点（＝ライブの「今」）にいるときは
    /// 現在時刻を <c>HH:mm</c>（時計に追従）で、過去の地点へスクラブ中はその地点の時間帯を
    /// <c>HH:00</c>（＝HH:00〜HH:59 を表す）で示す（§27.7.2）。クリックで時間帯ポップアップを開く。</summary>
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

    /// <summary>エディタでの編集地点を記録する。target は編集したファイルのフルパス、line は編集時の
    /// カーソル行。パスの大文字小文字は無視し、<b>行ごとに1点</b>：同じ行を編集し続ける間は点を増やさず
    /// 時刻だけ更新し、同じファイルでも別の行を編集したら新しい点を積む（列は無視／内容は復元しない）。</summary>
    public void RecordEdit(string path, int line = -1, int column = -1,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        Record(TrailEntryKind.Edit, path, Path.GetFileName(path), line, column, displayMode, stagePane, paneLayout);
    }

    /// <summary>Git 操作をログとして記録する。target は操作種別キー（例: commit / push / checkout）で、
    /// 連続する同種の操作はデデュープで1点に畳む。復元は行わないので配置（paneLayout）は載せない。</summary>
    public void RecordGit(string operationKey, string label,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null)
    {
        if (string.IsNullOrWhiteSpace(operationKey))
            return;
        Record(TrailEntryKind.Git, operationKey, string.IsNullOrWhiteSpace(label) ? "Git" : label,
            displayMode: displayMode, stagePane: stagePane, paneLayout: null);
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

    /// <summary>プレビュー（EditorSupport）地点を記録する。target はプレビュー元ファイルのフルパスで、
    /// 戻ると同じファイルのプレビューを開き直す。ファイル地点と同じく大文字小文字を無視してデデュープする。</summary>
    public void RecordPreview(string path,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        Record(TrailEntryKind.Preview, path, Path.GetFileName(path),
            displayMode: displayMode, stagePane: stagePane, paneLayout: paneLayout);
    }

    /// <summary>AI 会話セッションのアクティブ化（復元・新規セッションの確定）を記録する。
    /// target は保存済みセッションの ID。戻るとそのセッションを復元する。</summary>
    public void RecordSession(string id, string title,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;
        Record(TrailEntryKind.Session, id, string.IsNullOrWhiteSpace(title) ? "セッション" : title,
            displayMode: displayMode, stagePane: stagePane, paneLayout: paneLayout);
    }

    /// <summary>表示レイアウトの変更を、それ自体が戻り先になる独立した地点として記録する。</summary>
    public void RecordLayout(string layoutKey, string label,
        DisplayMode displayMode, PaneKind? stagePane, string? paneLayout)
        => Record(TrailEntryKind.Layout, layoutKey, label,
            displayMode: displayMode, stagePane: stagePane, paneLayout: paneLayout);

    /// <summary>あらゆる軌跡ソース共通の記録入口。直前と同一地点（同一 kind かつ同一 target）の
    /// 再通過はドットを増やさず時刻・ラベル・位置だけ上書きし、それ以外は新しい点を積む。
    /// target の同一判定はファイルだけ大文字小文字を無視（Windows のパス）、他は完全一致。
    /// <b>Edit だけは行も同一判定に含める</b>：同じファイルでも別の行を編集したら新しい点を積み、
    /// 同じ行の連続編集の間だけ畳む（列は無視）。</summary>
    public void Record(TrailEntryKind kind, string target, string label, int line = -1, int column = -1,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
    {
        if (string.IsNullOrWhiteSpace(target))
            return;

        var now = _now();
        RollLiveDayIfNeeded(DateOnly.FromDateTime(now));
        // File・Edit・Preview は Windows のファイルパスなので大文字小文字を無視して同一判定する。
        var comparison = kind is TrailEntryKind.File or TrailEntryKind.Edit or TrailEntryKind.Preview
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // 直前と同一地点の再通過はドットを増やさず、その行の時刻・ラベル・位置を上書きする。
        // Edit は「同じ行の連続編集」だけを畳む（行が変われば別の点）ので、行も一致条件に含める。
        if (_todayLatest is { } last && last.Kind == kind
            && string.Equals(last.Target, target, comparison)
            && last.Mode == displayMode && last.StagePane == stagePane
            && (kind is not TrailEntryKind.Edit || last.Line == line))
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
            {
                SetCurrent(Entries.IndexOf(last));
                OnEntriesChanged();   // 時刻が動くと時間帯ラベル・境界が変わりうる
            }
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
            OnEntriesChanged();
        }
    }

    /// <summary>表示中エントリの並びが変わったら、時間帯の境界フラグ（<see cref="TrailEntryViewModel.StartsNewHour"/>）と
    /// 時刻ポップアップの時間帯一覧を作り直し、時刻ラベルの再評価を促す。追加・削除・読込・
    /// デデュープ更新（時刻が動く）で呼ぶ。</summary>
    private void OnEntriesChanged()
    {
        RecomputeHourBands();
        RebuildHours();
        OnPropertyChanged(nameof(HourLabel));
    }

    /// <summary>隣り合う地点で「時（Hour）」が変わる先頭に印を付ける。バーはここへ区切り（|）を1本出す。</summary>
    private void RecomputeHourBands()
    {
        for (var i = 0; i < Entries.Count; i++)
            Entries[i].StartsNewHour = i > 0 && Entries[i].Timestamp.Hour != Entries[i - 1].Timestamp.Hour;
    }

    /// <summary>表示中の日に記録のある時間帯（時）を昇順・重複なしで一覧化する（時刻ポップアップ用）。</summary>
    private void RebuildHours()
    {
        var hours = Entries.Select(e => e.Timestamp.Hour).Distinct().OrderBy(h => h).ToList();
        Hours.Clear();
        foreach (var hour in hours)
            Hours.Add(new TrailHourViewModel(hour));
    }

    /// <summary>時計の進行に合わせてライブの時刻ラベルを更新する（View のタイマから定期的に呼ぶ）。</summary>
    public void RefreshHourLabel() => OnPropertyChanged(nameof(HourLabel));

    /// <summary>時刻ポップアップで選んだ時間帯の先頭ドットを現在地にする。画面復元（ジャンプ）はせず、
    /// バー内のナビゲーションに留める（日付ポップアップが「その日を出す」だけなのと同じ位置づけ）。
    /// スクロール追従は呼び出し側（View）が行う。</summary>
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

    /// <summary>現在地を軌跡の最新地点（＝ライブでは「今」）へ動かし、その地点を返す（無ければ null）。
    /// 時刻表示のダブルクリックで、スクラブや時間帯選択で過去へ動いた現在地を素早く「今」へ戻す。</summary>
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
        OnEntriesChanged();
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
            OnEntriesChanged();
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
