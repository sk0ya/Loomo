using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: 軌跡（操作ログ）バーの配線。エディタのファイル活性化・ブラウザ遷移・
/// ペイン／パネル切替を <see cref="TrailViewModel"/> へ記録し、ドットのクリックや
/// バー上のホイール（現在地の前後移動）でその地点へ戻る。バー左端の日付クリック→カレンダーで
/// 過去の日の軌跡も表示できる。アイデア.md「Semantic Depth」構想の Thread Rail の種。</summary>
public partial class ShellWindow
{
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
        _vm.Trail.JumpRequested += (_, entry) => JumpToTrailEntry(entry);
        // 追記・現在地移動でドットが見える位置へ追従スクロールする。
        _vm.Trail.Entries.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(ScrollTrailCurrentIntoView), DispatcherPriority.Loaded);
        // 起動のクリティカルパスを避けて、今日の軌跡を SQLite から遅延読込する。
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => _vm.Trail.EnsureLoaded()));
    }

    // ===== 記録 =====

    /// <summary>エディタタブの活性化を軌跡へ記録する（無題・仮想ドキュメントは対象外）。</summary>
    private void RecordTrailEditorTab(EditorTab tab)
    {
        if (_trailSuppressed)
            return;

        var path = tab.PeekFilePath;
        if (string.IsNullOrWhiteSpace(path) || tab.PeekIsVirtual)
            return;

        RefreshLatestTrailFilePosition();

        var line = -1;
        var column = -1;
        if (tab.IsRealized)
        {
            line = tab.Control.Caret.Line;
            column = tab.Control.Caret.Column;
        }
        _vm.Trail.RecordFile(path, line, column);
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
        if (_trailSuppressed || string.IsNullOrWhiteSpace(url))
            return;
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url, DefaultBrowserUrl, StringComparison.OrdinalIgnoreCase))
            return;

        _vm.Trail.RecordBrowser(url, title);
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
            RefreshLatestTrailFilePosition();
            _vm.Trail.RecordPane(kind.ToString(), PaneDisplayName(kind));
        };
        return timer;
    }

    /// <summary>サイドバーのパネル切替を軌跡へ記録する。</summary>
    private void RecordTrailPanel(SidebarPanel panel)
    {
        if (_trailSuppressed)
            return;
        _vm.Trail.RecordPanel(panel.ToString(), PanelDisplayName(panel));
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

    private async void JumpToTrailEntry(TrailEntryViewModel entry)
    {
        // 戻る操作で辿った活性化・フォーカス移動は新しい地点として記録しない。
        var saved = _trailSuppressed;
        _trailSuppressed = true;
        try
        {
            switch (entry.Kind)
            {
                case TrailEntryKind.File:
                    if (!File.Exists(entry.Target))
                        return;   // 消えたファイルはそっと何もしない（ブランチ切替等で戻ることもある）
                    await OpenFileInNewEditorTabAsync(entry.Target);
                    FocusPane(PaneKind.Editor);
                    if (entry.Line >= 0)
                        _activeEditorTab?.Control.NavigateTo(entry.Line, Math.Max(0, entry.Column));
                    break;

                case TrailEntryKind.Browser:
                    EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
                    FocusPane(PaneKind.Browser);
                    NavigateBrowser(entry.Target);
                    break;

                case TrailEntryKind.Pane:
                    if (Enum.TryParse<PaneKind>(entry.Target, out var pane))
                    {
                        EnsurePaneVisibleOrSwapTopLeft(pane);
                        FocusPane(pane);
                        _trailLastPane = pane;   // 戻った先を「直近のペイン」として同期する
                    }
                    break;

                case TrailEntryKind.Panel:
                    if (Enum.TryParse<SidebarPanel>(entry.Target, out var panel))
                    {
                        _vm.ActivePanel = panel;
                        _vm.IsSidebarVisible = true;
                    }
                    break;
            }
        }
        finally
        {
            _trailSuppressed = saved;
        }
        ScrollTrailCurrentIntoView();
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
