using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>実装対象 interface の未実装メンバーを stub 生成する。ワークスペース内にソースがある
/// interface は構文から、BCL ／ NuGet の interface は意味モデルからそれぞれ組み立てる。</summary>
internal static class CSharpInterfaceImplementationGenerator
{
    internal static (string? Text, string? Summary, string? Error) Generate(
        TypeDeclarationSyntax type, IReadOnlyList<SyntaxNode> roots, SemanticModel? semanticModel)
    {
        var semanticTypeSymbol = semanticModel is not null
            ? GenerationSyntax.FindEquivalentType(type, semanticModel) is { } semanticType
                ? semanticModel.GetDeclaredSymbol(semanticType) as INamedTypeSymbol
                : null
            : null;
        var semanticResult = semanticModel is null
            ? null
            : GenerateSemanticInterfaceMembers(type, semanticModel);

        var interfaces = semanticModel is not null
            ? GenerationSyntax.FindSemanticInterfaceHierarchy(type, semanticModel).ToList()
            : GenerationSyntax.FindInterfaceHierarchy(type, roots,
                type.BaseList?.Types.Select(baseType => GenerationSyntax.BaseTypeName(baseType.Type)) ?? [])
                .ToList();
        if (interfaces.Count == 0 && semanticModel is not null)
        {
            // メタデータだけのinterfaceはソースstubを生成できないため、同一ソースのfallbackを
            // 最後に試す。ただし意味モデルで一意に解決できた結果を名前検索で広げない。
            interfaces = GenerationSyntax.FindInterfaceHierarchy(type, roots,
                type.BaseList?.Types.Select(baseType => GenerationSyntax.BaseTypeName(baseType.Type)) ?? [])
                .ToList();
        }
        if (interfaces.Count == 0 && semanticResult?.Text is null)
            return semanticResult ?? (null, null, "実装対象のインターフェース定義を同じファイル内で解決できません。");

        var generated = new List<string>();
        var generatedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in interfaces)
        {
            foreach (var member in contract.Members)
            {
                if (member is EventFieldDeclarationSyntax eventFields)
                {
                    if (!IsImplementableContractMember(eventFields)) continue;
                    foreach (var variable in eventFields.Declaration.Variables)
                    {
                        var eventKey = "event:" + variable.Identifier.ValueText;
                        if (!generatedKeys.Add(eventKey) || GenerationSyntax.HasEvent(type, variable.Identifier.ValueText) ||
                            (semanticTypeSymbol is not null &&
                             HasSemanticInterfaceImplementation(semanticTypeSymbol, eventFields, semanticModel!,
                                 variable.Identifier.ValueText)))
                            continue;
                        generated.Add(GenerateEventStub(eventFields.Declaration.Type,
                            variable.Identifier.ValueText, "public"));
                    }
                    continue;
                }
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
                    case MethodDeclarationSyntax method when IsImplementableContractMember(method)
                        && !GenerationSyntax.HasMethod(type, method)
                        && (semanticTypeSymbol is null ||
                            !HasSemanticInterfaceImplementation(semanticTypeSymbol, method, semanticModel!)):
                        generated.Add(GenerateMethodStub(method, "public"));
                        break;
                    case PropertyDeclarationSyntax property when IsImplementableContractMember(property)
                        && !GenerationSyntax.HasProperty(type, property.Identifier.ValueText)
                        && (semanticTypeSymbol is null ||
                            !HasSemanticInterfaceImplementation(semanticTypeSymbol, property, semanticModel!)):
                        generated.Add(GeneratePropertyStub(property, "public"));
                        break;
                    case EventDeclarationSyntax @event when IsImplementableContractMember(@event)
                        && !GenerationSyntax.HasEvent(type, @event.Identifier.ValueText)
                        && (semanticTypeSymbol is null ||
                            !HasSemanticInterfaceImplementation(semanticTypeSymbol, @event, semanticModel!)):
                        generated.Add(GenerateEventStub(@event, "public"));
                        break;
                }
            }
        }

        if (semanticResult?.Text is { Length: > 0 } semanticText)
            generated.Add(semanticText);

        if (generated.Count == 0)
            return semanticResult ?? (null, null, "インターフェースの未実装メンバーがないか、構文だけでは生成できないメンバーです。");
        return (string.Join("\n\n", generated), "インターフェースメンバーを生成", null);
    }

    /// <summary>ソース宣言を持たないBCL／NuGet interfaceを意味モデルから生成する。
    /// SourceDeclarationsが空でも、member symbolのidentityと型引数は失わない。</summary>
    private static (string? Text, string? Summary, string? Error)? GenerateSemanticInterfaceMembers(
        TypeDeclarationSyntax type, SemanticModel semanticModel)
    {
        var semanticType = GenerationSyntax.FindEquivalentType(type, semanticModel);
        if (semanticType is null ||
            semanticModel.GetDeclaredSymbol(semanticType) is not INamedTypeSymbol typeSymbol)
            return null;

        var interfaces = typeSymbol.AllInterfaces
            .Where(contract => contract.TypeKind == TypeKind.Interface)
            .GroupBy(contract => contract.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (interfaces.Length == 0) return null;

        var generated = new List<string>();
        var generatedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in interfaces.Where(contract =>
                     !GenerationSyntax.SourceDeclarations<InterfaceDeclarationSyntax>(contract).Any()))
        {
            foreach (var member in contract.GetMembers())
            {
                if (!GenerationSyntax.IsAbstractInterfaceMember(member) ||
                    typeSymbol.FindImplementationForInterfaceMember(member) is not null)
                    continue;

                switch (member)
                {
                    case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                    {
                        var key = "method:" + method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        if (generatedKeys.Add(key)) generated.Add(GenerateMethodStub(method));
                        break;
                    }
                    case IPropertySymbol property:
                    {
                        var key = "property:" + property.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        if (generatedKeys.Add(key)) generated.Add(GeneratePropertyStub(property));
                        break;
                    }
                    case IEventSymbol @event:
                    {
                        var key = "event:" + @event.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        if (generatedKeys.Add(key)) generated.Add(GenerateEventStub(@event));
                        break;
                    }
                }
            }
        }

        return generated.Count == 0
            ? (null, null, "インターフェースの未実装メンバーがありません。")
            : (string.Join("\n\n", generated), "インターフェースメンバーを生成", null);
    }

    /// <summary>partial型の別宣言に既存実装があるかを、構文上の名前ではなくsymbol identityで確認する。
    /// active fileだけを見ると、ImplementInterface／OverrideMembersが重複メンバーを生成してしまう。</summary>
    private static bool HasSemanticInterfaceImplementation(
        INamedTypeSymbol typeSymbol, MemberDeclarationSyntax member, SemanticModel semanticModel,
        string? memberName = null)
    {
        foreach (var memberSymbol in GenerationSyntax.DeclaredMemberSymbols(member, semanticModel, memberName))
        {
            if (typeSymbol.FindImplementationForInterfaceMember(memberSymbol) is not null)
                return true;
        }
        return false;
    }

    private static bool IsImplementableContractMember(MemberDeclarationSyntax member)
    {
        if (member.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) ||
                                      m.IsKind(SyntaxKind.PrivateKeyword)))
            return false;

        // Default interface members already have an implementation in the contract and
        // must not be copied into the implementing type. A syntax fallback can still
        // distinguish declaration-only members from members with a body.
        return member switch
        {
            MethodDeclarationSyntax method => method.Body is null && method.ExpressionBody is null,
            PropertyDeclarationSyntax property => property.ExpressionBody is null &&
                property.AccessorList?.Accessors.All(accessor =>
                    accessor.Body is null && accessor.ExpressionBody is null) == true,
            EventDeclarationSyntax @event => @event.AccessorList?.Accessors.All(accessor =>
                    accessor.Body is null && accessor.ExpressionBody is null) == true,
            EventFieldDeclarationSyntax => true,
            _ => false,
        };
    }

    private static string GenerateMethodStub(IMethodSymbol method)
    {
        var parameters = string.Join(", ", method.Parameters.Select(MemberFormat.FormatParameter));
        var typeParameters = method.TypeParameters.Length == 0
            ? ""
            : "<" + string.Join(", ", method.TypeParameters.Select(p => GenerationNames.EscapeIdentifier(p.Name))) + ">";
        var constraints = string.Join(" ", method.TypeParameters
            .Select(MemberFormat.FormatTypeParameterConstraints)
            .Where(value => value.Length > 0));
        var returnRef = method.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.RefReadOnly => "ref readonly ",
            _ => "",
        };
        var returnType = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return $"public {returnRef}{returnType} {GenerationNames.EscapeIdentifier(method.Name)}{typeParameters}({parameters}){constraints}\n{{\n    throw new global::System.NotImplementedException();\n}}";
    }

    private static string GeneratePropertyStub(IPropertySymbol property)
    {
        var type = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var name = property.IsIndexer
            ? "this[" + string.Join(", ", property.Parameters.Select(MemberFormat.FormatParameter)) + "]"
            : GenerationNames.EscapeIdentifier(property.Name);
        var accessors = new List<string>();
        if (property.GetMethod is not null) accessors.Add("get;");
        if (property.SetMethod is not null)
            accessors.Add(property.SetMethod.IsInitOnly ? "init;" : "set;");
        if (accessors.Count == 0) return "";
        return $"public {type} {name} {{ {string.Join(" ", accessors)} }}";
    }

    private static string GenerateEventStub(IEventSymbol @event)
    {
        var type = @event.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return $"public event {type} {GenerationNames.EscapeIdentifier(@event.Name)}\n{{\n    add => throw new global::System.NotImplementedException();\n    remove => throw new global::System.NotImplementedException();\n}}";
    }

    private static string GenerateMethodStub(MethodDeclarationSyntax method, string modifier)
    {
        var hasRef = method.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword));
        var returnRef = hasRef && method.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword))
            ? "ref readonly "
            : hasRef ? "ref " : "";
        var generic = method.TypeParameterList?.ToString() ?? "";
        var constraints = method.ConstraintClauses.Count == 0
            ? ""
            : " " + string.Join(" ", method.ConstraintClauses.Select(c => c.ToString()));
        return $"{modifier} {returnRef}{method.ReturnType} {method.Identifier}{generic}({MemberFormat.FormatParameters(method.ParameterList)}){constraints}\n{{\n    throw new global::System.NotImplementedException();\n}}";
    }

    private static string GeneratePropertyStub(PropertyDeclarationSyntax property, string modifier)
    {
        var accessors = property.AccessorList?.Accessors
            .Where(a => a.Kind() is SyntaxKind.GetAccessorDeclaration or SyntaxKind.SetAccessorDeclaration
                or SyntaxKind.InitAccessorDeclaration)
            .Select(a => a.Kind() == SyntaxKind.GetAccessorDeclaration ? "get;" :
                a.Kind() == SyntaxKind.InitAccessorDeclaration ? "init;" : "set;")
            .ToList() ?? [];
        if (accessors.Count == 0) accessors.Add("get;");
        return $"{modifier} {property.Type} {property.Identifier} {{ {string.Join(" ", accessors)} }}";
    }

    private static string GenerateEventStub(EventDeclarationSyntax @event, string modifier)
        => GenerateEventStub(@event.Type, @event.Identifier.ValueText, modifier);

    private static string GenerateEventStub(TypeSyntax type, string name, string modifier)
        => $"{modifier} event {type} {name}\n{{\n    add => throw new global::System.NotImplementedException();\n    remove => throw new global::System.NotImplementedException();\n}}";
}
