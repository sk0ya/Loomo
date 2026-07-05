using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: 軌跡（操作ログ）バーの配線。エディタのファイル活性化・ブラウザ遷移・
/// ペイン／パネル切替を <see cref="TrailViewModel"/> へ記録し、ドットのクリックや
/// バー上のホイール（現在地の前後移動）でその地点へ戻る。バー左端の日付クリック→カレンダーで
/// 過去の日の軌跡も表示できる。アイデア.md「Semantic Depth」構想の Thread Rail の種。
///
/// <para><b>新しい軌跡ソースの足し方（登録側はこれだけ）</b>：
/// ①<see cref="TrailEntryKind"/> に enum 値を1つ追加し <c>Glyph</c> を1行足す。
/// ②<see cref="RegisterTrailJumps"/> にその種別の「戻る」処理を1行登録する。
/// ③記録したい場所（イベントハンドラ等）で <see cref="RecordTrail"/> を呼ぶ。
/// 記録の抑制（復元・ジャンプ中）と離脱位置の上書きは <see cref="RecordTrail"/> が共通で面倒を見るので、
/// 各ソースはこの3点以外を書かなくてよい。</para></summary>
public partial class ShellWindow
{
    /// <summary>種別ごとの「その地点へ戻る」処理。<see cref="RegisterTrailJumps"/> で一度だけ組み立て、
    /// <see cref="JumpToTrailEntry"/> がここを引いて呼ぶ（記録抑制の with を共通で被せる）。</summary>
    private readonly Dictionary<TrailEntryKind, Func<TrailEntryViewModel, Task>> _trailJumps = new();

    /// <summary>Git 操作を軌跡へログ記録するために購読する Git サービス（コンストラクタで注入）。</summary>
    private readonly sk0ya.Loomo.Services.GitService _git;

    /// <summary>true の間は軌跡へ記録しない。ワークスペース切替・復元による機械的なタブ活性化と、
    /// 軌跡からの「戻る」自体（戻った先を新しい地点として積まない）で立てる。</summary>
    private bool _trailSuppressed;

    /// <summary>直近に記録したペイン（同じペイン内のフォーカス移動でドットを増やさない）。</summary>
    private PaneKind? _trailLastPane;
    private DisplayMode? _trailLastPaneMode;

    /// <summary>ペイン切替の確定待ち（フォーカス奪い合いノイズを1個に畳むデバウンス）。</summary>
    private DispatcherTimer? _trailPaneCommitTimer;
    private PaneKind? _trailPendingPane;

    /// <summary>編集地点の確定待ち（1打鍵ごとに点を積まないよう、編集が一段落してから1個に畳むデバウンス）。</summary>
    private DispatcherTimer? _trailEditCommitTimer;
    private EditorTab? _trailPendingEditTab;

    /// <summary>現在地を表示領域の<b>左端</b>へ寄せている（時間帯選択・「今」へのダブルクリック）間 true。
    /// この間に軌跡の登録が入っても中央寄せへ戻さず左寄せを保ち、左端の地点が右へ動かないようにする。
    /// 中央寄せ（<see cref="ScrollTrailCurrentIntoView"/>）を通ると false へ戻る。</summary>
    private bool _trailSnapLeft;

    /// <summary>ホイールでの現在地移動を1回のジャンプへ畳むデバウンス。</summary>
    private DispatcherTimer? _trailScrubTimer;
    private TrailEntryViewModel? _trailScrubTarget;
    private TrailEntryViewModel? _trailPendingJumpEntry;
    private bool _trailJumpRunning;
    private string? _trailLastLayoutKey;

    private void InitializeTrail()
    {
        RegisterTrailJumps();
        _vm.Trail.JumpRequested += (_, entry) => JumpToTrailEntry(entry);
        // AI セッションのアクティブ化（復元・新規確定）を軌跡へ地点として記録する。
        _vm.AiBar.SessionActivated += (_, e) => RecordTrailSession(e.Id, e.Title);
        // Git の変更系操作（コミット・プッシュ・ブランチ切替など）をログとして記録する。
        // MutateAsync はバックグラウンドスレッドで走るので UI スレッドへ回してから記録する。
        _git.OperationExecuted += (_, e) =>
            Dispatcher.BeginInvoke(new Action(() => RecordTrailGit(e.Command, e.Success)));
        // 追記・現在地移動でドットが見える位置へ追従スクロールする。左端へ寄せている間は左寄せを保つ。
        _vm.Trail.Entries.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(ScrollTrailAfterEntriesChanged), DispatcherPriority.Loaded);
        // 日付・時刻クリックのトグル判定用：StaysOpen=False のポップアップは「開いたままボタンを再クリック」
        // すると Click が届く前に外側クリックとして閉じるため、閉じた時刻を覚えて直後の再オープンを抑止する。
        TrailCalendarPopup.Closed += (_, _) => _trailCalendarClosedAt = DateTime.UtcNow;
        TrailHourPopup.Closed += (_, _) => _trailHourPopupClosedAt = DateTime.UtcNow;
        // ライブの時刻ラベル（HH:mm）を時計の進みに合わせて更新する。過去地点表示中は HH:00 固定なので無害。
        _trailHourTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _trailHourTicker.Tick += (_, _) => _vm.Trail.RefreshHourLabel();
        _trailHourTicker.Start();
        // 起動のクリティカルパスを避けて、今日の軌跡を SQLite から遅延読込する。
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => _vm.Trail.EnsureLoaded()));
    }

    /// <summary>カレンダーポップアップが最後に閉じた時刻（日付クリックのトグル判定）。</summary>
    private DateTime _trailCalendarClosedAt;

    /// <summary>時刻ポップアップが最後に閉じた時刻（時刻クリックのトグル判定）。</summary>
    private DateTime _trailHourPopupClosedAt;

    /// <summary>ライブの時刻ラベルを定期更新するタイマ。</summary>
    private DispatcherTimer? _trailHourTicker;

    // ===== 記録 =====

    /// <summary>あらゆる軌跡ソース共通の記録入口。記録抑制（復元・ジャンプ中）の判定と、
    /// 新しい地点を積む前に「いま離れるファイル」のカーソルを離脱位置へ上書きする処理を
    /// ここへ一本化する。各ソースは対象の抽出（ファイルパス・URL 等）だけを行い、
    /// <paramref name="record"/> で実際の <see cref="TrailViewModel"/> 呼び出しを渡す。</summary>
    private void RecordTrail(Action<DisplayMode, PaneKind?, string?> record)
    {
        if (_trailSuppressed)
            return;
        RefreshLatestTrailFilePosition();
        var mode = _stageActive ? DisplayMode.Solo : DisplayMode.Layout;
        var paneLayout = _root is null ? null : JsonSerializer.Serialize(ToSnapshot(_root), TrailLayoutJson);
        // 遅延ワークスペース保存より先に別地点へ移動しても、離れた地点へ現在の配置を残す。
        _vm.Trail.UpdateLatestPaneLayout(paneLayout);
        record(mode, _stageActive ? _stagePane : null, paneLayout);
    }

    /// <summary>レイアウト変更後に同じ地点へ留まるケース用。ワークスペース保存の共通入口から呼ぶ。</summary>
    private void RefreshLatestTrailPaneLayout()
    {
        if (_trailSuppressed)
            return;
        var paneLayout = _root is null ? null : JsonSerializer.Serialize(ToSnapshot(_root), TrailLayoutJson);
        _vm.Trail.UpdateLatestPaneLayout(paneLayout);
    }

    /// <summary>保存要求のたびに表示状態を比較し、実際に変わったレイアウトだけを独立した軌跡へ積む。
    /// 復元中も基準値は同期し、復元操作そのものは新しい点にしない。</summary>
    private void RecordTrailLayoutIfChanged()
    {
        var (layoutKey, mode, stagePane, paneLayout) = CurrentTrailLayoutState();
        // 最初の観測値は起動・ワークスペース復元後の基準。ユーザー操作ではない。
        if (_trailLastLayoutKey is null)
        {
            _trailLastLayoutKey = layoutKey;
            return;
        }
        if (string.Equals(_trailLastLayoutKey, layoutKey, StringComparison.Ordinal))
            return;

        _trailLastLayoutKey = layoutKey;
        if (_trailSuppressed)
            return;

        var label = mode == DisplayMode.Solo
            ? $"ソロ · {PaneDisplayName(stagePane ?? PaneKind.Editor)}"
            : "レイアウト変更";
        RecordTrail((recordMode, recordStagePane, layout) =>
            _vm.Trail.RecordLayout(layoutKey, label, recordMode, recordStagePane, layout));
    }

    /// <summary>ユーザーのレイアウト操作を始める直前の状態を基準値にする。
    /// 起動復元の非同期処理が完全に収束する時刻には依存しない。</summary>
    private void BeginTrailLayoutChange()
    {
        _trailLastLayoutKey = CurrentTrailLayoutState().Key;
    }

    /// <summary>Layout ドットの変更検出キー。表示モード・舞台ペイン・ペイン<b>構造</b>から作る。
    /// 比率（Weight）は <see cref="PaneLayoutTree.StructureSignature"/> で除外するのでリサイズでは増えない。
    /// モード（ソロ⇄レイアウト）と舞台ペインは含めるので、ソロモードで舞台のペインを切り替えると
    /// 独立した Layout ドットになる（<c>Mode</c>／<c>StagePane</c> を載せて戻り先の表示を復元する）。
    /// ステージ中のペイン切替は Pane ドットではなくこの Layout ドットが代表する（<see cref="RecordTrailPane"/>
    /// はステージ中は記録しない）。この保存 choke point 経由の判定はデバウンス・フォーカス競合が無く確実。</summary>
    private (string Key, DisplayMode Mode, PaneKind? StagePane, string? PaneLayout) CurrentTrailLayoutState()
    {
        var mode = _stageActive ? DisplayMode.Solo : DisplayMode.Layout;
        var stagePane = _stageActive ? _stagePane : (PaneKind?)null;
        var snapshot = _root is null ? null : ToSnapshot(_root);
        var paneLayout = snapshot is null ? null : JsonSerializer.Serialize(snapshot, TrailLayoutJson);
        var structure = snapshot is null ? "-" : PaneLayoutTree.StructureSignature(snapshot);
        var key = $"{(int)mode}|{stagePane?.ToString() ?? "-"}|{structure}";
        return (key, mode, stagePane, paneLayout);
    }

    /// <summary>エディタタブの活性化を軌跡へ記録する（無題・仮想ドキュメントは対象外）。</summary>
    private void RecordTrailEditorTab(EditorTab tab)
    {
        var path = tab.PeekFilePath;
        if (string.IsNullOrWhiteSpace(path) || tab.PeekIsVirtual)
            return;

        var line = -1;
        var column = -1;
        if (tab.IsRealized)
        {
            line = tab.Control.Caret.Line;
            column = tab.Control.Caret.Column;
        }
        RecordTrail((mode, stagePane, layout) =>
            _vm.Trail.RecordFile(path, line, column, mode, stagePane, layout));
    }

    /// <summary>新しい地点を積む直前に、最新エントリ（＝いま離れるファイル）のカーソル位置を
    /// タブの現在値で上書きする。これで「戻る」が到着時でなく離脱時の場所になる。</summary>
    private void RefreshLatestTrailFilePosition()
    {
        if (_vm.Trail.LatestFileTarget is not { } target)
            return;

        var tab = _editorTabs.FirstOrDefault(t => t.IsRealized
            && string.Equals(t.PeekFilePath, target, StringComparison.OrdinalIgnoreCase));
        if (tab is not null)
            _vm.Trail.UpdateLatestFilePosition(target, tab.Control.Caret.Line, tab.Control.Caret.Column);
    }

    /// <summary>エディタでの編集（バッファ変更）を軌跡へ記録する。BufferChanged は1打鍵ごとに飛ぶので
    /// 即記録はせず、編集が一段落するまで待って（<see cref="_trailEditCommitTimer"/>）最新の編集行で1点に畳む。
    /// 未変更（ファイル読込直後など）・無題・仮想ドキュメントは対象外。別ファイルの編集へ移ったときは
    /// 前のファイルの編集を先に確定してから新しい待ちを張る（編集が取りこぼされないように）。</summary>
    private void RecordTrailEdit(EditorTab tab)
    {
        if (_trailSuppressed || !tab.IsRealized || !tab.Control.IsModified)
            return;
        var path = tab.PeekFilePath;
        if (string.IsNullOrWhiteSpace(path) || tab.PeekIsVirtual)
            return;

        if (_trailPendingEditTab is { } pending && !ReferenceEquals(pending, tab))
            CommitTrailEdit();

        _trailPendingEditTab = tab;
        _trailEditCommitTimer ??= CreateTrailEditCommitTimer();
        _trailEditCommitTimer.Stop();
        _trailEditCommitTimer.Start();
    }

    private DispatcherTimer CreateTrailEditCommitTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CommitTrailEdit();
        };
        return timer;
    }

    /// <summary>保留中の編集地点を、そのタブの現在のカーソル位置で確定する。編集が取り消されて
    /// 未変更に戻った・タブが破棄された等で対象が無ければ何もしない。</summary>
    private void CommitTrailEdit()
    {
        _trailEditCommitTimer?.Stop();
        if (_trailPendingEditTab is not { } tab)
            return;
        _trailPendingEditTab = null;
        if (_trailSuppressed || !tab.IsRealized || !tab.Control.IsModified)
            return;
        var path = tab.PeekFilePath;
        if (string.IsNullOrWhiteSpace(path) || tab.PeekIsVirtual)
            return;
        var line = tab.Control.Caret.Line;
        var column = tab.Control.Caret.Column;
        RecordTrail((mode, stagePane, layout) =>
            _vm.Trail.RecordEdit(path, line, column, mode, stagePane, layout));
    }

    /// <summary>Git 操作（変更系コマンド）を軌跡へログとして記録する。成功した操作だけを記録し
    /// （失敗→再試行の二重や、内部で失敗した多段操作の断片を避ける）、連続する同種操作はデデュープで
    /// 1点に畳む。復元は行わないので配置は載せない（<see cref="RecordTrailGit"/> はバックグラウンド
    /// スレッドの <see cref="sk0ya.Loomo.Services.GitService.OperationExecuted"/> から UI スレッドへ回して呼ぶ）。</summary>
    private void RecordTrailGit(string command, bool success)
    {
        if (!success)
            return;
        var (key, label) = DescribeGitOperation(command);
        if (string.IsNullOrEmpty(key))
            return;
        RecordTrail((mode, stagePane, _) =>
            _vm.Trail.RecordGit(key, label, mode, stagePane));
    }

    /// <summary>git のサブコマンド行（<see cref="sk0ya.Loomo.Services.GitOperationEventArgs.Command"/>）を、デデュープ用の
    /// 種別キーと表示ラベルへ変換する。キーが同じ連続操作は1点に畳まれる（多段の破棄＝clean+restore を
    /// 1点にまとめる等）。未知のサブコマンドはそのまま <c>git &lt;sub&gt;</c> と表示する。</summary>
    private static (string Key, string Label) DescribeGitOperation(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return ("", "");
        var sub = parts[0];
        bool Has(string flag) => Array.IndexOf(parts, flag) >= 0;
        var lastRef = parts.Length > 1 ? parts[^1] : "";
        return sub switch
        {
            "commit" => ("commit", Has("--amend") ? "コミット（amend）" : "コミット"),
            "add" => ("stage", "ステージ"),
            "restore" when Has("--staged") => ("unstage", "アンステージ"),
            "restore" => ("discard", "変更を破棄"),
            "clean" => ("discard", "変更を破棄"),
            "apply" => ("discard", "変更を破棄"),
            "push" => ("push", "プッシュ"),
            "pull" => ("pull", "プル"),
            "fetch" => ("fetch", "フェッチ"),
            "switch" when Has("-c") => ("branch-create", $"ブランチ作成: {lastRef}"),
            "switch" => ("checkout", $"ブランチ切替: {lastRef}"),
            "checkout" when Has("--detach") => ("checkout-detach", "コミットをチェックアウト"),
            "checkout" => ("checkout", $"ブランチ切替: {lastRef}"),
            "branch" when Has("-d") || Has("-D") => ("branch-delete", $"ブランチ削除: {lastRef}"),
            "branch" => ("branch", "ブランチ操作"),
            "merge" when Has("--continue") => ("merge", "マージ続行"),
            "merge" when Has("--abort") => ("merge", "マージ中止"),
            "merge" => ("merge", $"マージ: {lastRef}"),
            "rebase" when Has("--continue") => ("rebase", "リベース続行"),
            "rebase" when Has("--abort") => ("rebase", "リベース中止"),
            "rebase" when Has("--skip") => ("rebase", "リベーススキップ"),
            "rebase" => ("rebase", "リベース"),
            "cherry-pick" => ("cherry-pick", "チェリーピック"),
            "revert" => ("revert", "リバート"),
            "reset" => ("reset", "リセット"),
            "stash" => ("stash", "スタッシュ"),
            "tag" => ("tag", "タグ"),
            "submodule" => ("submodule", "サブモジュール"),
            "init" => ("init", "リポジトリ初期化"),
            _ => (sub, $"git {sub}")
        };
    }

    /// <summary>EditorSupport（プレビュー）で表示中のファイルを軌跡へ記録する。ペインではなく
    /// プレビュー対象のファイルを target にするので、戻ると同じファイルのプレビューが開き直す
    /// （generic な Pane(EditorSupport) ドットと違い「どのファイルを見ていたか」まで復元できる）。
    /// 無題・仮想ドキュメント・追従先未確定のときは記録しない。</summary>
    private void RecordTrailPreview(EditorTab? sourceTab)
    {
        var path = sourceTab?.PeekFilePath;
        if (string.IsNullOrWhiteSpace(path) || sourceTab!.PeekIsVirtual)
            return;
        RecordTrail((mode, stagePane, layout) =>
            _vm.Trail.RecordPreview(path, mode, stagePane, layout));
    }

    /// <summary>ブラウザ遷移を軌跡へ記録する。既定ページ（新規タブの初期表示）と about: は対象外。</summary>
    private void RecordTrailBrowser(string? url, string? title)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url, DefaultBrowserUrl, StringComparison.OrdinalIgnoreCase))
            return;

        RecordTrail((mode, stagePane, layout) =>
            _vm.Trail.RecordBrowser(url, title, mode, stagePane, layout));
    }

    /// <summary>ターミナルタブの活性化を、再起動後も同じタブへ戻れる ID 付きで記録する。</summary>
    private void RecordTrailTerminalTab(TerminalTab tab)
    {
        var label = _vm.Tabs.TerminalTabs.FirstOrDefault(t => t.Id == tab.Id)?.Title;
        if (string.IsNullOrWhiteSpace(label))
            label = string.IsNullOrWhiteSpace(tab.View.HeaderTitle) ? "ターミナル" : tab.View.HeaderTitle;
        RecordTrail((mode, stagePane, layout) =>
            _vm.Trail.RecordTerminal(tab.Id, label, mode, stagePane, layout));
    }

    /// <summary>フォーカスが別ペインへ移ったことを軌跡へ記録する（同一ペイン内の移動は対象外）。
    /// WebView2 の実体化やプレビュー更新はフォーカスを奪い合って Editor⇄Browser⇄プレビューの
    /// 往復イベントを大量に起こすため、即時には記録せず「同じペインに一定時間とどまった」ときだけ
    /// 1個のドットとして確定する（<see cref="_trailPaneCommitTimer"/>）。
    /// ステージ中は舞台に立つのは常に1ペインで、その切替は <see cref="RecordTrailLayoutIfChanged"/> の
    /// Layout ドットが確実に代表するため、ここ（デバウンス・フォーカス競合のある経路）では記録しない。</summary>
    private void RecordTrailPane(PaneKind kind)
    {
        if (_trailSuppressed || _stageActive)
            return;
        var mode = DisplayMode.Layout;
        if (_trailLastPane == kind && _trailLastPaneMode == mode)
        {
            // 元のペインへすぐ戻った＝行き来ノイズ。保留中の別ペイン記録も取り消す。
            _trailPendingPane = null;
            _trailPaneCommitTimer?.Stop();
            return;
        }
        _trailPendingPane = kind;
        _trailPaneCommitTimer ??= CreateTrailPaneCommitTimer();
        _trailPaneCommitTimer.Stop();
        _trailPaneCommitTimer.Start();
    }

    private DispatcherTimer CreateTrailPaneCommitTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_trailPendingPane is not { } kind)
                return;
            _trailPendingPane = null;
            // Pane ドットはタイル（レイアウト）モード専用。確定までの間にステージへ入ったら、その切替は
            // Layout ドットが代表するのでここでは積まない。確定条件は実フォーカスがまだそのペインにあること。
            var mode = DisplayMode.Layout;
            if (_trailSuppressed
                || _stageActive
                || (_trailLastPane == kind && _trailLastPaneMode == mode)
                || _focusedRegion?.Pane != kind)
                return;
            _trailLastPane = kind;
            _trailLastPaneMode = mode;
            // ファイルを開いているエディタへの切替は、タブ活性化のファイルドットが代表する
            // （「エディタ」ペインのドットを重ねて2個積まない）。デデュープで増殖はしない。
            if (kind == PaneKind.Editor && _activeEditorTab is { } et
                && !string.IsNullOrWhiteSpace(et.PeekFilePath) && !et.PeekIsVirtual)
            {
                RecordTrailEditorTab(et);
                return;
            }
            if (kind == PaneKind.Terminal && _activeTerminalTab is { } tt)
            {
                RecordTrailTerminalTab(tt);
                return;
            }
            // プレビューペインへの切替は、いま映しているファイルの Preview ドットが代表する
            // （どのファイルを見ていたかまで戻れる）。追従先が無ければ通常の Pane ドットへ落ちる。
            if (kind == PaneKind.EditorSupport && _editorSupportSourceTab is { } est
                && !string.IsNullOrWhiteSpace(est.PeekFilePath) && !est.PeekIsVirtual)
            {
                RecordTrailPreview(est);
                return;
            }
            RecordTrail((mode, stagePane, layout) =>
                _vm.Trail.RecordPane(kind.ToString(), PaneDisplayName(kind), mode, stagePane, layout));
        };
        return timer;
    }

    /// <summary>AI セッションのアクティブ化を軌跡へ記録する。target は保存済みセッションの ID で、
    /// 戻るとそのセッションを復元して AI ペインを開き直す。ジャンプ復帰中は <see cref="RecordTrail"/> が抑止する。</summary>
    private void RecordTrailSession(string id, string title)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;
        RecordTrail((mode, stagePane, layout) =>
            _vm.Trail.RecordSession(id, title, mode, stagePane, layout));
    }

    /// <summary>サイドバーのパネル切替を軌跡へ記録する。</summary>
    private void RecordTrailPanel(SidebarPanel panel)
        => RecordTrail((mode, stagePane, layout) =>
            _vm.Trail.RecordPanel(panel.ToString(), PanelDisplayName(panel), mode, stagePane, layout));

    private static readonly JsonSerializerOptions TrailLayoutJson = new();

    private static string PaneDisplayName(PaneKind kind) => kind switch
    {
        PaneKind.Terminal => "ターミナル",
        PaneKind.Editor => "エディタ",
        PaneKind.Browser => "ブラウザ",
        PaneKind.Ai => "AI",
        PaneKind.EditorSupport => "プレビュー",
        PaneKind.Git => "Git",
        PaneKind.Diff => "Diff",
        PaneKind.Trace => "トレース",
        PaneKind.Debug => "IDE",
        _ => kind.ToString()
    };

    private static string PanelDisplayName(SidebarPanel panel) => panel switch
    {
        SidebarPanel.Explorer => "エクスプローラ",
        SidebarPanel.Search => "検索",
        SidebarPanel.Tabs => "タブ一覧",
        SidebarPanel.Sessions => "AIセッション",
        SidebarPanel.Git => "Gitパネル",
        SidebarPanel.Pegboard => "ペグボード",
        _ => panel.ToString()
    };

    // ===== 戻る（ジャンプ） =====

    /// <summary>種別ごとの「戻る」処理を登録する。新しい軌跡ソースはここへ1行足すだけでよい
    /// （実処理は同期でも <c>Task.CompletedTask</c> を返せば足りる）。</summary>
    private void RegisterTrailJumps()
    {
        _trailJumps[TrailEntryKind.File] = JumpToFileAsync;
        _trailJumps[TrailEntryKind.Browser] = entry => { JumpToBrowser(entry); return Task.CompletedTask; };
        _trailJumps[TrailEntryKind.Pane] = entry => { JumpToPane(entry); return Task.CompletedTask; };
        _trailJumps[TrailEntryKind.Panel] = entry => { JumpToPanel(entry); return Task.CompletedTask; };
        _trailJumps[TrailEntryKind.Terminal] = entry => { JumpToTerminal(entry); return Task.CompletedTask; };
        _trailJumps[TrailEntryKind.Preview] = JumpToPreviewAsync;
        _trailJumps[TrailEntryKind.Session] = entry => { JumpToSession(entry); return Task.CompletedTask; };
        _trailJumps[TrailEntryKind.Layout] = _ => Task.CompletedTask;
        // 編集地点はファイルと同じく file:line を開き直す（内容の復元はしない）。
        _trailJumps[TrailEntryKind.Edit] = JumpToFileAsync;
        // Git 操作はログ専用で戻り先を持たない（CanJumpToTrailEntry で弾かれるため実際には呼ばれない）。
        _trailJumps[TrailEntryKind.Git] = _ => Task.CompletedTask;
    }

    private void JumpToTrailEntry(TrailEntryViewModel entry)
    {
        _trailPendingJumpEntry = entry; // 実行中なら中間要求を捨て、最後の要求だけ残す
        if (!_trailJumpRunning)
            ProcessTrailJumpsAsync();
    }

    private async void ProcessTrailJumpsAsync()
    {
        if (_trailJumpRunning)
            return;

        _trailJumpRunning = true;
        var saved = _trailSuppressed;
        _trailSuppressed = true;
        try
        {
            while (_trailPendingJumpEntry is { } entry)
            {
                _trailPendingJumpEntry = null;
                if (!_trailJumps.TryGetValue(entry.Kind, out var jump) || !CanJumpToTrailEntry(entry))
                    continue;
                RestoreTrailDisplayContext(entry);
                await jump(entry);
            }
        }
        finally
        {
            _trailSuppressed = saved;
            _trailJumpRunning = false;
        }
        ScrollTrailCurrentIntoView();
    }

    /// <summary>対象と表示コンテキストを先に検証し、失敗時に画面構成だけ変わることを防ぐ。</summary>
    private bool CanJumpToTrailEntry(TrailEntryViewModel entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.PaneLayout))
        {
            try
            {
                if (JsonSerializer.Deserialize<PaneNodeSnapshot>(entry.PaneLayout, TrailLayoutJson) is null)
                    return false;
            }
            catch { return false; }
        }

        return entry.Kind switch
        {
            TrailEntryKind.File => File.Exists(entry.Target),
            TrailEntryKind.Browser => !string.IsNullOrWhiteSpace(entry.Target),
            TrailEntryKind.Pane => Enum.TryParse<PaneKind>(entry.Target, out var pane)
                                   && _paneElements.ContainsKey(pane),
            TrailEntryKind.Panel => Enum.TryParse<SidebarPanel>(entry.Target, out _),
            TrailEntryKind.Terminal => Guid.TryParse(entry.Target, out var id)
                                       && _terminalTabs.Any(t => t.Id == id),
            TrailEntryKind.Preview => File.Exists(entry.Target),
            TrailEntryKind.Session => _vm.AiBar.SessionExists(entry.Target),
            TrailEntryKind.Layout => !string.IsNullOrWhiteSpace(entry.PaneLayout),
            TrailEntryKind.Edit => File.Exists(entry.Target),
            TrailEntryKind.Git => false,   // ログ専用：クリックしても復元しない
            _ => false
        };
    }

    /// <summary>地点固有の対象へ移動する前に、記録時のレイアウト／ソロ表示を復元する。</summary>
    private void RestoreTrailDisplayContext(TrailEntryViewModel entry)
    {
        if (_stageActive)
            ExitStageMode();

        if (!string.IsNullOrWhiteSpace(entry.PaneLayout))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<PaneNodeSnapshot>(entry.PaneLayout, TrailLayoutJson);
                if (snapshot is not null)
                    ApplyPaneLayout(snapshot);
            }
            catch { /* 壊れた1件だけ配置復元を省略し、対象へのジャンプは続ける */ }
        }

        if (entry.Mode == DisplayMode.Solo)
        {
            EnterStageMode(entry.StagePane);
        }
    }

    private async Task JumpToFileAsync(TrailEntryViewModel entry)
    {
        if (!File.Exists(entry.Target))
            return;   // 消えたファイルはそっと何もしない（ブランチ切替等で戻ることもある）
        await OpenFileInNewEditorTabAsync(entry.Target);
        FocusPane(PaneKind.Editor);
        if (entry.Line >= 0)
            _activeEditorTab?.Control.NavigateTo(entry.Line, Math.Max(0, entry.Column));
    }

    private async Task JumpToPreviewAsync(TrailEntryViewModel entry)
    {
        if (!File.Exists(entry.Target))
            return;   // 消えたファイルはそっと何もしない（ブランチ切替等で戻ることもある）
        // プレビュー元のファイルをエディタで開き直してから、その内容で EditorSupport ペインを開く。
        await OpenFileInNewEditorTabAsync(entry.Target);
        if (_activeEditorTab is { } tab)
            await OpenEditorSupportAsync(tab);
    }

    private void JumpToBrowser(TrailEntryViewModel entry)
    {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
        FocusPane(PaneKind.Browser);
        NavigateBrowser(entry.Target);
    }

    private void JumpToPane(TrailEntryViewModel entry)
    {
        if (!Enum.TryParse<PaneKind>(entry.Target, out var pane))
            return;
        EnsurePaneVisibleOrSwapTopLeft(pane);
        FocusPane(pane);
        _trailLastPane = pane;   // 戻った先を「直近のペイン」として同期する
        _trailLastPaneMode = entry.Mode;
    }

    private void JumpToPanel(TrailEntryViewModel entry)
    {
        if (!Enum.TryParse<SidebarPanel>(entry.Target, out var panel))
            return;
        _vm.ActivePanel = panel;
        _vm.IsSidebarVisible = true;
    }

    /// <summary>AI セッション地点へ戻る：保存済みセッションを復元し、AI ペインを前面に出してフォーカスする。
    /// 削除済みで復元できないときは画面構成を変えずに何もしない。</summary>
    private void JumpToSession(TrailEntryViewModel entry)
    {
        if (!_vm.AiBar.RestoreSessionById(entry.Target))
            return;   // 削除済みセッションは復元不能なので何もしない
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Ai);
        FocusPane(PaneKind.Ai);
    }

    private void JumpToTerminal(TrailEntryViewModel entry)
    {
        if (!Guid.TryParse(entry.Target, out var id) || _terminalTabs.All(t => t.Id != id))
            return;   // 閉じられたタブは復元不能なので何もしない
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Terminal);
        ActivateTerminalTab(id);
        FocusPane(PaneKind.Terminal);
    }

    // ===== バー上のホイール＝現在地の前後移動（スクラブ） =====

    /// <summary>バー上のホイール：上＝過去（左）へ、下＝未来（右）へ現在地を動かす。
    /// 実ジャンプは少し遅らせ、連続ホイールを最後の1回に畳む。</summary>
    private void OnTrailWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var entry = _vm.Trail.MoveCurrent(e.Delta > 0 ? -1 : +1);
        ScrollTrailCurrentIntoView();
        if (entry is null)
            return;

        _trailScrubTarget = entry;
        _trailScrubTimer ??= CreateTrailScrubTimer();
        _trailScrubTimer.Stop();
        _trailScrubTimer.Start();
    }

    private DispatcherTimer CreateTrailScrubTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_trailScrubTarget is { } target)
            {
                _trailScrubTarget = null;
                JumpToTrailEntry(target);
            }
        };
        return timer;
    }

    /// <summary>ドット1個のスロット幅。XAML のドット Button の幅と揃える。</summary>
    private const double TrailDotWidth = 14;

    /// <summary>時間帯の先頭ドットに前置する時刻ラベル枠の幅。XAML の HourTick Border の幅と揃える。</summary>
    private const double TrailHourLabelWidth = 32;

    /// <summary>ドット列の左端から index 番目のスロットの左端までの累積幅。時間帯の先頭ドットは
    /// 左に時刻ラベル枠（幅 <see cref="TrailHourLabelWidth"/>）が前置されてスロットが広くなるため、
    /// 単純な等間隔ではなく各ドットの実効幅を積む。index==Entries.Count で全体の内容幅になる。</summary>
    private double TrailSlotOffset(int index)
    {
        var entries = _vm.Trail.Entries;
        double x = 0;
        for (var i = 0; i < index && i < entries.Count; i++)
            x += TrailDotWidth + (entries[i].StartsNewHour ? TrailHourLabelWidth : 0);
        return x;
    }

    /// <summary>現在地のドットが見えるよう水平スクロールを追従させる（無ければ右端＝最新へ）。
    /// ライブ追従・スクラブ・ジャンプ共通の中央寄せ。末尾余白は要らないので畳んで、最新ドットが
    /// 右端に張り付く既定挙動へ戻す（時間帯選択の左寄せ <see cref="ScrollTrailHourToLeft"/> とは別経路）。</summary>
    /// <summary>エントリの追加・削除での追従スクロール。左端へ寄せている最中（<see cref="_trailSnapLeft"/>）は
    /// スクロール位置に一切触れない：新しいドットは末尾余白の中を右へどんどん積まれてよく、左端に見えている
    /// 地点だけが右へ動かなければよい。それ以外は現在地を中央へ寄せる既定挙動。</summary>
    private void ScrollTrailAfterEntriesChanged()
    {
        if (_trailSnapLeft)
            return;
        ScrollTrailCurrentIntoView();
    }

    private void ScrollTrailCurrentIntoView()
    {
        _trailSnapLeft = false;           // 中央寄せへ戻る：以後の登録は既定の中央追従に任せる
        TrailTrailingSpacer.Width = 0;   // 左寄せ用の末尾余白を畳む（既定は最新を右端へ）
        var index = _vm.Trail.CurrentIndex;
        if (index < 0)
        {
            TrailScroll.ScrollToRightEnd();
            return;
        }
        var entries = _vm.Trail.Entries;
        var x = TrailSlotOffset(index);
        var leading = index < entries.Count && entries[index].StartsNewHour ? TrailHourLabelWidth : 0;
        var center = x + leading + TrailDotWidth / 2;
        TrailScroll.ScrollToHorizontalOffset(Math.Max(0, center - TrailScroll.ViewportWidth / 2));
    }

    /// <summary>時間帯選択（<see cref="OnTrailHourSelected"/>）用：現在地スロットの<b>左端</b>（時間帯の
    /// 先頭なら時刻ラベル枠の先頭）を表示領域の左端へピタッと寄せる。末尾側の時間帯でも左寄せできるよう、
    /// 右に足りない分だけ末尾余白（<see cref="TrailTrailingSpacer"/>）を伸ばして表示領域を広げてから寄せる。</summary>
    private void ScrollTrailHourToLeft()
    {
        var index = _vm.Trail.CurrentIndex;
        if (index < 0)
        {
            ScrollTrailCurrentIntoView();
            return;
        }
        _trailSnapLeft = true;   // 以後の登録でも左寄せを保つ（登録で左端の地点が右へ動かない）
        var x = TrailSlotOffset(index);
        var contentWidth = TrailSlotOffset(_vm.Trail.Entries.Count);
        var viewport = TrailScroll.ViewportWidth;
        // 左端に x を置くには内容の右端が x+viewport まで必要。足りなければ末尾余白で補う。
        TrailTrailingSpacer.Width = Math.Max(0, x + viewport - contentWidth);
        // 余白反映後の再レイアウトを待ってから寄せる（伸ばした直後は ScrollableWidth がまだ古い）。
        Dispatcher.BeginInvoke(new Action(() => TrailScroll.ScrollToHorizontalOffset(x)),
            DispatcherPriority.Loaded);
    }

    // ===== 日付（カレンダーで過去の軌跡へ） =====

    private void OnTrailDateClick(object sender, RoutedEventArgs e)
    {
        // トグル：開いた状態でのボタン再クリックは、直前の外側クリックで閉じた分を「閉じる操作」とみなす。
        if (TrailCalendarPopup.IsOpen
            || (DateTime.UtcNow - _trailCalendarClosedAt).TotalMilliseconds < 250)
        {
            TrailCalendarPopup.IsOpen = false;
            return;
        }
        TrailCalendar.SelectedDate = _vm.Trail.DisplayDate.ToDateTime(TimeOnly.MinValue);
        TrailCalendar.DisplayDate = TrailCalendar.SelectedDate.Value;
        TrailCalendar.DisplayDateEnd = DateTime.Today;   // 未来は選べない
        TrailCalendarPopup.IsOpen = true;
    }

    private void OnTrailCalendarSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!TrailCalendarPopup.IsOpen || TrailCalendar.SelectedDate is not { } picked)
            return;
        TrailCalendarPopup.IsOpen = false;
        // Calendar はクリック後もマウスキャプチャを持ち続け、直後のクリックを1回飲み込むため解放する。
        Mouse.Capture(null);
        _vm.Trail.ShowDate(DateOnly.FromDateTime(picked));
        Dispatcher.BeginInvoke(new Action(ScrollTrailCurrentIntoView), DispatcherPriority.Loaded);
    }

    // ===== 時刻（時間帯へ移動） =====

    /// <summary>シングルクリックの時間帯ポップアップ開を、ダブルクリック判定の猶予ぶんだけ遅らせて
    /// 確定するタイマ。押下時に張り、システムのダブルクリック時間内に2打目が来たら取り消す。</summary>
    private DispatcherTimer? _trailHourClickTimer;

    /// <summary>時刻ボタンの押下を一元処理する。シングルクリックは時間帯ポップアップをトグルし、
    /// ダブルクリックは軌跡の現在地を最新地点（＝ライブでは「今」）へ選択し直して表示領域の左端へ寄せる。
    /// <para>ポップアップ（<c>StaysOpen=False</c>）は開くとマウスキャプチャを奪い、2打目の押下が
    /// ボタンへ届かず <see cref="MouseButtonEventArgs.ClickCount"/>==2 を取り逃す。そこで Button.Click は
    /// 使わず（<c>e.Handled</c> で抑止）、シングルクリックのポップアップ開はダブルクリック時間ぶん遅らせて、
    /// その猶予内に2打目が来たら開かずに「今」へ寄せる方を採る。閉じる側は日付ボタンと同じく、
    /// 直前の外側クリックで閉じた分を <see cref="_trailHourPopupClosedAt"/> で汲む。</para></summary>
    private void OnTrailHourPreviewDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;   // 押下はここで一元管理し、Button.Click（＝ポップアップ即時開）へ落とさない

        if (e.ClickCount >= 2)
        {
            _trailHourClickTimer?.Stop();   // 保留中のシングルクリック（ポップアップ開）を取り消す
            TrailHourPopup.IsOpen = false;
            if (_vm.Trail.MoveToLatest() is not null)
                // 時間帯選択と同じく「今」の地点を表示領域の左端へピタッと寄せる（末尾側でも余白で広げて左寄せ）。
                Dispatcher.BeginInvoke(new Action(ScrollTrailHourToLeft), DispatcherPriority.Loaded);
            return;
        }

        // 開いている（または直前に外側クリックで閉じた）なら、シングルクリックは閉じるだけ。
        if (TrailHourPopup.IsOpen
            || (DateTime.UtcNow - _trailHourPopupClosedAt).TotalMilliseconds < 250)
        {
            TrailHourPopup.IsOpen = false;
            return;
        }
        if (_vm.Trail.Hours.Count == 0)
            return;

        _trailHourClickTimer ??= CreateTrailHourClickTimer();
        _trailHourClickTimer.Stop();
        _trailHourClickTimer.Start();
    }

    /// <summary>2打目が来なければシングルクリック確定として時間帯ポップアップを開く。間隔はユーザーの
    /// システム設定（<c>GetDoubleClickTime</c>）に合わせ、猶予より速いダブルクリックは確実に拾う。</summary>
    private DispatcherTimer CreateTrailHourClickTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(200u, GetDoubleClickTime()))
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_vm.Trail.Hours.Count > 0)
                TrailHourPopup.IsOpen = true;
        };
        return timer;
    }

    /// <summary>ポップアップで時間帯を選ぶ：その時間帯の先頭ドットを現在地にしてスクロールする
    /// （画面復元はしない＝バー内のナビゲーション。ドット列はそのまま並ぶ）。</summary>
    private void OnTrailHourSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TrailHourViewModel hour })
            return;
        TrailHourPopup.IsOpen = false;
        Mouse.Capture(null);
        _vm.Trail.SelectHour(hour);
        // 選んだ時間帯の時刻ラベルを表示領域の左端へピタッと寄せる（末尾側でも余白で表示領域を広げて左寄せ）。
        Dispatcher.BeginInvoke(new Action(ScrollTrailHourToLeft), DispatcherPriority.Loaded);
    }
}
