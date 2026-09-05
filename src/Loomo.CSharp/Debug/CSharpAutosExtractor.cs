using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.CSharp.Debug;

/// <summary>C#ソースの停止行から、自動変数として評価する候補を抽出する専用アダプター。</summary>
public static class CSharpAutosExtractor
{
    private static readonly IReadOnlySet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while", "var", "nameof", "when", "where", "yield", "async", "await",
        "get", "set", "value", "add", "remove",
    };

    public static IReadOnlyList<string> ExtractCandidates(string? currentLine, string? previousLine)
        => AutosExtractor.ExtractCandidates(currentLine, previousLine, Keywords);
}
