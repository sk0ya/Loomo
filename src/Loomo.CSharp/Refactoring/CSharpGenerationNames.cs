using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using sk0ya.Loomo.CSharp.Configuration;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>生成する識別子の命名。editorconfig の命名規約（<see cref="CSharpNamingStyle"/>）を
/// 適用し、予約語のエスケープと重複回避もここに集める。</summary>
internal static class GenerationNames
{
    internal static string ToFieldName(string parameterName, CSharpNamingStyle? style = null)
    {
        var name = parameterName.TrimStart('_');
        if (name.StartsWith("m_", StringComparison.Ordinal)) name = name[2..];
        if (name.Length == 0) name = "value";
        name = ApplyNamingCapitalization(name, style?.Capitalization ?? "camel_case");
        return (style?.RequiredPrefix ?? "_") + name;
    }

    internal static string ToPropertyName(string fieldName, CSharpNamingStyle? style = null)
    {
        var name = fieldName.TrimStart('_');
        if (name.StartsWith("m_", StringComparison.Ordinal)) name = name[2..];
        if (name.Length == 0) return "Value";
        return ApplyNamingCapitalization(name, style?.Capitalization ?? "pascal_case");
    }

    internal static string MakeUniqueParameterName(
        string fieldName, HashSet<string> used, CSharpNamingStyle? style = null)
    {
        var name = fieldName.TrimStart('_');
        if (name.StartsWith("m_", StringComparison.Ordinal)) name = name[2..];
        if (name.Length == 0) name = "value";
        name = ApplyNamingCapitalization(name, style?.Capitalization ?? "camel_case");
        if (!string.IsNullOrEmpty(style?.RequiredPrefix))
            name = style.RequiredPrefix + name;
        if (SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None) name = "@" + name;
        var baseName = name;
        for (var i = 2; !used.Add(name); i++) name = baseName + i;
        return name;
    }

    private static string ApplyNamingCapitalization(string name, string capitalization)
    {
        if (name.Length == 0) return name;
        return capitalization.Trim().ToLowerInvariant() switch
        {
            "camel_case" or "first_word_lower" => char.ToLowerInvariant(name[0]) + name[1..],
            "pascal_case" or "first_word_upper" => char.ToUpperInvariant(name[0]) + name[1..],
            "all_upper" => name.ToUpperInvariant(),
            "all_lower" => name.ToLowerInvariant(),
            _ => name,
        };
    }

    internal static string EscapeIdentifier(string name)
        => SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None ? name : "@" + name;

    internal static SyntaxToken IdentifierToken(string name)
        => SyntaxFactory.Identifier(EscapeIdentifier(name));
}
