using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>TypeScript／JavaScriptの自動変数候補に使う言語固有の除外語。</summary>
internal static class TypeScriptAutosExtractor
{
    private static readonly IReadOnlySet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "abstract", "any", "as", "async", "await", "boolean", "break", "case", "catch", "class",
        "const", "continue", "debugger", "declare", "default", "delete", "do", "else", "enum",
        "export", "extends", "false", "finally", "for", "from", "function", "get", "if",
        "implements", "import", "in", "infer", "instanceof", "interface", "is", "keyof", "let",
        "namespace", "never", "new", "null", "number", "object", "of", "override", "private",
        "protected", "public", "readonly", "return", "satisfies", "set", "static", "string",
        "super", "switch", "symbol", "this", "throw", "true", "try", "type", "typeof",
        "undefined", "unique", "unknown", "var", "void", "while", "with", "yield",
        "console", "require", "module", "exports",
    };

    public static IReadOnlyList<string> ExtractCandidates(string? currentLine, string? previousLine)
        => AutosExtractor.ExtractCandidates(currentLine, previousLine, Keywords);
}
