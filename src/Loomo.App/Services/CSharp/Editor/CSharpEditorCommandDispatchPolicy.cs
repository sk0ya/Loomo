using sk0ya.Loomo.CSharp.Editor;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.App.Services;

/// <summary>C# editor command IDからコード生成要求への対応付け。</summary>
internal static class CSharpEditorCommandDispatchPolicy
{
    private static readonly IReadOnlyDictionary<string, CSharpCodeGenerationKind> CodeGenerationCommands =
        Enum.GetValues<CSharpCodeGenerationKind>()
            .Where(kind => kind != CSharpCodeGenerationKind.NullGuards)
            .ToDictionary(CSharpEditorResultMapper.CommandIdFor, kind => kind, StringComparer.Ordinal);

    public static bool TryGetCodeGenerationKind(string commandId, out CSharpCodeGenerationKind kind)
        => CodeGenerationCommands.TryGetValue(commandId, out kind);

    public static string? NativeEditorCommandFor(string commandId)
        => commandId switch
        {
            CSharpEditorCommandCatalog.Rename => "Rename",
            CSharpEditorCommandCatalog.Format => "Format",
            CSharpEditorCommandCatalog.QuickFix => "QuickFix",
            _ => null,
        };
}
