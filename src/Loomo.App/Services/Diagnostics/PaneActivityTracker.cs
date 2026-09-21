namespace sk0ya.Loomo.App.Services;

/// <summary>Terminal・IDE・AI の未確認活動状態と遷移を保持する。</summary>
internal sealed class PaneActivityTracker
{
    private sealed class TerminalState
    {
        public bool Running;
        public int? UnseenExitCode;
    }

    private sealed class IdeState
    {
        public bool TaskWasRunning;
        public PaneActivityKind UnseenCompletion;
    }

    private readonly Dictionary<Guid, TerminalState> _terminals = new();
    private readonly Dictionary<PaneKind, IdeState> _ides = new();

    public PaneActivityKind AiUnseenCompletion { get; private set; }

    public void TrackIdeTask(PaneKind kind, bool isRunning)
        => _ides[kind] = new IdeState { TaskWasRunning = isRunning };

    public void IdeTaskChanged(
        PaneKind kind, bool isRunning, bool paneWatched, string status)
    {
        if (!_ides.TryGetValue(kind, out var state))
            return;
        var next = PaneActivityPresentation.IdeTaskChanged(
            state.TaskWasRunning, isRunning, paneWatched, status, state.UnseenCompletion);
        state.TaskWasRunning = next.TaskWasRunning;
        state.UnseenCompletion = next.UnseenCompletion;
    }

    public void AiActivityChanged(
        bool chatChanged, bool chatBusy, bool? chatSucceeded,
        bool workflowRunning, string workflowStatus, bool paneWatched)
    {
        if (chatChanged && !chatBusy && chatSucceeded is { } succeeded)
            AiUnseenCompletion = PaneActivityPresentation.Completion(paneWatched, succeeded);
        else if (!chatChanged && !workflowRunning && !string.IsNullOrWhiteSpace(workflowStatus))
            AiUnseenCompletion = PaneActivityPresentation.Completion(
                paneWatched, workflowStatus == "完了しました。");

        if (chatBusy || workflowRunning)
            AiUnseenCompletion = PaneActivityKind.None;
    }

    public bool TerminalCommandChanged(
        Guid tabId, bool commandExecuted, bool commandDone, int? exitCode, bool paneWatched)
    {
        if (!_terminals.TryGetValue(tabId, out var state))
            _terminals[tabId] = state = new TerminalState();
        var next = PaneActivityPresentation.TerminalCommandChanged(
            state.Running, state.UnseenExitCode, commandExecuted, commandDone, exitCode, paneWatched);
        if (!next.ShouldRefresh)
            return false;
        state.Running = next.Running;
        state.UnseenExitCode = next.UnseenExitCode;
        return true;
    }

    public bool ForgetTerminal(Guid tabId) => _terminals.Remove(tabId);

    public bool MarkSeen(PaneKind kind)
    {
        if (kind == PaneKind.Terminal)
        {
            foreach (var state in _terminals.Values)
                state.UnseenExitCode = null;
            return true;
        }
        if (kind == PaneKind.Ai)
        {
            AiUnseenCompletion = PaneActivityKind.None;
            return true;
        }
        if (!_ides.TryGetValue(kind, out var ideState))
            return false;
        ideState.UnseenCompletion = PaneActivityKind.None;
        return true;
    }

    public PaneActivityKind AggregateTerminal(out int exitCode)
        => PaneActivityPresentation.AggregateTerminal(
            _terminals.Values.Select(state => (state.Running, state.UnseenExitCode)), out exitCode);

    public PaneActivityKind AggregateIde(
        PaneKind kind, bool taskRunning, bool sessionBusy, bool sessionStopped)
        => PaneActivityPresentation.AggregateIde(
            taskRunning, sessionBusy, sessionStopped,
            _ides.TryGetValue(kind, out var state) ? state.UnseenCompletion : PaneActivityKind.None);
}
