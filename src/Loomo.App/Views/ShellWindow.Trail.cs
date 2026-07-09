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
/// バー上の Shift+ホイール（現在地の前後移動）でその地点へ戻る（素のホイールはバーの水平スクロール）。
/// バー左端の日付クリック→カレンダーで
/// 過去の日の軌跡も表示できる。アイデア.md「Semantic Depth」構想の Thread Rail の種。
///
/// <para><b>新しい軌跡ソースの足し方（登録側はこれだけ）</b>：
/// ①<see cref="TrailEntryKind"/> に enum 値を1つ追加し <c>Glyph</c>（ツールチップ・一意性テスト用）と
/// <c>IconGeometry</c>（バーに描く絵姿）を対で1本ずつ足す。
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

    /// <summary>ユーザーが過去の地点を見ている（＝最新を追っていない）間 true。バーへ新しい地点が
    /// 積まれても左端を動かさず、見ている位置を保つ（レイアウト変更などで最新へ引っ張らない）。
    /// 「今」へ戻す操作・最新への移動・ホイールで右端まで戻したら false（＝ライブ追従）へ戻す。</summary>
    private bool _trailBrowsingPast;

    /// <summary>ホイールでの現在地移動を1回のジャンプへ畳むデバウンス。</summary>
    private DispatcherTimer? _trailScrubTimer;
    private TrailEntryViewModel? _trailScrubTarget;
    private TrailEntryViewModel? _trailPendingJumpEntry;
    private bool _trailJumpRunning;

    /// <summary>ジャンプ完了後、抑制を戻すまでの余韻（settle）を計るタイマ。ジャンプが誘発する
    /// 非同期イベント（フォーカス確定・ブラウザ遷移完了など）を抑制内に収めるために張る。</summary>
    private DispatcherTimer? _trailJumpSettleTimer;

    /// <summary>settle 後に <see cref="_trailSuppressed"/> を戻す先（ジャンプ開始前の抑制状態）。
    /// 連続ジャンプで settle を張り直す間は最初に捕まえた値を保つ（外側の抑制を壊さない）。</summary>
    private bool _trailJumpBaseSuppressed;
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
        // 追記でのスクロール追従：過去を見ている間は動かさず、ライブ（最新を追っている）ときだけ
        // 現在地を左端へ寄せる（末尾の余白ぶん、最新は左端＋右に余白で収まる）。
        _vm.Trail.Entries.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_trailBrowsingPast)
                    ScrollTrailToCurrent();
            }), DispatcherPriority.Loaded);
        // 末尾余白（ビュー幅ぶん）をバーの幅に追従させる。これが無いと ScrollViewer のクランプで
        // 末尾付近のドットを左端まで寄せられない（選んだ時間帯が左端に届かない）。
        TrailScroll.SizeChanged += (_, _) => UpdateTrailTrailingMargin();
        // 日付・時刻ボタンの再クリックによるトグル判定用：ボタンを再クリックすると、Click が届く前に
        // ロストフォーカスでポップアップが閉じるため、閉じた時刻を覚えて直後の再オープンを抑止する。
        // 併せて、ポップアップ内での日付・時間帯の選択は閉じたこの瞬間にまとめて画面（レイアウト）へ反映する
        // （選択中はバー内ナビゲーションに留め、閉じたときに現在地の地点へジャンプする＝コミット、§27.7.2）。
        TrailDateTimePopup.Closed += (_, _) =>
        {
            _trailDateTimePopupClosedAt = DateTime.UtcNow;
            CommitTrailPopupSelection();
        };
        // ウィンドウがフォーカスを失ったら日付・時刻ポップアップも閉じる（ロストフォーカスで閉じる）。
        // ポップアップ内クリックはウィンドウをアクティブに保つので、真に外部へ移ったときだけ閉じる。
        Deactivated += (_, _) => { if (TrailDateTimePopup.IsOpen) TrailDateTimePopup.IsOpen = false; };
        // ライブの時刻ラベル（HH:mm）を時計の進みに合わせて更新する。過去地点表示中は HH:00 固定なので無害。
        _trailHourTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _trailHourTicker.Tick += (_, _) => _vm.Trail.RefreshHourLabel();
        _trailHourTicker.Start();
        // 起動のクリティカルパスを避けて、今日の軌跡を SQLite から遅延読込する。
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => _vm.Trail.EnsureLoaded()));
    }

    /// <summary>日付・時刻ポップアップが最後に閉じた時刻（ボタン再クリックでのトグル判定）。</summary>
    private DateTime _trailDateTimePopupClosedAt;

    /// <summary>日付・時刻ポップアップを開いてから、日付か時間帯を選んだ（＝現在地をバー内で動かした）か。
    /// 選択中は画面（レイアウト）を変えず、ポップアップを閉じたときにまとめて現在地の地点へジャンプする
    /// （<see cref="CommitTrailPopupSelection"/>）ためのフラグ。開くたびに false へ戻す（§27.7.2）。</summary>
    private bool _trailPopupSelectionMade;

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

    /// <summary>ペイン切替でブラウザのページを軌跡へ代表させるための、アクティブなブラウザタブの
    /// 記録対象 URL（実体化前は保留中の遷移先で代用）。<see cref="RecordTrailBrowser"/> と同じ除外
    /// （既定ページ・about:・空）を満たす URL が無ければ null を返し、呼び出し側は generic な Pane
    /// ドットへ落とす（＝この「表示」を Browser ドットにはしない）。</summary>
    private string? CurrentBrowserTrailUrl()
    {
        var url = _activeBrowserTab?.View.Source?.ToString() ?? _activeBrowserTab?.PendingUrl;
        if (string.IsNullOrWhiteSpace(url)
            || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url, DefaultBrowserUrl, StringComparison.OrdinalIgnoreCase))
            return null;
        return url;
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
            // ブラウザペインへの切替は、いま表示しているページの Browser ドットが代表する
            // （generic な Pane(Browser) ドットではなく URL／タイトルまで戻れる）。ブラウザは
            // ナビゲートが起きない限り NavigationCompleted が飛ばないので、既に読み込み済みのページを
            // 見に来ただけの「表示」がここで記録されないと軌跡へ残らない。記録すべき URL が無い
            // （既定ページ・about:・未確定）ときだけ通常の Pane ドットへ落ちる。
            if (kind == PaneKind.Browser && CurrentBrowserTrailUrl() is { } browserUrl)
            {
                RecordTrailBrowser(browserUrl, _activeBrowserTab?.View.CoreWebView2?.DocumentTitle);
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
        // settle 待ちが無ければ、いまの抑制状態を戻し先として捕まえる（連続ジャンプの張り直しでは
        // 最初に捕まえた値を保つ＝外側の抑制を壊さない）。前回の settle は一旦止めてこのジャンプ後に張り直す。
        if (_trailJumpSettleTimer is not { IsEnabled: true })
            _trailJumpBaseSuppressed = _trailSuppressed;
        _trailJumpSettleTimer?.Stop();
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
            _trailJumpRunning = false;
        }
        // ジャンプ自体はバーをスクロールしない（クリックした地点はすでに見えており勝手に動かさない。
        // スクラブは ScrubTrailByWheel が別途スクロールする）。過去地点へ戻ったら以後の追記でも追従しない。
        _trailBrowsingPast = _vm.Trail.CurrentIndex < _vm.Trail.Entries.Count - 1;
        // ジャンプの誘発する非同期イベント（フォーカス確定・ブラウザ遷移完了など）は、抑制を戻した
        // 直後に飛んでくる。それらを新しい地点として積んで現在地が最新へ飛ぶのを防ぐため、少し余韻を
        // 置いてから抑制を戻す（連続ジャンプは張り直しで畳む）。
        _trailJumpSettleTimer ??= CreateTrailJumpSettleTimer();
        _trailJumpSettleTimer.Stop();
        _trailJumpSettleTimer.Start();
    }

    /// <summary>ジャンプ完了後、誘発される非同期イベントを抑制内に収めるための余韻タイマ。
    /// 満了で抑制をジャンプ前の状態へ戻す（<see cref="_trailJumpBaseSuppressed"/>）。</summary>
    private DispatcherTimer CreateTrailJumpSettleTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_trailJumpRunning)   // 次のジャンプが走り出していれば、そちらの settle に任せる
                _trailSuppressed = _trailJumpBaseSuppressed;
        };
        return timer;
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

    // ===== バー上のホイール：素＝水平スクロール／Shift＝現在地の前後移動（スクラブ） =====

    /// <summary>バー上のホイール。<b>素のホイール</b>はバーを水平にスクロールするだけ（現在地・画面表示は
    /// 動かさない＝ドット列を眺めるナビゲーション）。<b>Shift+ホイール</b>は従来どおり現在地を前後へ動かして
    /// その地点の表示を復元する（上＝過去（左）へ、下＝未来（右）へ／実ジャンプは少し遅らせて連続ホイールを
    /// 最後の1回に畳む）。</summary>
    private void OnTrailWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            ScrubTrailByWheel(e.Delta);
            return;
        }
        // 素のホイール：バーを水平にスクロールするだけ。右端（ライブ端）から離れたら追従を止め、
        // 右端まで戻したらライブ追従を再開する。
        var target = TrailScroll.HorizontalOffset + (e.Delta > 0 ? -1 : +1) * (TrailDotWidth * 3);
        TrailScroll.ScrollToHorizontalOffset(target);
        _trailBrowsingPast = target < TrailScroll.ScrollableWidth - 1;
    }

    /// <summary>Shift+ホイールでの現在地移動。移動先を左端へ寄せて見せ、実ジャンプは
    /// <see cref="_trailScrubTimer"/> で遅らせて連続ホイールを1回に畳む。</summary>
    private void ScrubTrailByWheel(int delta)
    {
        var entry = _vm.Trail.MoveCurrent(delta > 0 ? -1 : +1);
        _trailBrowsingPast = _vm.Trail.CurrentIndex < _vm.Trail.Entries.Count - 1;
        ScrollTrailToCurrent();
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
    private const double TrailDotWidth = 20;

    /// <summary>時間帯の先頭ドットに前置する時刻ラベル枠の幅。XAML の HourTick Border は
    /// TrailHourLabelW トークン（UiFontManager がフォントサイズと同倍率でスケール）を参照するため、
    /// ここも <see cref="UiFontManager.Scaled"/> で同じ倍率を掛けて揃える。</summary>
    private static double TrailHourLabelWidth => UiFontManager.Scaled(38);

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

    /// <summary>唯一のスクロール追従。現在地のドットのスロット左端を表示領域の左端へ寄せる
    /// （時間帯の先頭なら時刻ラベル枠の先頭）。末尾にビュー幅ぶんの余白（<see cref="UpdateTrailTrailingMargin"/>）が
    /// あるので、最新を含むどの地点も左端まで寄せられる（最新地点は左端＋右に余白で収まる）。
    /// ライブ追従も途中地点の「その地点を左端に置いて以後の並びを右に見せる」挙動も同じ計算で表せる。
    /// 現在地が無ければ末尾（＝最新の並び）へ。追記追従・スクラブ・ジャンプ・時間帯選択の共通経路。</summary>
    private void ScrollTrailToCurrent()
    {
        var index = _vm.Trail.CurrentIndex;
        if (index < 0)
        {
            TrailScroll.ScrollToRightEnd();
            return;
        }
        TrailScroll.ScrollToHorizontalOffset(TrailSlotOffset(index));
    }

    /// <summary>ドット列の末尾に「ビュー幅ぶん（末尾1ドットを左端に置ける最小＝
    /// <see cref="TrailScroll"/> の表示幅 − <see cref="TrailDotWidth"/>）」の余白を確保する。
    /// これが無いと <see cref="ScrollViewer"/> のクランプ（オフセットは <c>ScrollableWidth</c> で頭打ち）で
    /// 末尾付近のドットを左端まで寄せられず、選んだ時間帯が左端に届かない。バーの幅が変わるたびに追従させ、
    /// ライブ追従中は余白変更後の現在地を貼り直す。</summary>
    private void UpdateTrailTrailingMargin()
    {
        var trailing = Math.Max(0, TrailScroll.ViewportWidth - TrailDotWidth);
        if (Math.Abs(TrailDots.Padding.Right - trailing) < 0.5)
            return;
        TrailDots.Padding = new Thickness(0, 0, trailing, 0);
        if (!_trailBrowsingPast)
            Dispatcher.BeginInvoke(new Action(ScrollTrailToCurrent), DispatcherPriority.Loaded);
    }

    /// <summary>バー共通の右クリックメニュー「最新に戻る」。過去日を見ていれば今日へ戻し、現在地を
    /// 軌跡の最新地点（＝ライブの「今」）へ動かして左端へ寄せる。過去日表示・過去地点へのスクラブ、
    /// どちらの状態からも1回で最新のライブ追従へ戻す。日付・時刻ポップアップ内の「最新に戻る」ボタン
    /// （<see cref="OnTrailBackToLatestFromPopup"/>）とも共通の導線。</summary>
    private void OnTrailBackToLatest(object sender, RoutedEventArgs e) => BackToLatestTrail();

    /// <summary>日付・時刻ポップアップ内の「最新に戻る」ボタン。現在地を最新（＝ライブの「今」）へ動かし、
    /// それを選択扱いにしてポップアップを閉じる——閉じたときのコミット（<see cref="CommitTrailPopupSelection"/>）が
    /// 最新地点の画面（レイアウト）を復元する。日付・時間帯の選択と違い、このボタンはポップアップを閉じてよい。</summary>
    private void OnTrailBackToLatestFromPopup(object sender, RoutedEventArgs e)
    {
        BackToLatestTrail();               // 現在地を最新へ（バー内ナビゲーション）
        _trailPopupSelectionMade = true;   // 閉じたときに最新地点のレイアウトへコミットする
        TrailDateTimePopup.IsOpen = false; // Closed が同期発火 → CommitTrailPopupSelection が最新へジャンプ
    }

    /// <summary>過去日表示・過去地点へのスクラブから、ライブの最新地点（＝「今」）へ1回で戻す共通処理。</summary>
    private void BackToLatestTrail()
    {
        _vm.Trail.BackToTodayCommand.Execute(null);   // 過去日表示中なら今日へ（今日表示中は無害）
        _vm.Trail.MoveToLatest();                     // 過去地点へスクラブ中なら最新へ
        _trailBrowsingPast = false;                   // ライブ追従を再開（最新は左端＋右余白に収まる）
        Dispatcher.BeginInvoke(new Action(ScrollTrailToCurrent), DispatcherPriority.Loaded);
    }

    // ===== 日付＋時刻（1つのポップアップで過去の軌跡へ） =====

    /// <summary>日付＋時刻ボタンのクリックで日付・時刻ポップアップをトグルする（最新地点への復帰は
    /// ポップアップ内の「⟳ 最新に戻る」ボタン、またはバー共通の右クリックメニュー「最新に戻る」で行う）。
    /// <para>開いたままボタンを再クリックすると、押下でフォーカスがポップアップ外（このボタン）へ移り、
    /// ロストフォーカスで先に閉じる（<c>Closed</c> が <see cref="_trailDateTimePopupClosedAt"/> を記録）→
    /// その直後の Click はこのガードで閉じたまま＝トグル成立。</para></summary>
    private void OnTrailDateTimeClick(object sender, RoutedEventArgs e)
    {
        // 開いている（または直前にロストフォーカスで閉じた）なら、クリックは閉じるだけ。
        if (TrailDateTimePopup.IsOpen
            || (DateTime.UtcNow - _trailDateTimePopupClosedAt).TotalMilliseconds < 250)
        {
            TrailDateTimePopup.IsOpen = false;
            return;
        }

        OpenTrailDateTimePopup();
    }

    /// <summary>日付・時刻ポップアップを開き、現在の表示日をカレンダーへ反映してから枠へフォーカスを移す
    /// （フォーカスがポップアップ外へ出たら閉じる仕掛け＝<see cref="OnTrailDateTimeLostFocus"/> を効かせるため）。</summary>
    private void OpenTrailDateTimePopup()
    {
        _trailPopupSelectionMade = false;   // 新しく開いた回の選択を仕切り直す（閉じたときにコミットする）
        TrailCalendar.SelectedDate = _vm.Trail.DisplayDate.ToDateTime(TimeOnly.MinValue);
        TrailCalendar.DisplayDate = TrailCalendar.SelectedDate.Value;
        TrailCalendar.DisplayDateEnd = DateTime.Today;   // 未来は選べない
        TrailDateTimePopup.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() => TrailDateTimePopupRoot.Focus()), DispatcherPriority.Input);
    }

    /// <summary>カレンダーで日を選ぶ：その日の軌跡を即バーへ反映する（ポップアップは開いたまま＝
    /// リアルタイム反映。時間帯リストも新しい日の記録へ自動で更新される）。画面（レイアウト）は変えず、
    /// 選択したことだけを覚えて閉じたときにコミットする（<see cref="CommitTrailPopupSelection"/>）。</summary>
    private void OnTrailCalendarSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!TrailDateTimePopup.IsOpen || TrailCalendar.SelectedDate is not { } picked)
            return;
        // Calendar はクリック後もマウスキャプチャを持ち続け、直後のクリックを1回飲み込むため解放する。
        Mouse.Capture(null);
        _vm.Trail.ShowDate(DateOnly.FromDateTime(picked));
        _trailBrowsingPast = false;   // 日を切り替えたらその日の最新（右端）から見る
        _trailPopupSelectionMade = true;
        Dispatcher.BeginInvoke(new Action(ScrollTrailToCurrent), DispatcherPriority.Loaded);
    }

    /// <summary>ポップアップで時間帯を選ぶ：その時間帯の先頭ドットを現在地にしてスクロールする
    /// （ポップアップは開いたまま＝リアルタイム反映。画面復元はせずバー内のナビゲーションに留め、
    /// 実際のレイアウト反映はポップアップを閉じたときにまとめて行う＝コミット、§27.7.2）。</summary>
    private void OnTrailHourSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TrailHourViewModel hour })
            return;
        Mouse.Capture(null);
        _vm.Trail.SelectHour(hour);
        // 選んだ時間帯の先頭ドットを表示領域の左端へ寄せる（末尾余白で最新の帯も左端に届く＝ライブへ復帰）。
        _trailBrowsingPast = _vm.Trail.CurrentIndex < _vm.Trail.Entries.Count - 1;
        _trailPopupSelectionMade = true;
        Dispatcher.BeginInvoke(new Action(ScrollTrailToCurrent), DispatcherPriority.Loaded);
    }

    /// <summary>日付・時刻ポップアップを閉じた瞬間に、選択していた現在地の地点へジャンプして画面（レイアウト）を
    /// 復元する。選択中はバー内ナビゲーションに留め、閉じたときにまとめて反映する（§27.7.2）。ポップアップを
    /// 開いてから何も選ばずに閉じたとき（<see cref="_trailPopupSelectionMade"/> が false）は画面を変えない。</summary>
    private void CommitTrailPopupSelection()
    {
        if (!_trailPopupSelectionMade)
            return;
        _trailPopupSelectionMade = false;
        if (_vm.Trail.CurrentEntry is { } entry)
            JumpToTrailEntry(entry);
    }

    /// <summary>日付・時刻ポップアップの枠がキーボードフォーカスを失ったとき、移り先がポップアップの外なら
    /// 閉じる（ロストフォーカスで閉じる）。カレンダー・時間帯ボタンなどポップアップ内へのフォーカス移動
    /// （日付・時間帯の選択）では閉じないので、選択はポップアップを開いたまま連続して行える。</summary>
    private void OnTrailDateTimeLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject next && IsWithin(next, TrailDateTimePopupRoot))
            return;
        TrailDateTimePopup.IsOpen = false;
    }
}
