namespace sk0ya.Loomo.App.Services;

/// <summary>Terminal・IDE・AI の未確認活動状態と遷移を保持する。</summary>
internal sealed class PaneActivityTracker
{
    private sealed class TerminalState
    {
        public bool Running;
        public int? UnseenExitCode;
        public ClaudeCodeStatus? Agent;
        public bool AgentUnseenTurn;
        public long AgentStamp;
    }

    private sealed class IdeState
    {
        public bool TaskWasRunning;
        public PaneActivityKind UnseenCompletion;
    }

    private readonly Dictionary<Guid, TerminalState> _terminals = new();
    private readonly Dictionary<PaneKind, IdeState> _ides = new();
    private long _agentClock;

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
        if (commandDone)
        {
            // claude が終わればシェルのプロンプトに戻る。タイトルが戻るのを待たずに捨てる。
            state.Agent = null;
            state.AgentUnseenTurn = false;
        }
        return true;
    }

    /// <summary>タイトル変化を Claude Code の状態として取り込む。戻り値の <c>TurnEnded</c> は
    /// 作業中→待機の遷移（ウィンドウが裏にあるときの呼び戻しに使う）。</summary>
    public ClaudeCodeTitleTransition TerminalTitleChanged(Guid tabId, string? title, bool paneWatched)
    {
        var status = ClaudeCodeTitle.Parse(title);
        if (!_terminals.TryGetValue(tabId, out var state))
        {
            if (status is null)
                return default;
            _terminals[tabId] = state = new TerminalState();
        }
        // Terminal のタブ見出しはパスらしい部分（/ や \ を含む）を捨ててプロファイル名に戻すので、
        // 「◐ Fix src/foo.cs」は読めない見出しとして届く。claude がまだ実行中（OSC133 の C を見て D を
        // 見ていない）なら終了ではないので、今の状態を保つ。終了は D で捨てる（TerminalCommandChanged）。
        if (status is null && state.Running)
            return default;
        var next = PaneActivityPresentation.ClaudeTitleChanged(
            state.Agent, state.AgentUnseenTurn, status, paneWatched);
        state.Agent = next.Agent;
        state.AgentUnseenTurn = next.AgentUnseenTurn;
        if (next.Agent is not null)
            state.AgentStamp = ++_agentClock;
        return next;
    }

    public bool ForgetTerminal(Guid tabId) => _terminals.Remove(tabId);

    /// <summary>素材を Claude Code へ送るときの宛先タブ。いま前に出ているタブが Claude なら
    /// それ、でなければ最後に Claude の印が動いたタブ。Claude が居なければ null。</summary>
    public Guid? AgentTabFor(Guid? activeTabId)
    {
        if (activeTabId is { } active && _terminals.TryGetValue(active, out var current) && current.Agent is not null)
            return active;
        Guid? best = null;
        long bestStamp = -1;
        foreach (var (id, state) in _terminals)
        {
            if (state.Agent is not null && state.AgentStamp > bestStamp)
                (best, bestStamp) = (id, state.AgentStamp);
        }
        return best;
    }

    public bool MarkSeen(PaneKind kind)
    {
        if (kind == PaneKind.Terminal)
        {
            foreach (var state in _terminals.Values)
            {
                state.UnseenExitCode = null;
                state.AgentUnseenTurn = false;
            }
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
            _terminals.Values.Select(state =>
                (state.Running, state.UnseenExitCode, state.Agent, state.AgentUnseenTurn)), out exitCode);

    public PaneActivityKind AggregateIde(
        PaneKind kind, bool taskRunning, bool sessionBusy, bool sessionStopped)
        => PaneActivityPresentation.AggregateIde(
            taskRunning, sessionBusy, sessionStopped,
            _ides.TryGetValue(kind, out var state) ? state.UnseenCompletion : PaneActivityKind.None);
}
