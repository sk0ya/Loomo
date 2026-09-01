using sk0ya.Loomo.CSharp.Configuration;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>
/// C# cleanup用の.editorconfig設定を、編集操作から共通に組み立てるFactory。
/// 設定の解決規則はC#専用DLLに置き、App側には設定項目の解釈を漏らさない。
/// </summary>
public static class CSharpCleanupOptionsFactory
{
    public static CSharpCleanupOptions CreateForFile(
        string filePath,
        bool format = false,
        bool removeUnusedUsings = false,
        bool? insertFinalNewlineWhenUnset = true,
        bool excludeGeneratedCode = true,
        CSharpEditorConfigService? editorConfigService = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var config = (editorConfigService ?? new CSharpEditorConfigService()).Resolve(filePath);
        return Create(config, format, removeUnusedUsings,
            insertFinalNewlineWhenUnset, excludeGeneratedCode);
    }

    public static CSharpCleanupOptions Create(
        CSharpEditorConfig config,
        bool format = false,
        bool removeUnusedUsings = false,
        bool? insertFinalNewlineWhenUnset = true,
        bool excludeGeneratedCode = true)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new CSharpCleanupOptions(
            Format: format,
            RemoveUnusedUsings: removeUnusedUsings,
            EndOfLine: config.EndOfLine,
            InsertFinalNewline: config.InsertFinalNewline ?? insertFinalNewlineWhenUnset,
            ExcludeGeneratedCode: excludeGeneratedCode,
            SortSystemDirectivesFirst: SortSystemDirectivesFirst(config),
            IndentSize: config.IndentSize,
            TabWidth: config.TabWidth,
            UseTabs: string.Equals(config.IndentStyle, "tab", StringComparison.OrdinalIgnoreCase));
    }

    public static bool SortSystemDirectivesFirst(CSharpEditorConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return !string.Equals(config.Get("dotnet_sort_system_directives_first"),
            "false", StringComparison.OrdinalIgnoreCase);
    }
}
