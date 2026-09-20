namespace sk0ya.Loomo.App.ViewModels;

public enum DebugSessionEndKind
{
    Normal,
    ExitCode,
    UserStopped,
    AdapterDisconnected,
    LaunchFailed,
}

public sealed record DebugSessionOutcome(DebugSessionEndKind Kind, string Summary, string NextAction)
{
    public static DebugSessionOutcome Classify(int? exitCode, string? reason,
        bool userStopRequested, bool reachedRunning)
    {
        if (userStopRequested)
            return new(DebugSessionEndKind.UserStopped, "ユーザー停止", "同じ構成を再実行できます。");
        if (!reachedRunning)
            return new(DebugSessionEndKind.LaunchFailed, "起動失敗", "構成とadapterの導入状況を確認して再試行してください。");
        if (IsAdapterDisconnect(reason))
            return new(DebugSessionEndKind.AdapterDisconnected, "adapter切断", "adapterを再起動して再試行してください。");
        if (exitCode is null or 0)
            return new(DebugSessionEndKind.Normal, "正常終了", "同じ構成を再実行できます。");
        return new(DebugSessionEndKind.ExitCode, $"終了コード {exitCode}", "出力を確認し、設定またはプログラムを修正して再試行してください。");
    }

    private static bool IsAdapterDisconnect(string? reason) => reason?.Contains("adapter", StringComparison.OrdinalIgnoreCase) == true
        || reason?.Contains("disconnect", StringComparison.OrdinalIgnoreCase) == true
        || reason?.Contains("connection", StringComparison.OrdinalIgnoreCase) == true;
}
