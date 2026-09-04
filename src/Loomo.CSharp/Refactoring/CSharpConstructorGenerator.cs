using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynAccessibility = Microsoft.CodeAnalysis.Accessibility;
using sk0ya.Loomo.CSharp.Configuration;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>インスタンスフィールド／auto-property からコンストラクターを生成する。
/// 基底コンストラクターの呼び出しと、基底型の required メンバーの引き継ぎもここで解決する。</summary>
internal static class CSharpConstructorGenerator
{
    internal static (string? Text, string? Summary, string? Error) Generate(
        TypeDeclarationSyntax type, CSharpGenerationOptions options, SemanticModel? semanticModel)
    {
        // C# 12のprimary constructorは通常のConstructorDeclarationSyntaxとして
        // Membersに現れない。フィールドを持つ型でもここへ通常constructorを重ねると
        // 同じ責務の二重生成になるため、primary constructorは明示的に対象外にする。
        if (type.ParameterList is not null)
            return (null, null, "primary constructorを持つ型にはconstructorを追加できません。");

        var fields = GenerationSyntax.InstanceFields(type).ToList();
        var properties = GenerationSyntax.InstanceAutoProperties(type)
            .Where(property => !fields.Any(field =>
                string.Equals(GenerationNames.ToPropertyName(field.Identifier.ValueText, options.PropertyNaming),
                    property.Identifier.ValueText, StringComparison.Ordinal)))
            .ToList();
        var semanticTypeSymbol = semanticModel is not null &&
            GenerationSyntax.FindEquivalentType(type, semanticModel) is { } semanticTypeNode
            ? semanticModel.GetDeclaredSymbol(semanticTypeNode) as INamedTypeSymbol
            : null;
        var constructorMembers = fields
            .Select(field => new ConstructorMember(field.Identifier.ValueText, field.Type.ToString()))
            .Concat(properties.Select(property =>
                new ConstructorMember(property.Identifier.ValueText, property.Type.ToString())))
            .ToList();
        if (semanticTypeSymbol is not null)
            AddSemanticPartialConstructorMembers(type, semanticTypeSymbol, semanticModel!, constructorMembers,
                options.PropertyNaming);

        if (constructorMembers.Count == 0 &&
            (semanticTypeSymbol is null || !HasSemanticBaseConstructionTarget(semanticTypeSymbol)))
            return (null, null, "生成対象のインスタンスフィールドがありません。");

        var typeName = type.Identifier.ValueText;
        if (type.Members.OfType<ConstructorDeclarationSyntax>()
            .Any(c => string.Equals(c.Identifier.ValueText, typeName, StringComparison.Ordinal)))
            return (null, null, "コンストラクターが既にあります。");

        var usedParameters = new HashSet<string>(StringComparer.Ordinal);
        var parameters = new List<string>();
        var assignments = new List<string>();
        var baseInitializer = "";
        var requiredBaseMembers = Array.Empty<ISymbol>();
        var baseSetsRequiredMembers = false;
        if (semanticTypeSymbol is { } typeSymbol)
        {
            var baseResult = GetBaseConstructor(typeSymbol);
            if (baseResult.Error is not null)
                return (null, null, baseResult.Error);
            if (baseResult.Constructor is { } baseConstructor)
            {
                var baseArguments = new List<string>();
                foreach (var parameter in baseConstructor.Parameters)
                {
                    var parameterName = GenerationNames.MakeUniqueParameterName(parameter.Name, usedParameters,
                        options.ParameterNaming);
                    parameters.Add(MemberFormat.FormatParameter(parameter, parameterName));
                    baseArguments.Add(MemberFormat.FormatParameterArgument(parameter, parameterName));
                }
                baseInitializer = $" : base({string.Join(", ", baseArguments)})";
            }

            var requiredBaseResult = GetRequiredBaseMembers(typeSymbol, baseResult.Constructor);
            if (requiredBaseResult.Error is not null)
                return (null, null, requiredBaseResult.Error);
            requiredBaseMembers = requiredBaseResult.Members.ToArray();
            baseSetsRequiredMembers = requiredBaseResult.BaseConstructorSetsRequiredMembers;
            foreach (var requiredMember in requiredBaseMembers)
            {
                var parameter = GenerationNames.MakeUniqueParameterName(requiredMember.Name, usedParameters,
                    options.ParameterNaming);
                parameters.Add($"{MemberFormat.DisplayGeneratedType(GetMemberType(requiredMember))} {parameter}");
                assignments.Add($"base.{GenerationNames.EscapeIdentifier(requiredMember.Name)} = {parameter};");
            }
        }
        foreach (var field in constructorMembers)
        {
            var memberName = field.Name;
            var parameter = GenerationNames.MakeUniqueParameterName(memberName, usedParameters,
                options.ParameterNaming);
            parameters.Add($"{field.Type} {parameter}");
            assignments.Add($"this.{GenerationNames.EscapeIdentifier(memberName)} = {parameter};");
        }

        var body = string.Join("\n", assignments.Select(a => "    " + a));
        var requiredAttribute = HasRequiredMember(type) || requiredBaseMembers.Length > 0 ||
            baseSetsRequiredMembers
            ? "[global::System.Diagnostics.CodeAnalysis.SetsRequiredMembers]\n"
            : "";
        var generated = $"{requiredAttribute}public {typeName}({string.Join(", ", parameters)}){baseInitializer}\n{{\n{body}\n}}";
        return (generated, "コンストラクターを生成", null);
    }

    private static bool HasRequiredMember(TypeDeclarationSyntax type)
        => type.Members.Any(member => member switch
        {
            FieldDeclarationSyntax field => field.Modifiers.Any(modifier =>
                modifier.IsKind(SyntaxKind.RequiredKeyword)),
            PropertyDeclarationSyntax property => property.Modifiers.Any(modifier =>
                modifier.IsKind(SyntaxKind.RequiredKeyword)),
            _ => false,
        });

    private static void AddSemanticPartialConstructorMembers(
        TypeDeclarationSyntax activeType,
        INamedTypeSymbol typeSymbol,
        SemanticModel semanticModel,
        List<ConstructorMember> members,
        CSharpNamingStyle? propertyNaming)
    {
        var activeTree = semanticModel.SyntaxTree;
        var fieldNames = members.Select(member => member.Name)
            .ToHashSet(StringComparer.Ordinal);
        var propertyNames = activeType.Members.OfType<PropertyDeclarationSyntax>()
            .Select(property => property.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var field in typeSymbol.GetMembers().OfType<IFieldSymbol>()
                     .Where(field => !field.IsImplicitlyDeclared && !field.IsStatic && !field.IsConst &&
                         !field.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == activeTree)))
        {
            if (!fieldNames.Add(field.Name)) continue;
            members.Add(new ConstructorMember(field.Name, MemberFormat.DisplayGeneratedType(field.Type)));
        }

        foreach (var property in typeSymbol.GetMembers().OfType<IPropertySymbol>()
                     .Where(property => !property.IsStatic && !property.IsIndexer &&
                         !property.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == activeTree)))
        {
            var syntax = GenerationSyntax.GetPropertyDeclaration(property);
            if (syntax is null || !GenerationSyntax.IsConstructorProperty(syntax) ||
                !propertyNames.Add(property.Name)) continue;
            var fieldPropertyName = fieldNames.Any(fieldName =>
                string.Equals(GenerationNames.ToPropertyName(fieldName, propertyNaming), property.Name,
                    StringComparison.Ordinal));
            if (fieldPropertyName) continue;
            members.Add(new ConstructorMember(property.Name, MemberFormat.DisplayGeneratedType(property.Type)));
        }
    }

    private static bool HasSemanticBaseConstructionTarget(INamedTypeSymbol type)
    {
        var baseType = type.BaseType;
        if (baseType is null || baseType.SpecialType == SpecialType.System_Object ||
            type.TypeKind == TypeKind.Struct)
            return false;

        var constructors = baseType.InstanceConstructors
            .Where(constructor => constructor.DeclaredAccessibility is
                RoslynAccessibility.Public or RoslynAccessibility.Internal or
                RoslynAccessibility.Protected or RoslynAccessibility.ProtectedOrInternal)
            .ToArray();
        if (constructors.Any(constructor => constructor.Parameters.Length > 0))
            return true;

        for (var current = baseType;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            if (current.GetMembers().Any(IsRequiredMember))
                return true;
        }
        return false;
    }

    private static bool IsRequiredMember(ISymbol member)
        => member switch
        {
            IFieldSymbol field => field.IsRequired,
            IPropertySymbol property => property.IsRequired,
            _ => false,
        };

    private static (IMethodSymbol? Constructor, string? Error) GetBaseConstructor(
        INamedTypeSymbol type)
    {
        var baseType = type.BaseType;
        if (baseType is null || baseType.SpecialType == SpecialType.System_Object ||
            type.TypeKind == TypeKind.Struct)
            return (null, null);

        var constructors = baseType.InstanceConstructors
            .Where(constructor => constructor.DeclaredAccessibility is
                RoslynAccessibility.Public or RoslynAccessibility.Internal or
                RoslynAccessibility.Protected or RoslynAccessibility.ProtectedOrInternal)
            .ToList();
        if (constructors.Any(constructor => constructor.Parameters.Length == 0))
            return (null, null);
        if (constructors.Count == 0)
            return (null, "呼び出し可能な基底クラスコンストラクターがありません。");
        if (constructors.Count != 1)
            return (null, "基底クラスに複数のコンストラクターがあるため、呼び出し先を選択してから生成してください。");
        return (constructors[0], null);
    }

    /// <summary>基底型のrequired契約をコンストラクター生成へ引き継ぐ。
    /// base constructorがSetsRequiredMembersを持たない場合は、派生型から代入できるメンバーだけを
    /// パラメーター化し、private／readonlyなど安全に満たせない契約は生成自体を拒否する。</summary>
    private static (IReadOnlyList<ISymbol> Members, string? Error,
        bool BaseConstructorSetsRequiredMembers) GetRequiredBaseMembers(
        INamedTypeSymbol type, IMethodSymbol? selectedBaseConstructor)
    {
        var baseType = type.BaseType;
        if (baseType is null || baseType.SpecialType == SpecialType.System_Object ||
            type.TypeKind == TypeKind.Struct)
            return ([], null, false);

        var effectiveConstructor = selectedBaseConstructor ?? baseType.InstanceConstructors
            .FirstOrDefault(constructor => constructor.Parameters.Length == 0);
        var baseSetsRequiredMembers = effectiveConstructor is not null &&
            HasSetsRequiredMembers(effectiveConstructor);
        if (baseSetsRequiredMembers)
            return ([], null, true);

        var required = new List<ISymbol>();
        for (var current = baseType;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            required.AddRange(current.GetMembers().Where(IsRequiredMember));
        }

        foreach (var member in required)
        {
            if (!CanAssignRequiredMember(member))
                return ([], $"基底型のrequiredメンバー「{member.Name}」を派生コンストラクターから初期化できません。", false);
        }
        return (required, null, false);
    }

    private static bool HasSetsRequiredMembers(IMethodSymbol constructor)
        => constructor.GetAttributes().Any(attribute =>
            string.Equals(attribute.AttributeClass?.ToDisplayString(),
                "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute",
                StringComparison.Ordinal));

    private static bool CanAssignRequiredMember(ISymbol member)
        => member switch
        {
            IFieldSymbol field => field.DeclaredAccessibility != RoslynAccessibility.Private && !field.IsReadOnly,
            IPropertySymbol property => property.SetMethod is { } setter &&
                setter.DeclaredAccessibility != RoslynAccessibility.Private,
            _ => false,
        };

    private static ITypeSymbol GetMemberType(ISymbol member)
        => member switch
        {
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => throw new ArgumentException("requiredメンバーの型を解決できません。", nameof(member)),
        };

    private sealed record ConstructorMember(string Name, string Type);
}
