using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Ai;
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
public sealed partial class TrailHourViewModel : ObservableObject
{
    public TrailHourViewModel(int hour) => Hour = hour;

    /// <summary>0〜23 の時。</summary>
    public int Hour { get; }

    /// <summary>ポップアップに縦並びで出す表示（HH:00）。</summary>
    public string Label => $"{Hour:D2}:00";

    /// <summary>いま現在地が属する時間帯か（ポップアップのリストでこの項目を選択状態に強調する）。
    /// <see cref="TrailViewModel"/> が現在地の変化に合わせて更新する（§27.7.2）。</summary>
    [ObservableProperty] private bool _isSelected;
}

