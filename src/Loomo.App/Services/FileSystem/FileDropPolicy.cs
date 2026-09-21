using System.IO;

namespace sk0ya.Loomo.App.Services;

/// <summary>ペイン種別に応じたファイルドロップの受け入れ条件。</summary>
internal enum FileDropSurface
{
    Editor,
    Diff,
    Search,
    Git,
    Terminal,
    Unsupported,
}

internal static class FileDropPolicy
{
    public static bool CanAccept(FileDropSurface surface, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return false;

        return surface switch
        {
            FileDropSurface.Editor => paths.Any(File.Exists),
            FileDropSurface.Diff => paths.Count is 1 or 2 && paths.All(File.Exists),
            FileDropSurface.Search or FileDropSurface.Git or FileDropSurface.Terminal => true,
            _ => false,
        };
    }
}
