namespace sk0ya.Loomo.Core.Debug;

/// <summary>デバッグセッションの状態。</summary>
public enum DebugSessionState
{
    Idle,
    Launching,
    Running,
    Stopped,
    Terminated,
    Failed,
}

/// <summary>デバッグ対象またはアダプタからの出力種別。</summary>
public enum DebugOutputCategory
{
    Stdout,
    Stderr,
    Console,
    Important,
}

/// <summary>デバッグコンソールに流す出力。</summary>
public sealed record DebugOutput(DebugOutputCategory Category, string Text);
/// <summary>セッション終了の通知。</summary>
public sealed record DebugExited(int? ExitCode, string? Reason);
/// <summary>デバッガが停止した位置の通知。Line は1始まり。</summary>
public sealed record DebugStopped(string? SourcePath, int Line, string Reason, int ThreadId);
