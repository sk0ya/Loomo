namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペイン活動インジケータ（袖＝周辺視野）。Terminal / IDE / TS IDE / AI の長い処理を
/// 袖・俯瞰カードのバッジで知らせ、ペインを見ていない間も実行中／承認待ち／完了／失敗を
/// 目の端で追えるようにする。未確認結果は対象ペインが舞台に立つと消える。</summary>
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
    private void HookTerminalActivity(TerminalTab tab)
        => tab.View.ShellCommandActivity += (_, e) => OnTerminalShellActivity(tab.Id, e);
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
        var exitCode = 0;
        var activity = kind switch {
            PaneKind.Terminal => _paneActivity.AggregateTerminal(out exitCode),
            PaneKind.Debug or PaneKind.TsIde => AggregateIdeActivity(kind),
            PaneKind.Ai => PaneActivityPresentation.AggregateAi(
                _vm.AiBar.IsBusy, _vm.AiBar.Workflow.IsRunning, _vm.AiBar.StatusText,
                _vm.AiBar.Workflow.Approvals.Count, _paneActivity.AiUnseenCompletion),
            _ => PaneActivityKind.None,
        };
        UpdateDockBarBadge(kind, activity);   // 袖なしのドックでは帯のアイコンが周辺視野（§24.1）
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
            exitCode, manager?.StatusMessage ?? "");
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
