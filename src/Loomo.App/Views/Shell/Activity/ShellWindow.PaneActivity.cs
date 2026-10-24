namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペイン活動インジケータ（袖＝周辺視野）。Terminal / IDE / TS IDE / AI の長い処理を
/// 袖・俯瞰カードのバッジで知らせ、ペインを見ていない間も実行中／承認待ち／完了／失敗を
/// 目の端で追えるようにする。ターミナルで動く Claude Code の作業中／入力待ちもここに出す（§24.1.1）。
/// 未確認結果は対象ペインが舞台に立つと消える。</summary>
public partial class ShellWindow {
    private readonly PaneActivityTracker _paneActivity = new();
    private readonly Dictionary<PaneKind, (Border Chip, TextBlock Label)> _stageActivityBadges = new();

    private void HookAiActivity() {
        _vm.AiBar.PropertyChanged += (_, e) => {
            if (e.PropertyName is nameof(AiBarViewModel.IsBusy)
                or nameof(AiBarViewModel.StatusText)
                or nameof(AiBarViewModel.LastRunSucceeded))
                OnAiActivityChanged(chatChanged: true);
        };
        _vm.AiBar.Workflow.PropertyChanged += (_, e) => {
            if (e.PropertyName is nameof(WorkflowViewModel.IsRunning)
                or nameof(WorkflowViewModel.RunStatus))
                OnAiActivityChanged(chatChanged: false);
        };
        _vm.AiBar.Workflow.Approvals.CollectionChanged += (_, _) =>
            UpdatePaneActivityBadge(PaneKind.Ai);
    }
    private void HookIdeActivity(PaneKind kind, DebugManagerViewModelBase manager) {
        _paneActivity.TrackIdeTask(kind, manager.IsTaskRunning);
        manager.PropertyChanged += (_, e) => {
            if (e.PropertyName is nameof(DebugManagerViewModelBase.IsTaskRunning)
                or nameof(DebugManagerViewModelBase.IsBusy)
                or nameof(DebugManagerViewModelBase.IsStopped)
                or nameof(DebugManagerViewModelBase.StatusMessage))
                OnIdeActivityChanged(kind, manager);
        };
        manager.SessionStateChanged += () => OnIdeActivityChanged(kind, manager);
    }
    private void OnIdeActivityChanged(PaneKind kind, DebugManagerViewModelBase manager) {
        _paneActivity.IdeTaskChanged(kind, manager.IsTaskRunning, IsPaneWatched(kind), manager.StatusMessage);
        UpdatePaneActivityBadge(kind);
    }

    private void OnAiActivityChanged(bool chatChanged) {
        _paneActivity.AiActivityChanged(
            chatChanged, _vm.AiBar.IsBusy, _vm.AiBar.LastRunSucceeded,
            _vm.AiBar.Workflow.IsRunning, _vm.AiBar.Workflow.RunStatus, IsAiPaneWatched());
        UpdatePaneActivityBadge(PaneKind.Ai);
    }
    /// <summary>可視ターミナルへ cd やインストールを打ち込む経路（TerminalService）に、前面が埋まっているかを
    /// 教える。Claude Code が居るタブへ cd を打てば、それが Claude への頼みごととして送信される。</summary>
    private void WireTerminalForegroundBusy()
        => _terminal.IsForegroundBusy = view =>
            _terminalTabs.FirstOrDefault(t => ReferenceEquals(t.View, view)) is { } tab
            && _paneActivity.IsTerminalBusy(tab.Id);
    private void HookTerminalActivity(TerminalTab tab) {
        tab.View.ShellCommandActivity += (_, e) => OnTerminalShellActivity(tab.Id, e);
        // 見出し（HeaderTitleChanged）ではなく生のタイトル：見出しは / \ を含む部分を捨てるので
        // 「◐ Fix src/foo.cs」が読めなくなる（§38.6）。
        tab.View.WindowTitleChanged += (_, title) => OnTerminalTitleChanged(tab.Id, title);
        // コマンドが出す進捗（OSC 9;4）とベル（BEL）も袖へ（§24.35）。
        tab.View.TaskbarProgressChanged += (_, e) => OnTerminalProgressChanged(tab.Id, e);
        tab.View.BellRang += (_, _) => OnTerminalBellRang(tab.Id);
        // プログラムが自分で言う状態（OSC 7501）。Claude Code は承認待ち・作業中・入力待ちを申告する（§24.61）。
        tab.View.ProgramStatusChanged += (_, e) => OnTerminalProgramStatusChanged(tab.Id, e.Records);
        // コマンドが出す通知（OSC 9・OSC 777）はタブの中の 5 秒のトーストではなく、部屋の通知欄へ（§24.49）。
        tab.View.NotificationReceived += (_, e) => OnTerminalNotification(tab.Id, e);
    }
    /// <summary>ターミナルのコマンドが出した通知を、出所（タブ名）と「そのタブへ」付きで通知欄に載せる（§24.49）。
    /// 見ていないタブからならベルと同じく袖に印を残し、Loomo が裏ならタスクバーを点滅させる。</summary>
    private void OnTerminalNotification(Guid tabId, TerminalNotificationEventArgs e) {
        e.Handled = true;
        var source = TerminalNotificationPresentation.Source(TerminalTabTitle(tabId));
        NotificationService.Post(
            TerminalNotificationPresentation.Message(e.Title, e.Body), NotificationKind.Info, source,
            new NotificationAction("そのタブへ", () => GoToTerminalTab(tabId)));
        OnTerminalBellRang(tabId);
    }
    private void GoToTerminalTab(Guid tabId) {
        if (_terminalTabs.All(t => t.Id != tabId))
            return;   // 知らせを見るまでの間に閉じられた
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Terminal);
        ActivateTerminalTab(tabId);
        FocusPane(PaneKind.Terminal);
    }
    private void OnTerminalProgressChanged(Guid tabId, TaskbarProgressChangedEventArgs e) {
        var progress = new TerminalProgress(e.State switch {
            TaskbarProgressState.Normal => TerminalProgressKind.Normal,
            TaskbarProgressState.Error => TerminalProgressKind.Error,
            TaskbarProgressState.Indeterminate => TerminalProgressKind.Indeterminate,
            TaskbarProgressState.Warning => TerminalProgressKind.Warning,
            _ => TerminalProgressKind.None,
        }, Math.Clamp(e.Progress, 0, 100));
        if (_paneActivity.TerminalProgressChanged(tabId, progress))
            UpdatePaneActivityBadge(PaneKind.Terminal);
    }
    private void OnTerminalBellRang(Guid tabId) {
        if (!_paneActivity.TerminalBellRang(tabId, IsTerminalPaneWatched()))
            return;
        UpdatePaneActivityBadge(PaneKind.Terminal);
        if (!IsActive)
            FlashTaskbarUntilForeground();
    }
    /// <summary>ターミナルの進捗を Loomo のタスクバーのボタンにも出す（§24.35）。Loomo の外で作業していても見える。</summary>
    private void UpdateTaskbarProgress(PaneActivityKind activity, TerminalProgress progress) {
        var info = TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();
        info.ProgressState = activity switch {
            PaneActivityKind.ProgressError => System.Windows.Shell.TaskbarItemProgressState.Error,
            PaneActivityKind.Progress => progress.Kind switch {
                TerminalProgressKind.Indeterminate => System.Windows.Shell.TaskbarItemProgressState.Indeterminate,
                TerminalProgressKind.Warning => System.Windows.Shell.TaskbarItemProgressState.Paused,
                _ => System.Windows.Shell.TaskbarItemProgressState.Normal,
            },
            _ => System.Windows.Shell.TaskbarItemProgressState.None,
        };
        info.ProgressValue = progress.Percent / 100.0;
    }
    /// <summary>タイトルの印から Claude Code の作業中／入力待ちを拾う（§24.1.1）。ターンが終わったとき
    /// Loomo が裏に回っていれば、タスクバーを点滅させて呼び戻す（前面に出たら止まる）。</summary>
    private void OnTerminalTitleChanged(Guid tabId, string title)
        => ApplyClaudeTransition(tabId, _paneActivity.TerminalTitleChanged(tabId, title, IsTerminalPaneWatched()));

    /// <summary>プログラムが OSC 7501 で状態を申告した（§24.61）。Claude Code の申告はタイトルの印と同じ後始末へ。
    /// Claude 以外のプログラムが見ていない間に人を待ち始めたら、ベルと同じく袖に印を残し、Loomo が裏ならタスクバーを点滅させる。</summary>
    private void OnTerminalProgramStatusChanged(Guid tabId, IReadOnlyList<ProgramStatusRecord> records) {
        var transition = _paneActivity.TerminalProgramStatusChanged(tabId, records, IsTerminalPaneWatched());
        if (transition.ProgramsChanged)
            RequestRunningRefresh();
        if (transition.HumanTurnStarted)
            OnTerminalBellRang(tabId);
        ApplyClaudeTransition(tabId, transition.Claude);
    }

    private void ApplyClaudeTransition(Guid tabId, ClaudeCodeTitleTransition transition) {
        if (transition.ShouldRefresh)
        {
            UpdatePaneActivityBadge(PaneKind.Terminal);
            // 申告の変化は見出しのタイトルを動かさないので、印を付け直すにはこちらから組み直す（§24.61）。
            if (_terminalTabs.FirstOrDefault(t => t.Id == tabId) is { } tab)
                UpdateTerminalTab(tab, tab.View.HeaderTitle);
        }
        if (!transition.TurnEnded)
            return;
        // Claude が書き換えたファイルを開いたままだと、古い本文のまま保存して Claude の変更を消しうる。
        // 書き換えはタブの見張り（§24.57）が拾うが、監視が落ちていた・取りこぼした場合の網としてターンの終わりにも確かめる。
        // 未保存の編集があるタブは読み直さない（ReloadExistingTabIfChangedAsync の約束）。
        _ = RefreshOpenEditorTabsFromDiskAsync();
        if (_activeTerminalTab?.Id == tabId)
            RefreshClaudeContext();
        if (!IsActive)
            FlashTaskbarUntilForeground();
    }
    private void FlashTaskbarUntilForeground() {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        var info = new FlashWindowInfo {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FlashWindowInfo>(),
            hwnd = handle,
            dwFlags = FlashwTray | FlashwTimerNoFg,
        };
        FlashWindowEx(ref info);
    }
    private const uint FlashwTray = 0x2;
    private const uint FlashwTimerNoFg = 0xC;
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FlashWindowInfo {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);
    private void ForgetTerminalActivity(Guid tabId) {
        if (_paneActivity.ForgetTerminal(tabId))
            UpdatePaneActivityBadge(PaneKind.Terminal);
    }
    private void OnTerminalShellActivity(Guid tabId, ShellCommandActivityEventArgs e) {
        var shouldRefresh = _paneActivity.TerminalCommandChanged(
            tabId,
            e.Phase == ShellCommandPhase.CommandExecuted,
            e.Phase == ShellCommandPhase.CommandDone,
            e.ExitCode, IsTerminalPaneWatched());
        if (!shouldRefresh)
            return; // PromptStart / CommandStart と実行中でない CommandDone は表示に影響しない
        UpdatePaneActivityBadge(PaneKind.Terminal);
    }
    private bool IsPaneWatched(PaneKind kind)
        => _stageActive ? _stagePane == kind && !_overviewActive
        : _dockActive ? IsDockPaneShown(kind)
        : IsPaneVisible(kind);
    private bool IsTerminalPaneWatched() => IsPaneWatched(PaneKind.Terminal);
    private bool IsAiPaneWatched() => IsPaneWatched(PaneKind.Ai);
    private void MarkPaneActivitySeen(PaneKind kind) {
        if (!_paneActivity.MarkSeen(kind))
            return;
        UpdatePaneActivityBadge(kind);
    }
    private void UpdatePaneActivityBadge(PaneKind kind) {
        if (kind == PaneKind.Terminal) {
            RefreshWorktreeAgentMarks();
            RequestTerminalSupportRefresh();   // Claude の状態・前面のコマンドの変化はターミナルサポートにも効く（§39）
        }
        RequestRunningRefresh();   // Claude の状態・IDE の作業・AI の応答の変化は、動いているもの面にも効く（§24.54）
        var exitCode = 0;
        TerminalProgress progress = default;
        var activity = kind switch {
            PaneKind.Terminal => _paneActivity.AggregateTerminal(out exitCode, out progress),
            PaneKind.Debug or PaneKind.TsIde => AggregateIdeActivity(kind),
            PaneKind.Ai => PaneActivityPresentation.AggregateAi(
                _vm.AiBar.IsBusy, _vm.AiBar.Workflow.IsRunning, _vm.AiBar.StatusText,
                _vm.AiBar.Workflow.Approvals.Count, _paneActivity.AiUnseenCompletion),
            _ => PaneActivityKind.None,
        };
        UpdateDockBarBadge(kind, activity);   // 袖なしのドックでは帯のアイコンが周辺視野（§24.1）
        if (kind == PaneKind.Terminal)
            UpdateTaskbarProgress(activity, progress);
        if (!_stageActivityBadges.TryGetValue(kind, out var badge))
            return;
        var (chip, label) = badge;
        switch (PaneActivityPresentation.BadgeTone(activity))
        {
            case PaneActivityBadgeTone.Accent:
                chip.Visibility = Visibility.Visible;
                chip.Background = (Brush)FindResource("Accent");
                // アクセント塗りの上なので文字色もテーマ連動（白固定だと明るいアクセントで読めない）。
                label.Foreground = (Brush)FindResource("AccentFg");
                break;
            case PaneActivityBadgeTone.Failed:
                chip.Visibility = Visibility.Visible;
                chip.Background = PaneActivityFailedBrush;
                label.Foreground = Brushes.White;   // 固定の赤地
                break;
            case PaneActivityBadgeTone.Succeeded:
                chip.Visibility = Visibility.Visible;
                chip.Background = PaneActivitySucceededBrush;
                label.Foreground = Brushes.White;   // 固定の緑地
                break;
            default:
                chip.Visibility = Visibility.Collapsed;
                break;
        }
        var manager = kind is PaneKind.Debug or PaneKind.TsIde ? GetIdeManager(kind) : null;
        label.Text = PaneActivityPresentation.BadgeText(activity,
            kind == PaneKind.Terminal, manager is not null, manager?.IsTaskRunning == true,
            exitCode, manager?.StatusMessage ?? "", progress);
    }
    private DebugManagerViewModelBase GetIdeManager(PaneKind kind)
        => kind == PaneKind.Debug ? _vm.Debug : _vm.TsIde;
    private PaneActivityKind AggregateIdeActivity(PaneKind kind) {
        var manager = GetIdeManager(kind);
        return _paneActivity.AggregateIde(
            kind,
            manager.IsTaskRunning, manager.Sessions.Any(session => session.IsBusy),
            manager.Sessions.Any(session => session.IsStopped));
    }
    private static readonly Brush PaneActivitySucceededBrush =
        new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x5B));
    private static readonly Brush PaneActivityFailedBrush =
        new SolidColorBrush(Color.FromRgb(0xD9, 0x53, 0x4D));
    private void AttachActivityBadge(Grid cardRoot, PaneKind kind, bool isOverview) {
        if (kind is not (PaneKind.Terminal or PaneKind.Debug or PaneKind.TsIde or PaneKind.Ai))
            return;
        var label = new TextBlock {
            FontSize = isOverview ? 12 : 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, };
        var chip = new Border {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 5, 0), Padding = new Thickness(7, 2, 7, 2), CornerRadius = new CornerRadius(9), Visibility = Visibility.Collapsed, IsHitTestVisible = false, Child = label, };
        cardRoot.Children.Add(chip);
        _stageActivityBadges[kind] = (chip, label);
        UpdatePaneActivityBadge(kind);
    }
}
