using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>キャレット位置のフィールドが実装する interface のメンバーを、そのフィールドへ委譲する
/// 形で生成する。</summary>
internal static class CSharpDelegatingMemberGenerator
{
    /// <summary>キャレット位置のフィールドが実装するinterfaceから、単純な委譲メンバーを生成する。
    /// 意味モデルなしでは型引数の代入や明示的実装を解決できないため、非ジェネリックなinterfaceの
    /// 通常メソッド／プロパティ／イベントだけを対象にする。</summary>
    internal static (string? Text, string? Summary, string? Error) Generate(
        TypeDeclarationSyntax type, IReadOnlyList<SyntaxNode> roots, int position,
        SemanticModel? semanticModel)
    {
        var fields = type.Members.OfType<FieldDeclarationSyntax>()
            .Where(field => field.Declaration.Variables.Count == 1
                && position >= field.SpanStart && position <= field.Span.End)
            .ToList();
        if (fields.Count != 1)
            return (null, null, "委譲先フィールドの中にキャレットを置いてください。");

        var field = fields[0];
        if (field.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)
                || modifier.IsKind(SyntaxKind.ConstKeyword)))
            return (null, null, "インスタンスフィールドからのみ委譲メンバーを生成できます。");
        if (field.Declaration.Type.DescendantNodesAndSelf().OfType<GenericNameSyntax>().Any())
        {
            // Roslynの意味モデルがあれば、IList<string>のような構築済み型も安全に解決できる。
            if (semanticModel is null)
                return (null, null, "ジェネリックな委譲先は構文だけでは型引数を解決できません。");
        }

        if (semanticModel is not null &&
            GenerateSemanticDelegatingMembers(type, field, semanticModel) is { } semanticResult)
            return semanticResult;

        var delegateName = GenerationSyntax.BaseTypeName(field.Declaration.Type);
        var contracts = semanticModel is not null
            ? GenerationSyntax.FindSemanticFieldInterfaceHierarchy(field, semanticModel).ToList()
            : GenerationSyntax.FindInterfaceHierarchy(type, roots, [delegateName]).ToList();
        if (contracts.Count == 0 && semanticModel is not null)
            contracts = GenerationSyntax.FindInterfaceHierarchy(type, roots, [delegateName]).ToList();
        if (contracts.Count == 0)
            return (null, null, "委譲先のinterface定義を同じワークスペース内で一意に解決できません。");

        var fieldName = field.Declaration.Variables[0].Identifier.ValueText;
        var generated = new List<string>();
        var generatedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in contracts.SelectMany(contract => contract.Members))
        {
            if (!IsPubliclyDelegatable(member)) continue;

            switch (member)
            {
                case MethodDeclarationSyntax method
                    when method.TypeParameterList is null
                        && !method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.RefKeyword))
                        && !GenerationSyntax.HasMethod(type, method)
                        && generatedKeys.Add(GenerationSyntax.MethodKey(method.Identifier.ValueText, method.ParameterList)):
                    generated.Add(GenerateDelegatingMethod(method, fieldName));
                    break;
                case PropertyDeclarationSyntax property
                    when property.AccessorList is not null
                        && !property.AccessorList.Accessors.Any(accessor =>
                            accessor.IsKind(SyntaxKind.InitAccessorDeclaration))
                        && !GenerationSyntax.HasProperty(type, property.Identifier.ValueText)
                        && generatedKeys.Add("property:" + property.Identifier.ValueText):
                    generated.Add(GenerateDelegatingProperty(property, fieldName));
                    break;
                case EventDeclarationSyntax @event
                    when !GenerationSyntax.HasEvent(type, @event.Identifier.ValueText)
                        && generatedKeys.Add("event:" + @event.Identifier.ValueText):
                    generated.Add(GenerateDelegatingEvent(@event.Type, @event.Identifier.ValueText, fieldName));
                    break;
                case EventFieldDeclarationSyntax eventFields:
                    foreach (var variable in eventFields.Declaration.Variables)
                    {
                        var key = "event:" + variable.Identifier.ValueText;
                        if (!generatedKeys.Add(key) || GenerationSyntax.HasEvent(type, variable.Identifier.ValueText)) continue;
                        generated.Add(GenerateDelegatingEvent(
                            eventFields.Declaration.Type, variable.Identifier.ValueText, fieldName));
                    }
                    break;
            }
        }

        return generated.Count == 0
            ? (null, null, "委譲可能なinterfaceメンバーがないか、既に実装されています。")
            : (string.Join("\n\n", generated),
                $"フィールド「{fieldName}」から委譲メンバーを生成", null);
    }

    /// <summary>ソース宣言を持たないBCL／NuGet interfaceをフィールドから委譲する。
    /// 構築済みgeneric typeの型引数もsymbolから展開して、objectへの劣化を避ける。</summary>
    private static (string? Text, string? Summary, string? Error)? GenerateSemanticDelegatingMembers(
        TypeDeclarationSyntax type, FieldDeclarationSyntax field, SemanticModel semanticModel)
    {
        var semanticField = semanticModel.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.SpanStart == field.SpanStart);
        var semanticVariable = semanticField?.Declaration.Variables.FirstOrDefault(variable =>
            string.Equals(variable.Identifier.ValueText,
                field.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText,
                StringComparison.Ordinal));
        if (semanticVariable is null || semanticModel.GetDeclaredSymbol(semanticVariable) is not IFieldSymbol fieldSymbol ||
            fieldSymbol.Type is not INamedTypeSymbol fieldType)
            return null;

        var contracts = new[] { fieldType }
            .Concat(fieldType.AllInterfaces)
            .Where(contract => contract.TypeKind == TypeKind.Interface)
            .GroupBy(contract => contract.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (contracts.Length == 0 || contracts.Any(contract =>
                GenerationSyntax.SourceDeclarations<InterfaceDeclarationSyntax>(contract).Any()))
            return null;

        var containingType = semanticModel.GetDeclaredSymbol(
            GenerationSyntax.FindEquivalentType(type, semanticModel)!) as INamedTypeSymbol;
        if (containingType is null) return null;

        var fieldName = fieldSymbol.Name;
        var generated = new List<string>();
        var generatedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in contracts)
        foreach (var member in contract.GetMembers())
        {
            if (!GenerationSyntax.IsAbstractInterfaceMember(member) ||
                containingType.FindImplementationForInterfaceMember(member) is not null)
                continue;

            switch (member)
            {
                case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                    if (generatedKeys.Add(SymbolMemberKey(method)))
                        generated.Add(GenerateDelegatingMethod(method, fieldName));
                    break;
                case IPropertySymbol property when !property.IsWriteOnly &&
                    (property.SetMethod is null || !property.SetMethod.IsInitOnly):
                    if (generatedKeys.Add(SymbolMemberKey(property)))
                        generated.Add(GenerateDelegatingProperty(property, fieldName));
                    break;
                case IEventSymbol @event:
                    if (generatedKeys.Add(SymbolMemberKey(@event)))
                        generated.Add(GenerateDelegatingEvent(@event, fieldName));
                    break;
            }
        }

        return generated.Count == 0
            ? (null, null, "委譲可能なinterfaceメンバーがないか、既に実装されています。")
            : (string.Join("\n\n", generated),
                $"フィールド「{fieldName}」から委譲メンバーを生成", null);
    }

    private static bool IsPubliclyDelegatable(MemberDeclarationSyntax member)
        => !member.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)
            || modifier.IsKind(SyntaxKind.PrivateKeyword)
            || modifier.IsKind(SyntaxKind.ProtectedKeyword)
            || modifier.IsKind(SyntaxKind.InternalKeyword));

    private static string SymbolMemberKey(ISymbol member)
        => member switch
        {
            IMethodSymbol method => "method:" + method.Name + "/" + method.Arity + "/" +
                string.Join(",", method.Parameters.Select(parameter =>
                    parameter.RefKind + ":" + parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))),
            IPropertySymbol property => "property:" + property.Name + "/" + property.IsIndexer + "/" +
                string.Join(",", property.Parameters.Select(parameter => parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))),
            IEventSymbol @event => "event:" + @event.Name,
            _ => member.Kind + ":" + member.Name,
        };

    private static string FormatRefReturn(IMethodSymbol method)
        => method.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.RefReadOnly => "ref readonly ",
            _ => "",
        };

    private static string GenerateDelegatingMethod(IMethodSymbol method, string fieldName)
    {
        var typeParameters = method.TypeParameters.Length == 0
            ? ""
            : "<" + string.Join(", ", method.TypeParameters.Select(p => GenerationNames.EscapeIdentifier(p.Name))) + ">";
        var call = fieldName + "." + GenerationNames.EscapeIdentifier(method.Name) + typeParameters + "(" +
            string.Join(", ", method.Parameters.Select(MemberFormat.FormatParameterArgument)) + ")";
        var body = method.ReturnsVoid ? $"    {call};" : $"    return {call};";
        var constraints = string.Join(" ", method.TypeParameters
            .Select(MemberFormat.FormatTypeParameterConstraints)
            .Where(value => value.Length > 0));
        return $"public {FormatRefReturn(method)}{method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {GenerationNames.EscapeIdentifier(method.Name)}{typeParameters}({string.Join(", ", method.Parameters.Select(MemberFormat.FormatParameter))}){constraints}\n{{\n{body}\n}}";
    }

    private static string GenerateDelegatingProperty(IPropertySymbol property, string fieldName)
    {
        var type = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var name = property.IsIndexer
            ? "this[" + string.Join(", ", property.Parameters.Select(MemberFormat.FormatParameter)) + "]"
            : GenerationNames.EscapeIdentifier(property.Name);
        var arguments = string.Join(", ", property.Parameters.Select(MemberFormat.FormatParameterArgument));
        var receiver = fieldName + (property.IsIndexer ? "[" + arguments + "]" : "." + GenerationNames.EscapeIdentifier(property.Name));
        var accessors = new List<string>();
        if (property.GetMethod is not null) accessors.Add("get => " + receiver + ";");
        if (property.SetMethod is not null) accessors.Add("set => " + receiver + " = value;");
        return $"public {type} {name} {{ {string.Join(" ", accessors)} }}";
    }

    private static string GenerateDelegatingEvent(IEventSymbol @event, string fieldName)
        => $"public event {@event.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {GenerationNames.EscapeIdentifier(@event.Name)}\n{{\n    add => {fieldName}.{GenerationNames.EscapeIdentifier(@event.Name)} += value;\n    remove => {fieldName}.{GenerationNames.EscapeIdentifier(@event.Name)} -= value;\n}}";

    private static string GenerateDelegatingMethod(MethodDeclarationSyntax method, string fieldName)
    {
        var call = fieldName + "." + method.Identifier.ValueText + "("
            + string.Join(", ", method.ParameterList.Parameters.Select(MemberFormat.FormatParameterArgument)) + ")";
        var body = MemberFormat.IsVoid(method.ReturnType) ? $"    {call};" : $"    return {call};";
        var constraints = method.ConstraintClauses.Count == 0
            ? ""
            : " " + string.Join(" ", method.ConstraintClauses.Select(clause => clause.ToString()));
        return $"public {method.ReturnType} {method.Identifier}({MemberFormat.FormatParameters(method.ParameterList)}){constraints}\n{{\n{body}\n}}";
    }

    private static string GenerateDelegatingProperty(PropertyDeclarationSyntax property, string fieldName)
    {
        var accessors = property.AccessorList!.Accessors
            .Where(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration)
                || accessor.IsKind(SyntaxKind.SetAccessorDeclaration))
            .Select(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration)
                ? $"get => {fieldName}.{property.Identifier.ValueText};"
                : $"set => {fieldName}.{property.Identifier.ValueText} = value;")
            .ToList();
        return $"public {property.Type} {property.Identifier} {{ {string.Join(" ", accessors)} }}";
    }

    private static string GenerateDelegatingEvent(TypeSyntax eventType, string name, string fieldName)
        => $"public event {eventType} {name}\n{{\n    add => {fieldName}.{name} += value;\n    remove => {fieldName}.{name} -= value;\n}}";
}
