using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>値としての振る舞い（Equals ／ GetHashCode ／ ToString ／ Deconstruct）を生成する。
/// 対象メンバーの決め方が 3 つで共通なので、<see cref="GetSemanticValueMembers"/> を分け合う。</summary>
internal static class CSharpValueSemanticsGenerator
{
    internal static (string? Text, string? Summary, string? Error) GenerateEquality(
        TypeDeclarationSyntax type, CSharpGenerationOptions options, SemanticModel? semanticModel)
    {
        if (type is RecordDeclarationSyntax)
            return (null, null, "recordはコンパイラが値等価性を生成するため、Equals／GetHashCodeを追加しません。");

        var members = GetSemanticValueMembers(type, options, semanticModel, autoPropertiesOnly: true);
        if (members.Count == 0)
            return (null, null, "比較対象のインスタンスフィールドまたはauto-propertyがありません。");

        var methods = type.Members.OfType<MethodDeclarationSyntax>()
            .Select(m => m.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);
        if (methods.Contains("Equals") || methods.Contains("GetHashCode"))
            return (null, null, "Equals または GetHashCode が既にあります。");

        var typeName = type.Identifier.ValueText;
        var comparisonLines = new List<string> { $"    return obj is {typeName} other" };
        comparisonLines.AddRange(members.Select(member =>
            $"        && global::System.Object.Equals({member.Expression}, other.{member.Expression})"));
        comparisonLines[^1] += ";";
        var comparisons = string.Join("\n", comparisonLines);
        var hashExpressions = members.Select(member => member.Expression)
            .ToList();
        var hash = hashExpressions.Count <= 8
            ? $"return global::System.HashCode.Combine({string.Join(", ", hashExpressions)});"
            : string.Join("\n", [
                "var hash = new global::System.HashCode();",
                .. hashExpressions.Select(expression => $"hash.Add({expression});"),
                "return hash.ToHashCode();",
            ]);
        var objectType = options.NullableEnabled ? "object?" : "object";
        var generated = $"public override bool Equals({objectType} obj)\n{{\n" +
            $"{comparisons}\n" +
            "}\n\n" +
            "public override int GetHashCode()\n{\n" + hash + "\n}";
        return (generated, "Equals／GetHashCodeを生成", null);
    }

    internal static (string? Text, string? Summary, string? Error) GenerateToString(
        TypeDeclarationSyntax type, CSharpGenerationOptions options, SemanticModel? semanticModel)
    {
        if (type is RecordDeclarationSyntax)
            return (null, null, "recordはコンパイラがToStringを生成するため、追加しません。");

        if (type.Members.OfType<MethodDeclarationSyntax>().Any(method =>
                string.Equals(method.Identifier.ValueText, "ToString", StringComparison.Ordinal) &&
                method.ParameterList.Parameters.Count == 0))
            return (null, null, "ToStringメソッドが既にあります。");

        var members = GetSemanticValueMembers(type, options, semanticModel, autoPropertiesOnly: false);
        if (members.Count == 0)
            return (null, null, "ToStringに含めるインスタンスメンバーがありません。");

        var parts = string.Join(", ", members.Select(member =>
            $"{{nameof({member.Expression})}}={{{member.Expression}}}"));
        var generated = "public override string ToString()\n{\n"
            + $"    return $\"{parts}\";\n"
            + "}";
        return (generated, "ToStringを生成", null);
    }

    /// <summary>インスタンスフィールド／読み取り可能なプロパティからDeconstructを生成する。
    /// recordはコンパイラーが既に生成するため対象外とし、indexer・static・write-onlyは含めない。</summary>
    internal static (string? Text, string? Summary, string? Error) GenerateDeconstruct(
        TypeDeclarationSyntax type, CSharpGenerationOptions options, SemanticModel? semanticModel)
    {
        if (type is RecordDeclarationSyntax)
            return (null, null, "recordはコンパイラーがDeconstructを生成するため、追加しません。");

        var members = GetSemanticValueMembers(type, options, semanticModel, autoPropertiesOnly: false)
            .Select(member =>
                (member.Type, member.Name, Expression: "this." + member.Expression))
            .ToList();

        if (members.Count == 0)
            return (null, null, "Deconstructに含めるインスタンスメンバーがありません。");

        var existingArities = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(method => string.Equals(method.Identifier.ValueText, "Deconstruct",
                StringComparison.Ordinal))
            .Select(method => method.ParameterList.Parameters.Count)
            .ToHashSet();
        if (existingArities.Contains(members.Count))
            return (null, null, "同じ引数数のDeconstructメソッドが既にあります。");

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var parameters = members.Select(member =>
        {
            var parameterName = GenerationNames.MakeUniqueParameterName(member.Name, usedNames, options.ParameterNaming);
            return (member.Type, Name: parameterName, member.Expression);
        }).ToList();
        var parameterText = string.Join(", ", parameters.Select(parameter =>
            $"out {parameter.Type} {parameter.Name}"));
        var assignments = string.Join("\n", parameters.Select(parameter =>
            $"{parameter.Name} = {parameter.Expression};"));
        return ($"public void Deconstruct({parameterText})\n{{\n" +
                string.Join("\n", assignments.Split('\n').Select(line => "    " + line)) +
                "\n}", "Deconstructを生成", null);
    }

    private static List<ValueMember> GetSemanticValueMembers(
        TypeDeclarationSyntax type,
        CSharpGenerationOptions options,
        SemanticModel? semanticModel,
        bool autoPropertiesOnly)
    {
        var fields = GenerationSyntax.InstanceFields(type).ToList();
        var members = fields.Select(field => new ValueMember(
                field.Identifier.ValueText, field.Type.ToString(), field.Identifier.Text, true))
            .ToList();
        var fieldNames = members.Select(member => member.Name)
            .ToHashSet(StringComparer.Ordinal);

        if (semanticModel is not null &&
            GenerationSyntax.FindEquivalentType(type, semanticModel) is { } semanticType &&
            semanticModel.GetDeclaredSymbol(semanticType) is INamedTypeSymbol typeSymbol)
        {
            var activeTree = semanticModel.SyntaxTree;
            foreach (var field in typeSymbol.GetMembers().OfType<IFieldSymbol>()
                         .Where(field => !field.IsImplicitlyDeclared && !field.IsStatic && !field.IsConst &&
                             !field.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == activeTree)))
            {
                if (fieldNames.Add(field.Name))
                    members.Add(new ValueMember(field.Name, MemberFormat.DisplayGeneratedType(field.Type),
                        GenerationNames.EscapeIdentifier(field.Name), true));
            }
        }

        var fieldPropertyNames = fieldNames
            .Select(fieldName => GenerationNames.ToPropertyName(fieldName, options.PropertyNaming))
            .ToHashSet(StringComparer.Ordinal);
        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        var activeProperties = (autoPropertiesOnly
                ? GenerationSyntax.InstanceReadableAutoProperties(type)
                : GenerationSyntax.InstanceReadableProperties(type))
            .Where(property => !fieldPropertyNames.Contains(property.Identifier.ValueText));
        foreach (var property in activeProperties)
        {
            if (propertyNames.Add(property.Identifier.ValueText))
                members.Add(new ValueMember(property.Identifier.ValueText, property.Type.ToString(),
                    property.Identifier.Text, false));
        }

        if (semanticModel is not null &&
            GenerationSyntax.FindEquivalentType(type, semanticModel) is { } propertyType &&
            semanticModel.GetDeclaredSymbol(propertyType) is INamedTypeSymbol propertyTypeSymbol)
        {
            var activeTree = semanticModel.SyntaxTree;
            foreach (var property in propertyTypeSymbol.GetMembers().OfType<IPropertySymbol>()
                         .Where(property => !property.IsStatic && !property.IsIndexer &&
                             !property.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == activeTree)))
            {
                var syntax = GenerationSyntax.GetPropertyDeclaration(property);
                if (syntax is null || !GenerationSyntax.IsReadableProperty(syntax, autoPropertiesOnly) ||
                    fieldPropertyNames.Contains(property.Name) || !propertyNames.Add(property.Name))
                    continue;
                members.Add(new ValueMember(property.Name, MemberFormat.DisplayGeneratedType(property.Type),
                    GenerationNames.EscapeIdentifier(property.Name), false));
            }
        }
        return members;
    }

    private sealed record ValueMember(string Name, string Type, string Expression, bool IsField);
}
