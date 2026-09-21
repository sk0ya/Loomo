namespace sk0ya.Loomo.App.Services;

/// <summary>デバッグアダプターごとの管轄ソース拡張子。</summary>
internal static class DebugSourcePolicy
{
    private static readonly HashSet<string> TypeScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".mts", ".cts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
    };

    private static readonly HashSet<string> DotnetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".fsx", ".vb", ".razor", ".cshtml",
    };

    public static bool IsTypeScriptSource(string? path)
        => !string.IsNullOrWhiteSpace(path) && TypeScriptExtensions.Contains(Path.GetExtension(path));

    public static bool IsDebuggableSource(string? path)
        => IsTypeScriptSource(path)
            || (!string.IsNullOrWhiteSpace(path) && DotnetExtensions.Contains(Path.GetExtension(path)));
}
