using Terminal.Tabs;

namespace sk0ya.Loomo.App.Services;

/// <summary>OSC 7501 の申告を取り込んだ結果（§24.61）。<see cref="Claude"/> はタイトルの印と同じ後始末に回す。
/// <see cref="HumanTurnStarted"/> は Claude 以外のプログラムが見ていない間に人を待ち始めた（袖に印・タスクバーの点滅）。</summary>
internal readonly record struct ProgramStatusTransition(
    ClaudeCodeTitleTransition Claude, bool HumanTurnStarted, bool ProgramsChanged);

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
        public bool AgentApproval;
        public string? SessionId;
        public string? TranscriptPath;
        /// <summary>Claude Code が付けた題（印を外したタイトル）。一覧に出す。</summary>
        public string? AgentTitle;
        /// <summary>コマンドが OSC 9;4 で出している進捗（§24.35）。</summary>
        public TerminalProgress Progress;
        /// <summary>見ていない間に鳴ったベル（§24.35）。</summary>
        public bool UnseenBell;
        /// <summary>Claude Code が OSC 7501 で状態を申告している（§24.61）。その間はタイトルの印で状態を動かさない。</summary>
        public bool AgentReportsStatus;
        /// <summary>Claude 以外のプログラムの、いま動いている申告（§24.61）。</summary>
        public IReadOnlyList<ProgramStatusRecord> Programs = [];
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
            // 進捗を 0 に戻さずに終わるコマンドがある（途中で Ctrl+C など）。終わったら捨てる。
            state.Progress = default;
            // claude が終わればシェルのプロンプトに戻る。タイトルが戻るのを待たずに捨てる。
            state.Agent = null;
            state.AgentUnseenTurn = false;
            state.AgentApproval = false;
            state.AgentTitle = null;
            state.AgentReportsStatus = false;
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
        // claude がまだ実行中（OSC133 の C を見て D を見ていない）なのに印の無いタイトルが来たら、終了ではない
        // （claude の中で動いた別のプログラムがタイトルを書いた等）ので今の状態を保つ。終了は D で捨てる
        // （TerminalCommandChanged）。
        if (status is null && state.Running)
            return default;
        // 申告（OSC 7501）がある間は、状態は申告から決める。タイトルからは題だけ拾う（§24.61）。
        if (state.AgentReportsStatus)
        {
            if (status is not null)
                state.AgentTitle = ClaudeCodeTitle.Subject(title);
            return default;
        }
        var next = PaneActivityPresentation.ClaudeTitleChanged(
            state.Agent, state.AgentUnseenTurn, status, paneWatched);
        state.Agent = next.Agent;
        state.AgentUnseenTurn = next.AgentUnseenTurn;
        if (status is not null)
            state.AgentTitle = ClaudeCodeTitle.Subject(title);
        if (next.Agent is not null)
            state.AgentStamp = ++_agentClock;
        // 承認したら作業が再開する（印が回り出す）。終了したら当然いらない。
        if (next.Agent is not ClaudeCodeStatus.Idle && state.AgentApproval)
        {
            state.AgentApproval = false;
            return next with { ShouldRefresh = true };
        }
        return next;
    }

    /// <summary>
    /// ターミナルの中のプログラムが OSC 7501 で状態を申告した（§24.61）。Claude Code の申告は承認待ち・作業中・入力待ちの
    /// 正本にし（タイトルの印・フックより正確）、Claude 以外の申告は動いているもの面のために持つ。
    /// 承認待ちは、フックと同じく見ているペインでは残さない（ダイアログが目の前にある）。
    /// </summary>
    public ProgramStatusTransition TerminalProgramStatusChanged(
        Guid tabId, IReadOnlyList<ProgramStatusRecord> records, bool paneWatched)
    {
        var claude = ProgramStatusPresentation.Claude(records);
        var others = ProgramStatusPresentation.Others(records);
        if (!_terminals.TryGetValue(tabId, out var state))
        {
            if (claude is null && others.Count == 0)
                return default;
            _terminals[tabId] = state = new TerminalState();
        }

        var wasWaiting = state.Programs.Any(r => r.State == ProgramStatusState.Blocked);
        var programsChanged = !ProgramStatusPresentation.SameShape(state.Programs, others);
        state.Programs = others;
        var humanTurnStarted = !paneWatched && !wasWaiting && others.Any(r => r.State == ProgramStatusState.Blocked);

        ClaudeCodeTitleTransition transition = default;
        if (claude is { } c)
        {
            state.AgentReportsStatus = true;
            var next = PaneActivityPresentation.ClaudeTitleChanged(state.Agent, state.AgentUnseenTurn, c.Status, paneWatched);
            state.Agent = next.Agent;
            state.AgentUnseenTurn = next.AgentUnseenTurn;
            if (next.ShouldRefresh)
                state.AgentStamp = ++_agentClock;   // 同じ申告の繰り返しでは並びを動かさない
            if (c.Title is { } subject)
                state.AgentTitle = subject;
            var refresh = next.ShouldRefresh;
            if (c.Blocked && !paneWatched && !state.AgentApproval)
                (state.AgentApproval, refresh) = (true, true);
            else if (!c.Blocked && state.AgentApproval)
                (state.AgentApproval, refresh) = (false, true);   // 申告は正確なので、待ちが解けたら印も解く
            transition = next with { ShouldRefresh = refresh };
        }
        else if (state.AgentReportsStatus)
        {
            // 申告が消えた（clear・次のプロンプト・プロセスの終了）＝ Claude は居なくなった。
            state.AgentReportsStatus = false;
            state.Agent = null;
            state.AgentUnseenTurn = false;
            state.AgentApproval = false;
            state.AgentTitle = null;
            transition = new ClaudeCodeTitleTransition(null, false, true, false);
        }
        return new ProgramStatusTransition(transition, humanTurnStarted, programsChanged);
    }

    /// <summary>そのタブの、Claude 以外のプログラムのいま動いている申告（§24.61）。</summary>
    public IReadOnlyList<ProgramStatusRecord> ProgramsOf(Guid tabId)
        => _terminals.TryGetValue(tabId, out var state) ? state.Programs : [];

    /// <summary>Claude Code のフックが「承認を待っている」と知らせてきた（§24.1.1）。見ているなら残さない。
    /// 状態が変わったら true。</summary>
    public bool TerminalAgentNeedsApproval(Guid tabId, bool paneWatched)
    {
        if (paneWatched)
            return false;
        if (!_terminals.TryGetValue(tabId, out var state))
            _terminals[tabId] = state = new TerminalState();
        if (state.AgentApproval)
            return false;
        state.AgentApproval = true;
        state.Agent ??= ClaudeCodeStatus.Idle;
        return true;
    }

    /// <summary>そのタブで動いている Claude Code のセッション（SessionStart フックで分かる）。</summary>
    public void TerminalAgentSessionStarted(Guid tabId, string sessionId, string? transcriptPath)
    {
        if (!_terminals.TryGetValue(tabId, out var state))
            _terminals[tabId] = state = new TerminalState();
        state.SessionId = sessionId;
        state.TranscriptPath = transcriptPath;
    }

    public (string SessionId, string? TranscriptPath)? AgentSessionOf(Guid tabId)
        => _terminals.TryGetValue(tabId, out var state) && state.SessionId is { } id ? (id, state.TranscriptPath) : null;

    public bool ForgetTerminal(Guid tabId) => _terminals.Remove(tabId);

    /// <summary>コマンドが進捗を知らせてきた（OSC 9;4・§24.35）。表示が変わるなら true
    /// （同じ種類で % だけ動いたときも true——バッジの数字を書き換える）。</summary>
    public bool TerminalProgressChanged(Guid tabId, TerminalProgress progress)
    {
        if (!_terminals.TryGetValue(tabId, out var state))
        {
            if (!progress.IsActive)
                return false;
            _terminals[tabId] = state = new TerminalState();
        }
        if (state.Progress == progress)
            return false;
        state.Progress = progress;
        return true;
    }

    /// <summary>ベルが鳴った（§24.35）。ペインを見ているなら残さない（見えているタブの帯には Terminal 自身が印を出す）。
    /// 状態が変わったら true。</summary>
    public bool TerminalBellRang(Guid tabId, bool paneWatched)
    {
        if (paneWatched)
            return false;
        if (!_terminals.TryGetValue(tabId, out var state))
            _terminals[tabId] = state = new TerminalState();
        if (state.UnseenBell)
            return false;
        state.UnseenBell = true;
        return true;
    }

    /// <summary>Claude Code が居るタブ（§38.8 の一覧）。人間の番が先：承認待ち → 入力待ち（見ていない間に
    /// ターンが終わった）→ 待機 → 作業中。同じ段の中は最後に印が動いた順。</summary>
    public IReadOnlyList<ClaudeAgentTab> AgentTabs()
        => _terminals
            .Where(pair => pair.Value.Agent is not null)
            .Select(pair => new ClaudeAgentTab(
                pair.Key, ClaudeAgentTab.StateOf(pair.Value.Agent!.Value, pair.Value.AgentApproval, pair.Value.AgentUnseenTurn),
                pair.Value.AgentTitle, pair.Value.SessionId, pair.Value.TranscriptPath, pair.Value.AgentStamp))
            .OrderBy(tab => tab.State)
            .ThenByDescending(tab => tab.Stamp)
            .ToList();

    /// <summary>そのタブの前面でシェル以外が動いているか（OSC133 の C を見て D 未着、または Claude Code）。
    /// 動いている間に打ち込んだ行はシェルでなくそのプログラムに届く。</summary>
    public bool IsTerminalBusy(Guid tabId)
        => _terminals.TryGetValue(tabId, out var state) && (state.Running || state.Agent is not null);

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
                state.AgentApproval = false;
                state.UnseenBell = false;
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

    public PaneActivityKind AggregateTerminal(out int exitCode) => AggregateTerminal(out exitCode, out _);

    public PaneActivityKind AggregateTerminal(out int exitCode, out TerminalProgress progress)
        => PaneActivityPresentation.AggregateTerminal(
            _terminals.Values.Select(state =>
                (state.Running, state.UnseenExitCode, state.Agent, state.AgentUnseenTurn, state.AgentApproval,
                    state.Progress, state.UnseenBell)), out exitCode, out progress);

    public PaneActivityKind AggregateIde(
        PaneKind kind, bool taskRunning, bool sessionBusy, bool sessionStopped)
        => PaneActivityPresentation.AggregateIde(
            taskRunning, sessionBusy, sessionStopped,
            _ides.TryGetValue(kind, out var state) ? state.UnseenCompletion : PaneActivityKind.None);
}
