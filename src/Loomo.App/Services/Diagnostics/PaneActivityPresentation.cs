namespace sk0ya.Loomo.App.Services;

internal enum PaneActivityKind { None, Running, Stopped, Approval, Succeeded, Failed, AgentWorking, AgentWaiting, AgentApproval, Progress, ProgressError, Bell }

/// <summary>ターミナルのコマンドが OSC 9;4 で知らせる進捗の種類（§24.35）。Terminal パッケージの
/// <c>TaskbarProgressState</c> を写したもの（集約の側をパッケージから切り離しておく）。</summary>
internal enum TerminalProgressKind { None, Normal, Error, Indeterminate, Warning }

/// <summary>1タブの進捗。<see cref="Percent"/> は 0–100（不確定なら使わない）。</summary>
internal readonly record struct TerminalProgress(TerminalProgressKind Kind, int Percent)
{
    public bool IsActive => Kind != TerminalProgressKind.None;
}
internal enum PaneActivityBadgeTone { Hidden, Accent, Failed, Succeeded }

internal readonly record struct TerminalActivityTransition(
    bool Running, int? UnseenExitCode, bool ShouldRefresh);

internal readonly record struct ClaudeCodeTitleTransition(
    ClaudeCodeStatus? Agent, bool AgentUnseenTurn, bool ShouldRefresh, bool TurnEnded);

/// <summary>ペイン活動の状態遷移、集約、短いバッジ文言を決める。</summary>
internal static class PaneActivityPresentation
{
    public static PaneActivityBadgeTone BadgeTone(PaneActivityKind activity)
        => activity switch
        {
            PaneActivityKind.Running or PaneActivityKind.Stopped or PaneActivityKind.Approval
                or PaneActivityKind.AgentWorking or PaneActivityKind.AgentWaiting or PaneActivityKind.AgentApproval
                or PaneActivityKind.Progress or PaneActivityKind.Bell
                => PaneActivityBadgeTone.Accent,
            PaneActivityKind.Failed or PaneActivityKind.ProgressError => PaneActivityBadgeTone.Failed,
            PaneActivityKind.Succeeded => PaneActivityBadgeTone.Succeeded,
            _ => PaneActivityBadgeTone.Hidden,
        };

    public static bool IsFailedTaskStatus(string status)
        => status.Contains("失敗", StringComparison.Ordinal)
           || status.Contains("エラー", StringComparison.Ordinal)
           || status.Contains("中断", StringComparison.Ordinal);

    public static (bool TaskWasRunning, PaneActivityKind UnseenCompletion) IdeTaskChanged(
        bool wasRunning, bool isRunning, bool paneWatched, string status, PaneActivityKind previousCompletion)
    {
        var completion = previousCompletion;
        if (wasRunning && !isRunning)
            completion = paneWatched
                ? PaneActivityKind.None
                : IsFailedTaskStatus(status) ? PaneActivityKind.Failed : PaneActivityKind.Succeeded;
        if (isRunning)
            completion = PaneActivityKind.None;
        return (isRunning, completion);
    }

    public static PaneActivityKind Completion(bool paneWatched, bool succeeded)
        => paneWatched ? PaneActivityKind.None : succeeded ? PaneActivityKind.Succeeded : PaneActivityKind.Failed;

    public static PaneActivityKind AggregateIde(
        bool taskRunning, bool sessionBusy, bool sessionStopped, PaneActivityKind unseenCompletion)
    {
        if (taskRunning)
            return PaneActivityKind.Running;
        if (sessionBusy)
            return sessionStopped ? PaneActivityKind.Stopped : PaneActivityKind.Running;
        return unseenCompletion;
    }

    public static PaneActivityKind AggregateAi(
        bool chatBusy, bool workflowRunning, string statusText, int approvalCount,
        PaneActivityKind unseenCompletion)
    {
        if (chatBusy || workflowRunning)
        {
            var waitingApproval = chatBusy
                ? statusText.Contains("承認待ち", StringComparison.Ordinal)
                : approvalCount > 0;
            return waitingApproval ? PaneActivityKind.Approval : PaneActivityKind.Running;
        }
        return unseenCompletion;
    }

    /// <summary>タブを1つのバッジへ集約する。承認を待っている Claude Code ＞ 人間の番を待っている Claude Code ＞
    /// 見ていない間に鳴ったベル ＞ 作業中の Claude Code ＞ 進捗を出しているコマンド ＞
    /// 実行中のコマンド ＞ 失敗 ＞ 成功。ベルは「呼ばれた」なので人間の番の次に置く。進捗はいちばん遅れているタブの値
    /// （全部が終わるまでの目安）を返す。Claude Code が居るタブは OSC133 上は <c>claude</c> コマンドが
    /// ずっと実行中なので、そのタブの「実行中」は Claude 自身の状態で置き換える（開いているだけの
    /// セッションが「● 実行中」を出し続けるのは雑音でしかない）。</summary>
    public static PaneActivityKind AggregateTerminal(
        IEnumerable<(bool Running, int? UnseenExitCode, ClaudeCodeStatus? Agent, bool AgentUnseenTurn, bool AgentApproval)> sessions,
        out int exitCode)
        => AggregateTerminal(
            sessions.Select(s => (s.Running, s.UnseenExitCode, s.Agent, s.AgentUnseenTurn, s.AgentApproval,
                default(TerminalProgress), false)),
            out exitCode, out _);

    public static PaneActivityKind AggregateTerminal(
        IEnumerable<(bool Running, int? UnseenExitCode, ClaudeCodeStatus? Agent, bool AgentUnseenTurn, bool AgentApproval,
            TerminalProgress Progress, bool UnseenBell)> sessions,
        out int exitCode, out TerminalProgress progress)
    {
        exitCode = 0;
        progress = default;
        var states = sessions.ToArray();
        if (states.Any(state => state.AgentApproval))
            return PaneActivityKind.AgentApproval;
        if (states.Any(state => state.AgentUnseenTurn))
            return PaneActivityKind.AgentWaiting;
        if (states.Any(state => state.UnseenBell))
            return PaneActivityKind.Bell;
        if (states.Any(state => state.Agent == ClaudeCodeStatus.Working))
            return PaneActivityKind.AgentWorking;
        var reporting = states.Where(state => state.Progress.IsActive).Select(state => state.Progress).ToArray();
        if (reporting.Length > 0)
        {
            // エラーのタブがあればそれを出す（赤）。無ければいちばん遅れているタブ。不確定は % を持たないので後ろへ。
            var error = reporting.FirstOrDefault(p => p.Kind == TerminalProgressKind.Error);
            progress = error.IsActive
                ? error
                : reporting.OrderBy(p => p.Kind == TerminalProgressKind.Indeterminate ? 1 : 0).ThenBy(p => p.Percent).First();
            return progress.Kind == TerminalProgressKind.Error ? PaneActivityKind.ProgressError : PaneActivityKind.Progress;
        }
        if (states.Any(state => state.Running && state.Agent is null))
            return PaneActivityKind.Running;
        var failed = states.FirstOrDefault(state => state.UnseenExitCode is > 0);
        if (failed.UnseenExitCode is { } code)
        {
            exitCode = code;
            return PaneActivityKind.Failed;
        }
        return states.Any(state => state.UnseenExitCode == 0)
            ? PaneActivityKind.Succeeded
            : PaneActivityKind.None;
    }

    public static TerminalActivityTransition TerminalCommandChanged(
        bool wasRunning, int? previousUnseenExitCode, bool commandExecuted, bool commandDone,
        int? exitCode, bool paneWatched)
    {
        if (commandExecuted)
            return new(true, null, true);
        if (!commandDone || !wasRunning)
            return new(wasRunning, previousUnseenExitCode, false);
        return new(false, paneWatched ? null : exitCode ?? 0, true);
    }

    /// <summary>Claude Code のタイトル変化による1タブの遷移。作業中→待機へ移ったときに
    /// ペインが見えていなければ「人間の番」を未確認として残す。印のコマ送り（◐⇄◑）は状態が変わらないので
    /// 再描画しない。Claude Code のタイトルでなくなったら（終了）状態を捨てる。</summary>
    public static ClaudeCodeTitleTransition ClaudeTitleChanged(
        ClaudeCodeStatus? previous, bool previousUnseenTurn, ClaudeCodeStatus? next, bool paneWatched)
    {
        var turnEnded = previous == ClaudeCodeStatus.Working && next == ClaudeCodeStatus.Idle;
        var unseen = next switch
        {
            null or ClaudeCodeStatus.Working => false,
            _ => turnEnded ? !paneWatched : previousUnseenTurn,
        };
        return new(next, unseen, previous != next || previousUnseenTurn != unseen, turnEnded);
    }

    /// <summary>進捗のバッジ文言（<c>● 42%</c>／不確定は <c>● 進行中</c>／一時停止は <c>⏸ 42%</c>）。</summary>
    public static string ProgressText(TerminalProgress progress)
        => progress.Kind switch
        {
            TerminalProgressKind.Indeterminate => "● 進行中",
            TerminalProgressKind.Warning => $"⏸ {progress.Percent}%",
            TerminalProgressKind.Error => $"✗ {progress.Percent}%",
            _ => $"● {progress.Percent}%",
        };

    public static string IdeTaskLabel(string status)
    {
        var label = status.Trim().TrimEnd('…', '.');
        return string.IsNullOrWhiteSpace(label) ? "実行中" : label;
    }

    public static string BadgeText(
        PaneActivityKind activity, bool isTerminal, bool isIde, bool ideTaskRunning,
        int exitCode, string ideStatus, TerminalProgress progress = default)
        => activity switch
        {
            PaneActivityKind.Progress or PaneActivityKind.ProgressError => ProgressText(progress),
            PaneActivityKind.Bell => "🔔 呼び出し",
            PaneActivityKind.Running when isIde && ideTaskRunning => $"● {IdeTaskLabel(ideStatus)}",
            PaneActivityKind.Running when isIde => "● デバッグ中",
            PaneActivityKind.Running => "● 実行中",
            PaneActivityKind.Stopped => "● 停止中",
            PaneActivityKind.Approval => "● 承認待ち",
            PaneActivityKind.AgentWorking => "● Claude 作業中",
            PaneActivityKind.AgentWaiting => "✳ Claude 入力待ち",
            PaneActivityKind.AgentApproval => "● Claude 承認待ち",
            PaneActivityKind.Failed when isTerminal => $"✗ 失敗 {exitCode}",
            PaneActivityKind.Failed => "✗ 失敗",
            PaneActivityKind.Succeeded => "✓ 完了",
            _ => "",
        };
}
