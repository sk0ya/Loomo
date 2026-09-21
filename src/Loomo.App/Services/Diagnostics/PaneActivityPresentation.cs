namespace sk0ya.Loomo.App.Services;

internal enum PaneActivityKind { None, Running, Stopped, Approval, Succeeded, Failed }
internal enum PaneActivityBadgeTone { Hidden, Accent, Failed, Succeeded }

internal readonly record struct TerminalActivityTransition(
    bool Running, int? UnseenExitCode, bool ShouldRefresh);

/// <summary>ペイン活動の状態遷移、集約、短いバッジ文言を決める。</summary>
internal static class PaneActivityPresentation
{
    public static PaneActivityBadgeTone BadgeTone(PaneActivityKind activity)
        => activity switch
        {
            PaneActivityKind.Running or PaneActivityKind.Stopped or PaneActivityKind.Approval
                => PaneActivityBadgeTone.Accent,
            PaneActivityKind.Failed => PaneActivityBadgeTone.Failed,
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

    public static PaneActivityKind AggregateTerminal(
        IEnumerable<(bool Running, int? UnseenExitCode)> sessions, out int exitCode)
    {
        exitCode = 0;
        var states = sessions.ToArray();
        if (states.Any(state => state.Running))
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

    public static string IdeTaskLabel(string status)
    {
        var label = status.Trim().TrimEnd('…', '.');
        return string.IsNullOrWhiteSpace(label) ? "実行中" : label;
    }

    public static string BadgeText(
        PaneActivityKind activity, bool isTerminal, bool isIde, bool ideTaskRunning,
        int exitCode, string ideStatus)
        => activity switch
        {
            PaneActivityKind.Running when isIde && ideTaskRunning => $"● {IdeTaskLabel(ideStatus)}",
            PaneActivityKind.Running when isIde => "● デバッグ中",
            PaneActivityKind.Running => "● 実行中",
            PaneActivityKind.Stopped => "● 停止中",
            PaneActivityKind.Approval => "● 承認待ち",
            PaneActivityKind.Failed when isTerminal => $"✗ 失敗 {exitCode}",
            PaneActivityKind.Failed => "✗ 失敗",
            PaneActivityKind.Succeeded => "✓ 完了",
            _ => "",
        };
}
