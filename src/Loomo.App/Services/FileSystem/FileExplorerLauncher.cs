using System.Diagnostics;

namespace sk0ya.Loomo.App.Services;

/// <summary>既定の関連付けや Explorer でファイルシステム項目を開く。</summary>
internal static class FileExplorerLauncher
{
    public static void OpenWithDefaultApp(string path)
    {
        if (!File.Exists(path))
            return;

        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { ToastService.Error($"既定のアプリで開けませんでした: {ex.Message}"); }
    }

    public static void RevealInExplorer(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            if (File.Exists(fullPath))
                info.ArgumentList.Add("/select," + fullPath);
            else if (Directory.Exists(fullPath))
                info.ArgumentList.Add(fullPath);
            else
                return;

            Process.Start(info);
        }
        catch { /* Explorer 起動失敗は無視する。 */ }
    }
}
