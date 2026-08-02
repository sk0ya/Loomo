namespace sk0ya.Loomo.Services.Lsp;

public enum LspServerRuntimeState
{
    Unconfigured,
    Starting,
    Initializing,
    ProjectLoading,
    Ready,
    Reconnecting,
    Stopped,
    Failed,
}

public sealed record LspServerRuntimeStatus(
    string Executable,
    string Root,
    LspServerRuntimeState State,
    string? LastError,
    int ReconnectAttempt);
