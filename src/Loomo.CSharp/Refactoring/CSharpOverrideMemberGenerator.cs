using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynAccessibility = Microsoft.CodeAnalysis.Accessibility;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>基底クラスの virtual ／ abstract メンバーの override を生成する。abstract は
/// NotImplementedException、それ以外は base 呼び出しを本文にする。</summary>
internal static class CSharpOverrideMemberGenerator
{
    internal static (string? Text, string? Summary, string? Error) Generate(
        TypeDeclarationSyntax type, IReadOnlyList<SyntaxNode> roots, SemanticModel? semanticModel)
    {
        var semanticTypeSymbol = semanticModel is not null
            ? GenerationSyntax.FindEquivalentType(type, semanticModel) is { } semanticType
                ? semanticModel.GetDeclaredSymbol(semanticType) as INamedTypeSymbol
                : null
            : null;
        if (semanticModel is not null &&
            GenerateSemanticOverrideMembers(type, semanticModel) is { } semanticResult)
            return semanticResult;

        var bases = semanticModel is not null
            ? GenerationSyntax.FindSemanticBaseDeclarations(type, semanticModel).ToList()
            : type.BaseList?.Types
                .Select(baseType => GenerationSyntax.BaseTypeName(baseType.Type))
                .Where(name => name.Length > 0)
                .SelectMany(name => GenerationSyntax.FindRelatedTypes<ClassDeclarationSyntax>(type, roots, name))
                .Distinct()
                .ToList() ?? [];
        if (bases.Count == 0 && semanticModel is not null)
        {
            bases = type.BaseList?.Types
                .Select(baseType => GenerationSyntax.BaseTypeName(baseType.Type))
                .Where(name => name.Length > 0)
                .SelectMany(name => GenerationSyntax.FindRelatedTypes<ClassDeclarationSyntax>(type, roots, name))
                .Distinct()
                .ToList() ?? [];
        }
        if (bases.Count == 0)
            return (null, null, "override対象の基底クラス定義を同じファイル内で解決できません。");

        var generated = new List<string>();
        var generatedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var baseType in bases)
        {
            foreach (var member in baseType.Members)
            {
                if (member is EventFieldDeclarationSyntax eventFields)
                {
                    if (!IsOverridableBaseMember(eventFields)) continue;
                    foreach (var variable in eventFields.Declaration.Variables)
                    {
                        var eventKey = "event:" + variable.Identifier.ValueText;
                        if (!generatedKeys.Add(eventKey) || GenerationSyntax.HasEvent(type, variable.Identifier.ValueText) ||
                            (semanticTypeSymbol is not null &&
                             HasSemanticOverride(semanticTypeSymbol, eventFields, semanticModel!,
                                 variable.Identifier.ValueText)))
                            continue;
                        generated.Add(GenerateOverrideEventStub(eventFields.Declaration.Type,
                            variable.Identifier.ValueText, GenerationSyntax.AccessModifier(eventFields.Modifiers) + " override",
                            eventFields.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.AbstractKeyword))));
                    }
                    continue;
                }
                if (!IsOverridableBaseMember(member)) continue;
                var key = member switch
                {
                    MethodDeclarationSyntax method => GenerationSyntax.MethodKey(method.Identifier.ValueText, method.ParameterList),
                    PropertyDeclarationSyntax property => "property:" + property.Identifier.ValueText,
                    EventDeclarationSyntax @event => "event:" + @event.Identifier.ValueText,
                    _ => "",
                };
                if (key.Length == 0 || !generatedKeys.Add(key)) continue;

                switch (member)
                {
                    case MethodDeclarationSyntax method when !GenerationSyntax.HasMethod(type, method)
                        && (semanticTypeSymbol is null ||
                            !HasSemanticOverride(semanticTypeSymbol, method, semanticModel!)):
                        generated.Add(GenerateOverrideMethodStub(
                            method, GenerationSyntax.AccessModifier(method.Modifiers) + " override"));
                        break;
                    case PropertyDeclarationSyntax property when !GenerationSyntax.HasProperty(type, property.Identifier.ValueText)
                        && (semanticTypeSymbol is null ||
                            !HasSemanticOverride(semanticTypeSymbol, property, semanticModel!)):
                        generated.Add(GenerateOverridePropertyStub(
                            property, GenerationSyntax.AccessModifier(property.Modifiers) + " override"));
                        break;
                    case EventDeclarationSyntax @event when !GenerationSyntax.HasEvent(type, @event.Identifier.ValueText)
                        && (semanticTypeSymbol is null ||
                            !HasSemanticOverride(semanticTypeSymbol, @event, semanticModel!)):
                        generated.Add(GenerateOverrideEventStub(
                            @event, GenerationSyntax.AccessModifier(@event.Modifiers) + " override"));
                        break;
                }
            }
        }

        return generated.Count == 0
            ? (null, null, "override可能な未実装メンバーがありません。")
            : (string.Join("\n\n", generated), "overrideメンバーを生成", null);
    }

    /// <summary>ワークスペース内に基底classのソースが無い場合のoverride生成。
    /// BCL／NuGet型のvirtual・abstract memberも、symbolから完全修飾型を取得してstub化する。</summary>
    private static (string? Text, string? Summary, string? Error)? GenerateSemanticOverrideMembers(
        TypeDeclarationSyntax type, SemanticModel semanticModel)
    {
        var semanticType = GenerationSyntax.FindEquivalentType(type, semanticModel);
        if (semanticType is null ||
            semanticModel.GetDeclaredSymbol(semanticType) is not INamedTypeSymbol typeSymbol ||
            typeSymbol.BaseType is not { } baseType)
            return null;

        if (GenerationSyntax.SourceDeclarations<ClassDeclarationSyntax>(baseType).Any()) return null;

        var generated = new List<string>();
        var generatedKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var current = baseType; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (!IsOverridableSymbol(member) ||
                    typeSymbol.GetMembers(member.Name).Any(existing =>
                        IsSameOverridableSignature(existing, member)))
                    continue;

                var key = member.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (!generatedKeys.Add(key)) continue;
                switch (member)
                {
                    case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                        generated.Add(GenerateOverrideMethodStub(method));
                        break;
                    case IPropertySymbol property:
                        generated.Add(GenerateOverridePropertyStub(property));
                        break;
                    case IEventSymbol @event:
                        generated.Add(GenerateOverrideEventStub(@event));
                        break;
                }
            }
        }

        return generated.Count == 0
            ? (null, null, "override可能な未実装メンバーがありません。")
            : (string.Join("\n\n", generated), "overrideメンバーを生成", null);
    }

    private static bool HasSemanticOverride(
        INamedTypeSymbol typeSymbol, MemberDeclarationSyntax member, SemanticModel semanticModel,
        string? memberName = null)
    {
        foreach (var memberSymbol in GenerationSyntax.DeclaredMemberSymbols(member, semanticModel, memberName))
        {
            if (typeSymbol.GetMembers(memberSymbol.Name).Any(existing =>
                    IsSameOverridableSignature(existing, memberSymbol)))
                return true;
        }
        return false;
    }

    private static bool IsOverridableSymbol(ISymbol member)
        => member switch
        {
            IMethodSymbol method => method.MethodKind == MethodKind.Ordinary &&
                !method.IsStatic && method.DeclaredAccessibility != RoslynAccessibility.Private && !method.IsSealed &&
                (method.IsAbstract || method.IsVirtual || method.IsOverride) &&
                method.DeclaredAccessibility is RoslynAccessibility.Public or RoslynAccessibility.Protected or
                    RoslynAccessibility.ProtectedOrInternal,
            IPropertySymbol property => !property.IsStatic && !property.IsWriteOnly && !property.IsSealed &&
                (property.IsAbstract || property.IsVirtual || property.IsOverride) &&
                property.DeclaredAccessibility is RoslynAccessibility.Public or RoslynAccessibility.Protected or
                    RoslynAccessibility.ProtectedOrInternal,
            IEventSymbol @event => !@event.IsStatic && !@event.IsSealed &&
                (@event.IsAbstract || @event.IsVirtual || @event.IsOverride) &&
                @event.DeclaredAccessibility is RoslynAccessibility.Public or RoslynAccessibility.Protected or
                    RoslynAccessibility.ProtectedOrInternal,
            _ => false,
        };

    private static bool IsSameOverridableSignature(ISymbol existing, ISymbol candidate)
    {
        if (existing.Kind != candidate.Kind || !string.Equals(existing.Name, candidate.Name, StringComparison.Ordinal))
            return false;
        return existing switch
        {
            IMethodSymbol left when candidate is IMethodSymbol right =>
                left.Arity == right.Arity && left.Parameters.Length == right.Parameters.Length &&
                left.Parameters.Select(p => p.RefKind).SequenceEqual(right.Parameters.Select(p => p.RefKind)) &&
                left.Parameters.Select(p => p.Type).SequenceEqual(right.Parameters.Select(p => p.Type),
                    SymbolEqualityComparer.Default),
            IPropertySymbol left when candidate is IPropertySymbol right =>
                left.IsIndexer == right.IsIndexer &&
                left.Parameters.Select(p => p.RefKind).SequenceEqual(right.Parameters.Select(p => p.RefKind)) &&
                left.Parameters.Select(p => p.Type).SequenceEqual(right.Parameters.Select(p => p.Type),
                    SymbolEqualityComparer.Default),
            IEventSymbol => true,
            _ => false,
        };
    }

    private static bool IsOverridableBaseMember(MemberDeclarationSyntax member)
        => (member is MethodDeclarationSyntax or PropertyDeclarationSyntax or EventDeclarationSyntax
            or EventFieldDeclarationSyntax)
            && member.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)
                || m.IsKind(SyntaxKind.VirtualKeyword) || m.IsKind(SyntaxKind.OverrideKeyword))
            && !member.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)
                || m.IsKind(SyntaxKind.PrivateKeyword) || m.IsKind(SyntaxKind.SealedKeyword));

    private static string BasePropertyAccess(IPropertySymbol property)
        => property.IsIndexer
            ? "base[" + string.Join(", ", property.Parameters.Select(MemberFormat.FormatParameterArgument)) + "]"
            : "base." + GenerationNames.EscapeIdentifier(property.Name);

    private static string GenerateOverrideMethodStub(IMethodSymbol method)
    {
        var accessibility = MemberFormat.SymbolAccessibility(method.DeclaredAccessibility);
        var returnRef = method.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.RefReadOnly => "ref readonly ",
            _ => "",
        };
        var typeParameters = method.TypeParameters.Length == 0
            ? ""
            : "<" + string.Join(", ", method.TypeParameters.Select(p => GenerationNames.EscapeIdentifier(p.Name))) + ">";
        var constraints = string.Join(" ", method.TypeParameters
            .Select(MemberFormat.FormatTypeParameterConstraints)
            .Where(value => value.Length > 0));
        var returnType = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var call = $"base.{GenerationNames.EscapeIdentifier(method.Name)}{typeParameters}(\n            {string.Join(", ", method.Parameters.Select(MemberFormat.FormatParameterArgument))})";
        var body = method.IsAbstract
            ? "throw new global::System.NotImplementedException();"
            : method.RefKind is RefKind.Ref or RefKind.RefReadOnly
                ? $"return ref {call};"
                : method.ReturnsVoid
                    ? $"{call};"
                    : $"return {call};";
        return $"{accessibility} override {returnRef}{returnType} {GenerationNames.EscapeIdentifier(method.Name)}{typeParameters}({string.Join(", ", method.Parameters.Select(MemberFormat.FormatParameter))}){constraints}\n{{\n    {body}\n}}";
    }

    private static string GenerateOverridePropertyStub(IPropertySymbol property)
    {
        var accessibility = MemberFormat.SymbolAccessibility(property.DeclaredAccessibility);
        var type = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var name = property.IsIndexer
            ? "this[" + string.Join(", ", property.Parameters.Select(MemberFormat.FormatParameter)) + "]"
            : GenerationNames.EscapeIdentifier(property.Name);
        var accessors = new List<string>();
        if (property.GetMethod is not null)
            accessors.Add(property.GetMethod.IsAbstract
                ? "get;"
                : $"get => {BasePropertyAccess(property)};");
        if (property.SetMethod is not null)
            accessors.Add(property.SetMethod.IsAbstract
                ? property.SetMethod.IsInitOnly
                    ? "init;"
                    : "set;"
                : property.SetMethod.IsInitOnly
                    ? $"init => {BasePropertyAccess(property)} = value;"
                    : $"set => {BasePropertyAccess(property)} = value;");
        return $"{accessibility} override {type} {name} {{ {string.Join(" ", accessors)} }}";
    }

    private static string GenerateOverrideEventStub(IEventSymbol @event)
    {
        var accessibility = MemberFormat.SymbolAccessibility(@event.DeclaredAccessibility);
        var type = @event.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var access = @event.IsAbstract
            ? ("add => throw new global::System.NotImplementedException();", "remove => throw new global::System.NotImplementedException();")
            : ($"add => base.{GenerationNames.EscapeIdentifier(@event.Name)} += value;",
                $"remove => base.{GenerationNames.EscapeIdentifier(@event.Name)} -= value;");
        return $"{accessibility} override event {type} {GenerationNames.EscapeIdentifier(@event.Name)}\n{{\n    {access.Item1}\n    {access.Item2}\n}}";
    }

    private static string GenerateOverrideMethodStub(MethodDeclarationSyntax method, string modifier)
    {
        var hasRef = method.Modifiers.Any(token => token.IsKind(SyntaxKind.RefKeyword));
        var returnRef = hasRef && method.Modifiers.Any(token => token.IsKind(SyntaxKind.ReadOnlyKeyword))
            ? "ref readonly "
            : hasRef ? "ref " : "";
        var generic = method.TypeParameterList?.ToString() ?? "";
        var constraints = method.ConstraintClauses.Count == 0
            ? ""
            : " " + string.Join(" ", method.ConstraintClauses.Select(clause => clause.ToString()));
        var call = $"base.{method.Identifier}{generic}({string.Join(", ",
            method.ParameterList.Parameters.Select(MemberFormat.FormatParameterArgument))})";
        var body = method.Modifiers.Any(token => token.IsKind(SyntaxKind.AbstractKeyword))
            ? "throw new global::System.NotImplementedException();"
            : hasRef
                ? $"return ref {call};"
                : MemberFormat.IsVoid(method.ReturnType)
                    ? $"{call};"
                    : $"return {call};";
        return $"{modifier} {returnRef}{method.ReturnType} {method.Identifier}{generic}({MemberFormat.FormatParameters(method.ParameterList)}){constraints}\n{{\n    {body}\n}}";
    }

    private static string GenerateOverridePropertyStub(PropertyDeclarationSyntax property, string modifier)
    {
        var name = property.Identifier.ValueText;
        var receiver = "base." + property.Identifier.ValueText;
        var isAbstract = property.Modifiers.Any(token => token.IsKind(SyntaxKind.AbstractKeyword));
        var accessors = property.AccessorList?.Accessors
            .Where(accessor => accessor.Kind() is SyntaxKind.GetAccessorDeclaration
                or SyntaxKind.SetAccessorDeclaration or SyntaxKind.InitAccessorDeclaration)
            .Select(accessor => accessor.Kind() == SyntaxKind.GetAccessorDeclaration
                ? isAbstract ? "get;" : $"get => {receiver};"
                : accessor.Kind() == SyntaxKind.InitAccessorDeclaration
                    ? isAbstract ? "init;" : $"init => {receiver} = value;"
                    : isAbstract ? "set;" : $"set => {receiver} = value;")
            .ToList() ?? [];
        if (accessors.Count == 0 && property.ExpressionBody is not null)
            accessors.Add(isAbstract
                ? "get;"
                : $"get => {receiver};");
        if (accessors.Count == 0) accessors.Add("get;");
        return $"{modifier} {property.Type} {name} {{ {string.Join(" ", accessors)} }}";
    }

    private static string GenerateOverrideEventStub(
        EventDeclarationSyntax @event, string modifier)
        => GenerateOverrideEventStub(@event.Type, @event.Identifier.ValueText, modifier,
            @event.Modifiers.Any(token => token.IsKind(SyntaxKind.AbstractKeyword)));

    private static string GenerateOverrideEventStub(
        TypeSyntax type, string name, string modifier, bool isAbstract)
    {
        var add = isAbstract
            ? "add => throw new global::System.NotImplementedException();"
            : $"add => base.{GenerationNames.EscapeIdentifier(name)} += value;";
        var remove = isAbstract
            ? "remove => throw new global::System.NotImplementedException();"
            : $"remove => base.{GenerationNames.EscapeIdentifier(name)} -= value;";
        return $"{modifier} event {type} {GenerationNames.EscapeIdentifier(name)}\n{{\n    {add}\n    {remove}\n}}";
    }
}
