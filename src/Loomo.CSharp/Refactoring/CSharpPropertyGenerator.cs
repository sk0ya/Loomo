using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>インスタンスフィールドからラッパープロパティを生成する。</summary>
internal static class CSharpPropertyGenerator
{
    internal static (string? Text, string? Summary, string? Error) Generate(
        TypeDeclarationSyntax type, CSharpGenerationOptions options, SemanticModel? semanticModel)
    {
        var existing = type.Members.OfType<PropertyDeclarationSyntax>()
            .Select(p => p.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);
        var fields = GenerationSyntax.InstanceFields(type)
            .Select(field => new PropertyGenerationField(
                field.Type.ToString(), field.Identifier.ValueText,
                field.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.ReadOnlyKeyword))))
            .ToList();
        if (semanticModel is not null &&
            GenerationSyntax.FindEquivalentType(type, semanticModel) is { } semanticType &&
            semanticModel.GetDeclaredSymbol(semanticType) is INamedTypeSymbol typeSymbol)
        {
            var activeTree = semanticModel.SyntaxTree;
            var fieldNames = fields.Select(field => field.Name)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var field in typeSymbol.GetMembers().OfType<IFieldSymbol>()
                         .Where(field => !field.IsImplicitlyDeclared && !field.IsStatic && !field.IsConst &&
                             !field.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == activeTree)))
            {
                if (fieldNames.Add(field.Name))
                    fields.Add(new PropertyGenerationField(
                        MemberFormat.DisplayGeneratedType(field.Type), field.Name, field.IsReadOnly));
            }
        }

        fields = fields
            .Where(field => !existing.Contains(GenerationNames.ToPropertyName(field.Name, options.PropertyNaming)))
            .ToList();
        if (fields.Count == 0)
            return (null, null, "生成対象のフィールドがないか、プロパティが既にあります。");

        var members = fields.Select(field =>
        {
            var fieldName = GenerationNames.EscapeIdentifier(field.Name);
            var propertyName = GenerationNames.ToPropertyName(field.Name, options.PropertyNaming);
            var readOnly = field.ReadOnly;
            var setter = readOnly ? "" : $" set => {fieldName} = value;";
            return $"public {field.Type} {propertyName} {{ get => {fieldName};{setter} }}";
        });
        return (string.Join("\n\n", members), "プロパティを生成", null);
    }

    private sealed record PropertyGenerationField(string Type, string Name, bool ReadOnly);
}
