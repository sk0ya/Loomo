namespace sk0ya.Loomo.App.Services;

/// <summary>切り離し後のターミナル作業フォルダーとブラウザーの初期URLを選ぶ。</summary>
internal static class DetachedLaunchTargetPolicy
{
    public static string ResolveTerminalWorkingDirectory(
        string? sourceDirectory,
        string? workspaceRoot,
        string terminalCurrentDirectory)
    {
        if (!string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory))
            return sourceDirectory;
        return workspaceRoot ?? terminalCurrentDirectory;
    }

    public static string InitialBrowserAddress(string? sourceUrl, string defaultBrowserUrl)
        => sourceUrl ?? defaultBrowserUrl;

    public static string NormalizeBrowserAddress(string address, string defaultBrowserUrl)
        => WorkspaceSessionCoordinator.NormalizeBrowserAddress(address, defaultBrowserUrl);
}
