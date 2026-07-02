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

    /// <summary>true の間は軌跡へ記録しない。ワークスペース切替・復元による機械的なタブ活性化と、
    /// 軌跡からの「戻る」自体（戻った先を新しい地点として積まない）で立てる。</summary>
    private bool _trailSuppressed;

    /// <summary>直近に記録したペイン（同じペイン内のフォーカス移動でドットを増やさない）。</summary>
    private PaneKind? _trailLastPane;

    /// <summary>ペイン切替の確定待ち（フォーカス奪い合いノイズを1個に畳むデバウンス）。</summary>
    private DispatcherTimer? _trailPaneCommitTimer;
    private PaneKind? _trailPendingPane;

    /// <summary>ホイールでの現在地移動を1回のジャンプへ畳むデバウンス。</summary>
    private DispatcherTimer? _trailScrubTimer;
    private TrailEntryViewModel? _trailScrubTarget;

    private void InitializeTrail()
    {
        RegisterTrailJumps();
        _vm.Trail.JumpRequested += (_, entry) => JumpToTrailEntry(entry);
        // 追記・現在地移動でドットが見える位置へ追従スクロールする。
        _vm.Trail.Entries.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(ScrollTrailCurrentIntoView), DispatcherPriority.Loaded);
        // 日付クリックのトグル判定用：StaysOpen=False のポップアップは「開いたままボタンを再クリック」
        // すると Click が届く前に外側クリックとして閉じるため、閉じた時刻を覚えて直後の再オープンを抑止する。
        TrailCalendarPopup.Closed += (_, _) => _trailCalendarClosedAt = DateTime.UtcNow;
        // 起動のクリティカルパスを避けて、今日の軌跡を SQLite から遅延読込する。
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => _vm.Trail.EnsureLoaded()));
    }

    /// <summary>カレンダーポップアップが最後に閉じた時刻（日付クリックのトグル判定）。</summary>
    private DateTime _trailCalendarClosedAt;

    // ===== 記録 =====

    /// <summary>あらゆる軌跡ソース共通の記録入口。記録抑制（復元・ジャンプ中）の判定と、
    /// 新しい地点を積む前に「いま離れるファイル」のカーソルを離脱位置へ上書きする処理を
    /// ここへ一本化する。各ソースは対象の抽出（ファイルパス・URL 等）だけを行い、
    /// <paramref name="record"/> で実際の <see cref="TrailViewModel"/> 呼び出しを渡す。</summary>
    private void RecordTrail(Action record)
    {
        if (_trailSuppressed)
            return;
        RefreshLatestTrailFilePosition();
        record();
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
        RecordTrail(() => _vm.Trail.RecordFile(path, line, column));
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

    /// <summary>ブラウザ遷移を軌跡へ記録する。既定ページ（新規タブの初期表示）と about: は対象外。</summary>
    private void RecordTrailBrowser(string? url, string? title)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url, DefaultBrowserUrl, StringComparison.OrdinalIgnoreCase))
            return;

        RecordTrail(() => _vm.Trail.RecordBrowser(url, title));
    }

    /// <summary>ターミナルタブの活性化を、再起動後も同じタブへ戻れる ID 付きで記録する。</summary>
    private void RecordTrailTerminalTab(TerminalTab tab)
    {
        var label = _vm.Tabs.TerminalTabs.FirstOrDefault(t => t.Id == tab.Id)?.Title;
        if (string.IsNullOrWhiteSpace(label))
            label = string.IsNullOrWhiteSpace(tab.View.HeaderTitle) ? "ターミナル" : tab.View.HeaderTitle;
        RecordTrail(() => _vm.Trail.RecordTerminal(tab.Id, label));
    }

    /// <summary>フォーカスが別ペインへ移ったことを軌跡へ記録する（同一ペイン内の移動は対象外）。
    /// WebView2 の実体化やプレビュー更新はフォーカスを奪い合って Editor⇄Browser⇄プレビューの
    /// 往復イベントを大量に起こすため、即時には記録せず「同じペインに一定時間とどまった」ときだけ
    /// 1個のドットとして確定する（<see cref="_trailPaneCommitTimer"/>）。</summary>
    private void RecordTrailPane(PaneKind kind)
    {
        if (_trailSuppressed)
            return;
        if (_trailLastPane == kind)
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
            // 確定時点でまだそのペインにフォーカスがあるときだけ記録する（奪い合いの残骸を捨てる）。
            if (_trailSuppressed || _trailLastPane == kind || _focusedRegion?.Pane != kind)
                return;
            _trailLastPane = kind;
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
            RecordTrail(() => _vm.Trail.RecordPane(kind.ToString(), PaneDisplayName(kind)));
        };
        return timer;
    }

    /// <summary>サイドバーのパネル切替を軌跡へ記録する。</summary>
    private void RecordTrailPanel(SidebarPanel panel)
        => RecordTrail(() => _vm.Trail.RecordPanel(panel.ToString(), PanelDisplayName(panel)));

    // ===== レイアウト（複数ペインのタイル配置） =====

    /// <summary>最後に保存時に観測したレイアウト配置。構造変更時には「いま離れた配置」として記録する基準
    /// （<see cref="PaneLayoutTree.SnapshotsEquivalent"/> は Weight を無視して構造だけを比べる）。
    /// ワークスペース復元・ジャンプ直後はここへ現配置を入れておき、直後の保存で重複ドットを積まない。</summary>
    private PaneNodeSnapshot? _lastRecordedLayout;

    private static readonly JsonSerializerOptions TrailLayoutJson = new();

    /// <summary>メイン領域のペイン配置が構造的に変わったら軌跡へ記録する。<see cref="SaveActiveWorkspaceSnapshotNow"/>
    /// （レイアウト変更・タブ操作・リサイズ等あらゆる保存の choke point）から呼ぶ。複数ペイン表示のときだけ
    /// 記録する。変更後ではなく変更前の配置を積むことで、その四角から実際に元の配置へ戻れる。
    /// 単一ペインはファイル／ペインのドットが代表し、前回と同じ構造なら積まない。</summary>
    private void RecordTrailLayout()
    {
        if (_trailSuppressed || _stageActive || _root is null)
            return;

        var current = ToSnapshot(_root);
        if (_lastRecordedLayout is not { } previous)
        {
            _lastRecordedLayout = current;
            return;
        }

        if (PaneLayoutTree.SnapshotsEquivalent(previous, current))
        {
            // 構造が同じ（リサイズだけ等）ならドットは増やさない。ただし、次に構造が変わった際に
            // 直前の比率へ戻せるよう、復元用スナップショット自体は最新状態へ更新する。
            _lastRecordedLayout = current;
            return;
        }

        // 比較基準は先に現在配置へ進める。Record が別の保存処理を誘発しても重複させない。
        _lastRecordedLayout = current;
        if (VisibleLeafKinds(previous).Take(2).Count() < 2)
            return;   // 離れた配置が単一ペインなら、配置ドットの対象外

        var target = JsonSerializer.Serialize(previous, TrailLayoutJson);
        var label = DescribeLayout(previous);
        RecordTrail(() => _vm.Trail.Record(TrailEntryKind.Layout, target, label));
    }

    /// <summary>復元・ジャンプ直後に、現配置を「記録済みの基準」として覚える（ドットは積まない）。
    /// 直後の遅延保存が同じ配置でドットを積むのを防ぐ。</summary>
    private void SeedTrailLayoutBaseline()
        => _lastRecordedLayout = _root is null ? null : ToSnapshot(_root);

    /// <summary>配置ドットのラベル＝表示中ペインの並び（例「エディタ · ターミナル · ブラウザ」）。</summary>
    private static string DescribeLayout(PaneNodeSnapshot root)
        => string.Join(" · ", VisibleLeafKinds(root).Select(PaneLabel));

    private static IEnumerable<PaneKind> VisibleLeafKinds(PaneNodeSnapshot node)
    {
        if (node.Kind is { } kind)
        {
            if (!node.Hidden)
                yield return kind;
            yield break;
        }
        if (node.Children is null)
            yield break;
        foreach (var child in node.Children)
            foreach (var k in VisibleLeafKinds(child))
                yield return k;
    }

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
        _trailJumps[TrailEntryKind.Layout] = entry => { JumpToLayout(entry); return Task.CompletedTask; };
        _trailJumps[TrailEntryKind.Terminal] = entry => { JumpToTerminal(entry); return Task.CompletedTask; };
    }

    private async void JumpToTrailEntry(TrailEntryViewModel entry)
    {
        if (!_trailJumps.TryGetValue(entry.Kind, out var jump))
            return;

        // 戻る操作で辿った活性化・フォーカス移動は新しい地点として記録しない。
        var saved = _trailSuppressed;
        _trailSuppressed = true;
        try
        {
            await jump(entry);
        }
        finally
        {
            _trailSuppressed = saved;
        }
        ScrollTrailCurrentIntoView();
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
    }

    private void JumpToPanel(TrailEntryViewModel entry)
    {
        if (!Enum.TryParse<SidebarPanel>(entry.Target, out var panel))
            return;
        _vm.ActivePanel = panel;
        _vm.IsSidebarVisible = true;
    }

    private void JumpToTerminal(TrailEntryViewModel entry)
    {
        if (!Guid.TryParse(entry.Target, out var id) || _terminalTabs.All(t => t.Id != id))
            return;   // 閉じられたタブは復元不能なので何もしない
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Terminal);
        ActivateTerminalTab(id);
        FocusPane(PaneKind.Terminal);
    }

    /// <summary>配置ドット：複数ペインのタイル配置ごと復元する。ソロ（ステージ）表示中なら
    /// 先にレイアウトモードへ戻してからタイルを組み直す。</summary>
    private void JumpToLayout(TrailEntryViewModel entry)
    {
        PaneNodeSnapshot? snapshot;
        try { snapshot = JsonSerializer.Deserialize<PaneNodeSnapshot>(entry.Target, TrailLayoutJson); }
        catch { return; }
        if (snapshot is null)
            return;

        if (_stageActive)
            ExitStageMode();
        ApplyPaneLayout(snapshot);
        SeedTrailLayoutBaseline();   // 復元した配置を基準に（直後の保存で重複ドットを積まない）
        if (AllLeaves().FirstOrDefault(l => !l.Hidden)?.Kind is { } first)
            FocusPane(first);
        SaveActiveWorkspaceSnapshot();
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

    /// <summary>現在地のドットが見えるよう水平スクロールを追従させる（無ければ右端＝最新へ）。</summary>
    private void ScrollTrailCurrentIntoView()
    {
        var index = _vm.Trail.CurrentIndex;
        if (index < 0)
        {
            TrailScroll.ScrollToRightEnd();
            return;
        }
        // ドット1個の実効幅（TrailDotButton の幅）。中央に寄せる。
        const double dotWidth = 14;
        var target = index * dotWidth - TrailScroll.ViewportWidth / 2 + dotWidth / 2;
        TrailScroll.ScrollToHorizontalOffset(Math.Max(0, target));
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
}
