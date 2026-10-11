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

    /// <summary>
    /// C# 専用カタログにあるが、中身は言語サーバーの汎用操作（定義・参照・名前変更・整形・Quick Fix）。
    /// これらは C# 以外のファイルでも、言語サーバーの付いたエディタにフォーカスがあれば同じキーで動く
    /// （Rider の手癖を言語で途切れさせない・設計書 §36）。Id は互換のため変えない
    /// （利用者のキー上書きが Id で保存されている）。
    /// </summary>
    public static bool IsLanguageNeutral(string commandId)
        => commandId is CSharpEditorCommandCatalog.Rename
            or CSharpEditorCommandCatalog.Format
            or CSharpEditorCommandCatalog.QuickFix
            or CSharpEditorCommandCatalog.GoToDefinition
            or CSharpEditorCommandCatalog.PeekDefinition
            or CSharpEditorCommandCatalog.GoToImplementation
            or CSharpEditorCommandCatalog.GoToTypeDefinition
            or CSharpEditorCommandCatalog.GoToDeclaration
            or CSharpEditorCommandCatalog.FindReferences;

    /// <summary>
    /// 言語共通の操作を C# 以外のエディタで受けてよいか。<paramref name="vimEnabled"/> のとき、
    /// 宣言へ移動（既定 Ctrl+B）は Vim のページ送りへ譲る——C# は従来どおり Rider の Ctrl+B を優先する。
    /// </summary>
    public static bool AcceptsInNonCSharpEditor(string commandId, bool vimEnabled)
        => IsLanguageNeutral(commandId)
            && !(vimEnabled && commandId == CSharpEditorCommandCatalog.GoToDeclaration);

    public static string? NativeEditorCommandFor(string commandId)
        => commandId switch
        {
            CSharpEditorCommandCatalog.Rename => "Rename",
            CSharpEditorCommandCatalog.Format => "Format",
            CSharpEditorCommandCatalog.QuickFix => "QuickFix",
            _ => null,
        };
}
