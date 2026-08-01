namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペイン活動インジケータ（袖＝周辺視野）。Terminal / IDE / TS IDE / AI の長い処理を
/// 袖・俯瞰カードのバッジで知らせ、ペインを見ていない間も実行中／承認待ち／完了／失敗を
/// 目の端で追えるようにする。未確認結果は対象ペインが舞台に立つと消える。</summary>
public partial class ShellWindow {
    private enum PaneActivityKind { None, Running, Stopped, Approval, Succeeded, Failed }
    private sealed class TerminalActivityState {
        public bool Running;
        public int? UnseenExitCode;
    }
    private readonly Dictionary<Guid, TerminalActivityState> _terminalActivity = new();
    private sealed class IdeActivityState {
        public bool TaskWasRunning;
        public PaneActivityKind UnseenCompletion;
    }
    private readonly Dictionary<PaneKind, IdeActivityState> _ideActivity = new();
    private readonly Dictionary<PaneKind, (Border Chip, TextBlock Label)> _stageActivityBadges = new();
    private PaneActivityKind _aiUnseenCompletion;

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
        var state = new IdeActivityState { TaskWasRunning = manager.IsTaskRunning };
        _ideActivity[kind] = state;
        manager.PropertyChanged += (_, e) => {
            if (e.PropertyName is nameof(DebugManagerViewModelBase.IsTaskRunning)
                or nameof(DebugManagerViewModelBase.IsBusy)
                or nameof(DebugManagerViewModelBase.IsStopped)
                or nameof(DebugManagerViewModelBase.StatusMessage))
                OnIdeActivityChanged(kind, manager, state);
        };
        manager.SessionStateChanged += () => OnIdeActivityChanged(kind, manager, state);
    }
    private void OnIdeActivityChanged(PaneKind kind, DebugManagerViewModelBase manager, IdeActivityState state) {
        if (state.TaskWasRunning && !manager.IsTaskRunning)
            state.UnseenCompletion = IsPaneWatched(kind)
                ? PaneActivityKind.None
                : IsFailedTaskStatus(manager.StatusMessage)
                    ? PaneActivityKind.Failed
                    : PaneActivityKind.Succeeded;
        if (manager.IsTaskRunning)
            state.UnseenCompletion = PaneActivityKind.None;
        state.TaskWasRunning = manager.IsTaskRunning;
        UpdatePaneActivityBadge(kind);
    }
    private static bool IsFailedTaskStatus(string status)
        => status.Contains("失敗", StringComparison.Ordinal)
           || status.Contains("エラー", StringComparison.Ordinal)
           || status.Contains("中断", StringComparison.Ordinal);

    private void OnAiActivityChanged(bool chatChanged) {
        if (chatChanged && !_vm.AiBar.IsBusy && _vm.AiBar.LastRunSucceeded is { } chatSucceeded)
            _aiUnseenCompletion = IsAiPaneWatched()
                ? PaneActivityKind.None
                : chatSucceeded ? PaneActivityKind.Succeeded : PaneActivityKind.Failed;
        else if (!chatChanged && !_vm.AiBar.Workflow.IsRunning
                 && !string.IsNullOrWhiteSpace(_vm.AiBar.Workflow.RunStatus))
            _aiUnseenCompletion = IsAiPaneWatched()
                ? PaneActivityKind.None
                : _vm.AiBar.Workflow.RunStatus == "完了しました。"
                    ? PaneActivityKind.Succeeded
                    : PaneActivityKind.Failed;

        if (_vm.AiBar.IsBusy || _vm.AiBar.Workflow.IsRunning)
            _aiUnseenCompletion = PaneActivityKind.None;
        UpdatePaneActivityBadge(PaneKind.Ai);
    }
    private void HookTerminalActivity(TerminalTab tab)
        => tab.View.ShellCommandActivity += (_, e) => OnTerminalShellActivity(tab.Id, e);
    private void ForgetTerminalActivity(Guid tabId) {
        if (_terminalActivity.Remove(tabId))
            UpdatePaneActivityBadge(PaneKind.Terminal);
    }
    private void OnTerminalShellActivity(Guid tabId, ShellCommandActivityEventArgs e) {
        if (!_terminalActivity.TryGetValue(tabId, out var state))
            _terminalActivity[tabId] = state = new TerminalActivityState();
        switch (e.Phase) {
            case ShellCommandPhase.CommandExecuted:
                state.Running = true;
                state.UnseenExitCode = null;
                break;
            case ShellCommandPhase.CommandDone:
                if (!state.Running)
                    return;
                state.Running = false;
                state.UnseenExitCode = IsTerminalPaneWatched() ? null : (e.ExitCode ?? 0);
                break;
            default:
                return; // PromptStart / CommandStart は表示に影響しない
        }
        UpdatePaneActivityBadge(PaneKind.Terminal);
    }
    private bool IsPaneWatched(PaneKind kind)
        => _stageActive
            ? _stagePane == kind && !_overviewActive
            : IsPaneVisible(kind);
    private bool IsTerminalPaneWatched() => IsPaneWatched(PaneKind.Terminal);
    private bool IsAiPaneWatched() => IsPaneWatched(PaneKind.Ai);
    private void MarkPaneActivitySeen(PaneKind kind) {
        if (kind == PaneKind.Terminal) {
            foreach (var state in _terminalActivity.Values)
                state.UnseenExitCode = null;
        } else if (kind == PaneKind.Ai) {
            _aiUnseenCompletion = PaneActivityKind.None;
        } else if (_ideActivity.TryGetValue(kind, out var ideState)) {
            ideState.UnseenCompletion = PaneActivityKind.None;
        } else {
            return;
        }
        UpdatePaneActivityBadge(kind);
    }
    private PaneActivityKind AggregateTerminalActivity(out int exitCode) {
        exitCode = 0;
        if (_terminalActivity.Values.Any(s => s.Running))
            return PaneActivityKind.Running;
        var failed = _terminalActivity.Values.FirstOrDefault(s => s.UnseenExitCode is > 0);
        if (failed is not null) {
            exitCode = failed.UnseenExitCode!.Value;
            return PaneActivityKind.Failed;
        }
        return _terminalActivity.Values.Any(s => s.UnseenExitCode == 0)
            ? PaneActivityKind.Succeeded
            : PaneActivityKind.None;
    }
    private void UpdatePaneActivityBadge(PaneKind kind) {
        if (!_stageActivityBadges.TryGetValue(kind, out var badge))
            return;
        var (chip, label) = badge;
        var exitCode = 0;
        var activity = kind switch {
            PaneKind.Terminal => AggregateTerminalActivity(out exitCode),
            PaneKind.Debug or PaneKind.TsIde => AggregateIdeActivity(kind),
            PaneKind.Ai => AggregateAiActivity(),
            _ => PaneActivityKind.None,
        };
        switch (activity)
        {
            case PaneActivityKind.Running:
                chip.Visibility = Visibility.Visible;
                chip.Background = (Brush)FindResource("Accent");
                // アクセント塗りの上なので文字色もテーマ連動（白固定だと明るいアクセントで読めない）。
                label.Foreground = (Brush)FindResource("AccentFg");
                label.Text = kind switch {
                    PaneKind.Debug or PaneKind.TsIde when GetIdeManager(kind).IsTaskRunning
                        => $"● {GetIdeTaskLabel(GetIdeManager(kind).StatusMessage)}",
                    PaneKind.Debug or PaneKind.TsIde => "● デバッグ中",
                    _ => "● 実行中",
                };
                break;
            case PaneActivityKind.Stopped:
                chip.Visibility = Visibility.Visible;
                chip.Background = (Brush)FindResource("Accent");
                label.Foreground = (Brush)FindResource("AccentFg");
                label.Text = "● 停止中";
                break;
            case PaneActivityKind.Approval:
                chip.Visibility = Visibility.Visible;
                chip.Background = (Brush)FindResource("Accent");
                label.Foreground = (Brush)FindResource("AccentFg");
                label.Text = "● 承認待ち";
                break;
            case PaneActivityKind.Failed:
                chip.Visibility = Visibility.Visible;
                chip.Background = PaneActivityFailedBrush;
                label.Foreground = Brushes.White;   // 固定の赤地
                label.Text = kind == PaneKind.Terminal ? $"✗ 失敗 {exitCode}" : "✗ 失敗";
                break;
            case PaneActivityKind.Succeeded:
                chip.Visibility = Visibility.Visible;
                chip.Background = PaneActivitySucceededBrush;
                label.Foreground = Brushes.White;   // 固定の緑地
                label.Text = "✓ 完了";
                break;
            default:
                chip.Visibility = Visibility.Collapsed;
                break;
        }
    }
    private DebugManagerViewModelBase GetIdeManager(PaneKind kind)
        => kind == PaneKind.Debug ? _vm.Debug : _vm.TsIde;
    private PaneActivityKind AggregateIdeActivity(PaneKind kind) {
        var manager = GetIdeManager(kind);
        if (manager.IsTaskRunning)
            return PaneActivityKind.Running;
        if (manager.Sessions.Any(s => s.IsBusy))
            return manager.Sessions.Any(s => s.IsStopped)
                ? PaneActivityKind.Stopped
                : PaneActivityKind.Running;
        return _ideActivity.TryGetValue(kind, out var state)
            ? state.UnseenCompletion
            : PaneActivityKind.None;
    }
    private static string GetIdeTaskLabel(string status) {
        var label = status.Trim().TrimEnd('…', '.');
        return string.IsNullOrWhiteSpace(label) ? "実行中" : label;
    }
    private PaneActivityKind AggregateAiActivity() {
        if (_vm.AiBar.IsBusy || _vm.AiBar.Workflow.IsRunning) {
            var waitingApproval = _vm.AiBar.IsBusy
                ? _vm.AiBar.StatusText.Contains("承認待ち", StringComparison.Ordinal)
                : _vm.AiBar.Workflow.Approvals.Count > 0;
            return waitingApproval
                ? PaneActivityKind.Approval
                : PaneActivityKind.Running;
        }
        return _aiUnseenCompletion;
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
