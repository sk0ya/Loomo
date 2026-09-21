namespace sk0ya.Loomo.App.Services;

/// <summary>選択アクションへ渡す入力の正規化と、対象ファイルの分類。</summary>
internal static class SelectionActionPolicy
{
    private static readonly string[] RunnableScriptExtensions = [".ps1", ".bat", ".cmd"];

    public static (string Path, string MarkdownDestination) NormalizeMarkdownDestination(string destination)
    {
        var path = destination.Trim().Replace('\\', '/');
        return (path, path.Replace(" ", "%20", StringComparison.Ordinal));
    }

    public static bool IsRunnableScript(string path)
        => Array.Exists(RunnableScriptExtensions,
            extension => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase));
}
