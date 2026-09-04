using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynAccessibility = Microsoft.CodeAnalysis.Accessibility;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>生成コードの断片（型名・引数リスト・アクセシビリティ）を文字列にする。
/// 「どのメンバーを生成するか」は各生成器の責務で、ここは書き方だけを持つ。</summary>
internal static class MemberFormat
{
    internal static string DisplayGeneratedType(ITypeSymbol type)
    {
        if (type.SpecialType is not SpecialType.None)
            return type.SpecialType switch
            {
                SpecialType.System_Boolean => "bool",
                SpecialType.System_Byte => "byte",
                SpecialType.System_SByte => "sbyte",
                SpecialType.System_Char => "char",
                SpecialType.System_Decimal => "decimal",
                SpecialType.System_Double => "double",
                SpecialType.System_Single => "float",
                SpecialType.System_Int16 => "short",
                SpecialType.System_Int32 => "int",
                SpecialType.System_Int64 => "long",
                SpecialType.System_UInt16 => "ushort",
                SpecialType.System_UInt32 => "uint",
                SpecialType.System_UInt64 => "ulong",
                SpecialType.System_String => "string",
                SpecialType.System_Object => "object",
                _ => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            };
        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    internal static string FormatParameter(IParameterSymbol parameter, string name)
    {
        var modifier = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => parameter.IsParams ? "params " : "",
        };
        var type = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return $"{modifier}{type} {GenerationNames.EscapeIdentifier(name)}";
    }

    internal static string FormatParameter(IParameterSymbol parameter)
    {
        var modifier = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => parameter.IsParams ? "params " : "",
        };
        var type = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return $"{modifier}{type} {GenerationNames.EscapeIdentifier(parameter.Name)}";
    }

    internal static string FormatParameterArgument(IParameterSymbol parameter, string name)
    {
        var modifier = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => "",
        };
        return modifier + GenerationNames.EscapeIdentifier(name);
    }

    internal static string FormatParameterArgument(IParameterSymbol parameter)
    {
        var modifier = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => "",
        };
        return modifier + GenerationNames.EscapeIdentifier(parameter.Name);
    }

    internal static string FormatParameterArgument(ParameterSyntax parameter)
    {
        var modifier = parameter.Modifiers.Any(token => token.IsKind(SyntaxKind.RefKeyword)) ? "ref "
            : parameter.Modifiers.Any(token => token.IsKind(SyntaxKind.OutKeyword)) ? "out "
            : parameter.Modifiers.Any(token => token.IsKind(SyntaxKind.InKeyword)) ? "in "
            : "";
        return modifier + parameter.Identifier.ValueText;
    }

    internal static string FormatParameters(ParameterListSyntax parameters)
        => string.Join(", ", parameters.Parameters.Select(parameter =>
        {
            var modifiers = string.Join(" ", parameter.Modifiers.Select(m => m.Text));
            var prefix = modifiers.Length == 0 ? "" : modifiers + " ";
            var type = parameter.Type?.ToString() ?? "object";
            return $"{prefix}{type} {parameter.Identifier.ValueText}";
        }));

    internal static string FormatTypeParameterConstraints(ITypeParameterSymbol parameter)
    {
        var constraints = new List<string>();
        if (parameter.HasUnmanagedTypeConstraint) constraints.Add("unmanaged");
        else if (parameter.HasValueTypeConstraint) constraints.Add("struct");
        else if (parameter.HasReferenceTypeConstraint) constraints.Add("class");
        if (parameter.HasNotNullConstraint) constraints.Add("notnull");
        constraints.AddRange(parameter.ConstraintTypes.Select(type =>
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        if (parameter.HasConstructorConstraint) constraints.Add("new()");
        return constraints.Count == 0
            ? ""
            : "where " + GenerationNames.EscapeIdentifier(parameter.Name) + " : " + string.Join(", ", constraints);
    }

    internal static string SymbolAccessibility(RoslynAccessibility accessibility)
        => accessibility switch
        {
            RoslynAccessibility.Protected => "protected",
            RoslynAccessibility.ProtectedOrInternal => "protected internal",
            RoslynAccessibility.ProtectedAndInternal => "private protected",
            RoslynAccessibility.Internal => "internal",
            _ => "public",
        };

    internal static bool IsVoid(TypeSyntax type)
        => type is PredefinedTypeSyntax predefined
            && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);
}
