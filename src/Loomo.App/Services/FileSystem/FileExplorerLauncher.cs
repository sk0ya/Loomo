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

    /// <summary>Explorer で開く。フォルダーはその中を、ファイルは中を開けないので親フォルダーを開いて
    /// 選択状態にする。PC・ライブラリ等のシェル名前空間も開ける。</summary>
    public static void OpenInExplorer(string path)
    {
        if (ExplorerArgument(path, reveal: false) is { } argument)
            StartExplorer(argument);
    }

    /// <summary>親フォルダーを開いて項目を選択状態にする。フォルダーも中へは入らず親で選ぶ
    /// （中を開くのは <see cref="OpenInExplorer"/> の役目）。</summary>
    public static void RevealInExplorer(string path)
    {
        if (ExplorerArgument(path, reveal: true) is { } argument)
            StartExplorer(argument);
    }

    /// <summary>explorer.exe に渡す引数。存在しないものは null。ファイルは開く／表示とも親で選択する。
    /// ドライブ直下のように親が無いフォルダーは、選択ではなくそのまま開く。</summary>
    internal static string? ExplorerArgument(string? path, bool reveal)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (FolderTreeShellNamespaces.IsShellPath(path))
            return reveal ? null : path.StartsWith(":::", StringComparison.Ordinal) ? "shell" + path : path;

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { return null; }

        if (File.Exists(fullPath))
            return "/select," + fullPath;
        if (!Directory.Exists(fullPath))
            return null;

        var trimmed = Path.TrimEndingDirectorySeparator(fullPath);
        return reveal && Path.GetDirectoryName(trimmed) is { Length: > 0 }
            ? "/select," + trimmed
            : fullPath;
    }

    private static void StartExplorer(string argument)
    {
        try
        {
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            info.ArgumentList.Add(argument);
            Process.Start(info);
        }
        catch { /* Explorer 起動失敗は無視する。 */ }
    }
}
