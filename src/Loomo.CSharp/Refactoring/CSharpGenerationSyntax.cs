using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>コード生成器が共有する構文・シンボルの探索。「どこに何が既にあるか」「基底/
/// interfaceはどこで宣言されているか」「生成本文をどこへ挿入するか」だけを扱い、
/// 生成する文字列そのものは持たない（そちらは各生成器と <see cref="MemberFormat"/>）。</summary>
internal static class GenerationSyntax
{
    internal static IReadOnlyList<SyntaxNode> ParseWorkspaceRoots(
        string activeFilePath,
        SyntaxNode activeRoot,
        IReadOnlyDictionary<string, string>? workspaceTexts,
        CSharpParseOptions parseOptions,
        IReadOnlyDictionary<string, CSharpParseOptions>? workspaceParseOptions)
    {
        if (workspaceTexts is null || workspaceTexts.Count == 0) return [activeRoot];

        var roots = new List<SyntaxNode> { activeRoot };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(activeFilePath),
        };
        foreach (var (path, source) in workspaceTexts)
        {
            if (!string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (!seen.Add(Path.GetFullPath(path))) continue;
                var sourceParseOptions = workspaceParseOptions is not null &&
                    workspaceParseOptions.TryGetValue(path, out var configured)
                    ? configured : parseOptions;
                roots.Add(CSharpSyntaxTree.ParseText(source, sourceParseOptions).GetRoot());
            }
            catch (ArgumentException)
            {
                // 読み取り対象の1ファイルが不正でも、他のソースから生成候補を探し続ける。
            }
        }
        return roots;
    }

    internal static IEnumerable<T> FindRelatedTypes<T>(
        TypeDeclarationSyntax target,
        IEnumerable<SyntaxNode> roots,
        string name)
        where T : TypeDeclarationSyntax
    {
        var candidates = roots.SelectMany(root => root.DescendantNodes().OfType<T>())
            .Where(candidate => string.Equals(candidate.Identifier.ValueText, name, StringComparison.Ordinal))
            .ToList();
        if (candidates.Count <= 1) return candidates;

        var targetNamespace = NamespaceName(target);
        var sameNamespace = candidates.Where(candidate =>
            string.Equals(NamespaceName(candidate), targetNamespace, StringComparison.Ordinal)).ToList();
        return sameNamespace.Count > 0 ? sameNamespace : candidates;
    }

    /// <summary>interfaceの継承階層を構文だけで辿る。Roslynの意味モデルがないfallbackでも、
    /// 子interfaceだけを実装した型へ親interfaceのメンバーを欠落させない。</summary>
    internal static IEnumerable<InterfaceDeclarationSyntax> FindInterfaceHierarchy(
        TypeDeclarationSyntax target,
        IReadOnlyList<SyntaxNode> roots,
        IEnumerable<string> initialNames)
    {
        var pending = new Queue<string>(initialNames.Where(name => name.Length > 0));
        var visitedNames = new HashSet<string>(StringComparer.Ordinal);
        var visitedContracts = new HashSet<InterfaceDeclarationSyntax>();
        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            if (!visitedNames.Add(name)) continue;
            foreach (var contract in FindRelatedTypes<InterfaceDeclarationSyntax>(target, roots, name))
            {
                if (!visitedContracts.Add(contract)) continue;
                yield return contract;
                foreach (var baseType in contract.BaseList?.Types ?? [])
                    pending.Enqueue(BaseTypeName(baseType.Type));
            }
        }
    }

    /// <summary>意味モデルで解決したinterfaceだけを、元ソースの宣言へ戻す。
    /// 名前だけの検索では、別namespaceに同名interfaceがあると両方を生成してしまう。</summary>
    internal static IEnumerable<InterfaceDeclarationSyntax> FindSemanticInterfaceHierarchy(
        TypeDeclarationSyntax target, SemanticModel semanticModel)
    {
        var semanticTarget = FindEquivalentType(target, semanticModel);
        if (semanticTarget is null) yield break;
        if (semanticModel.GetDeclaredSymbol(semanticTarget) is not INamedTypeSymbol symbol) yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in symbol.AllInterfaces)
        {
            var key = contract.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (!seen.Add(key)) continue;
            foreach (var syntax in SourceDeclarations<InterfaceDeclarationSyntax>(contract))
                yield return syntax;
        }
    }

    /// <summary>フィールドの静的型からinterface階層を解決する。aliasや完全修飾名を
    /// BaseTypeNameへ潰さず、Roslynのシンボルidentityを保つ。</summary>
    internal static IEnumerable<InterfaceDeclarationSyntax> FindSemanticFieldInterfaceHierarchy(
        FieldDeclarationSyntax field, SemanticModel semanticModel)
    {
        var semanticField = semanticModel.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.SpanStart == field.SpanStart);
        var semanticVariable = semanticField?.Declaration.Variables.FirstOrDefault(variable =>
            string.Equals(variable.Identifier.ValueText,
                field.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText,
                StringComparison.Ordinal));
        if (semanticVariable is null || semanticModel.GetDeclaredSymbol(semanticVariable) is not IFieldSymbol fieldSymbol)
            yield break;
        if (fieldSymbol.Type is not INamedTypeSymbol fieldType) yield break;

        var contracts = new[] { fieldType }
            .Concat(fieldType.AllInterfaces)
            .Where(contract => contract.TypeKind == TypeKind.Interface)
            .GroupBy(contract => contract.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                StringComparer.Ordinal)
            .Select(group => group.First());
        foreach (var contract in contracts)
            foreach (var syntax in SourceDeclarations<InterfaceDeclarationSyntax>(contract))
                yield return syntax;
    }

    /// <summary>意味モデルで解決した直接の基底class宣言を取得する。</summary>
    internal static IEnumerable<ClassDeclarationSyntax> FindSemanticBaseDeclarations(
        TypeDeclarationSyntax target, SemanticModel semanticModel)
    {
        var semanticTarget = FindEquivalentType(target, semanticModel);
        if (semanticTarget is null) yield break;
        if (semanticModel.GetDeclaredSymbol(semanticTarget) is not INamedTypeSymbol symbol ||
            symbol.BaseType is not { } baseType)
            yield break;
        foreach (var syntax in SourceDeclarations<ClassDeclarationSyntax>(baseType))
            yield return syntax;
    }

    internal static TypeDeclarationSyntax? FindEquivalentType(
        TypeDeclarationSyntax target, SemanticModel semanticModel)
        => semanticModel.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.SpanStart == target.SpanStart &&
                candidate.RawKind == target.RawKind &&
                string.Equals(candidate.Identifier.ValueText, target.Identifier.ValueText,
                    StringComparison.Ordinal));

    internal static VariableDeclaratorSyntax? FindEquivalentField(
        GeneratedFieldInfo field, SemanticModel semanticModel)
        => field.Declarator is null ? null : semanticModel.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(candidate => candidate.SpanStart == field.Declarator.SpanStart &&
                string.Equals(candidate.Identifier.ValueText, field.Identifier.ValueText,
                    StringComparison.Ordinal));

    internal static BaseMethodDeclarationSyntax? FindEquivalentMethod(
        BaseMethodDeclarationSyntax target, SemanticModel semanticModel)
        => semanticModel.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<BaseMethodDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.SpanStart == target.SpanStart &&
                candidate.RawKind == target.RawKind);

    internal static IEnumerable<TSyntax> SourceDeclarations<TSyntax>(INamedTypeSymbol symbol)
        where TSyntax : SyntaxNode
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            SyntaxNode? syntax;
            try { syntax = reference.GetSyntax(); }
            catch (InvalidOperationException) { continue; }
            if (syntax is TSyntax typed) yield return typed;
        }
    }

    private static string NamespaceName(TypeDeclarationSyntax type)
        => string.Join(".", type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse().Select(namespaceNode => namespaceNode.Name.ToString()));

    internal static IEnumerable<GeneratedFieldInfo> InstanceFields(TypeDeclarationSyntax type)
    {
        foreach (var declaration in type.Members.OfType<FieldDeclarationSyntax>())
        {
            if (declaration.Modifiers.Any(m =>
                    m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ConstKeyword)))
                continue;
            foreach (var variable in declaration.Declaration.Variables)
                yield return new GeneratedFieldInfo(
                    declaration.Declaration.Type, variable.Identifier, declaration.Modifiers, variable);
        }
    }

    internal static IEnumerable<PropertyDeclarationSyntax> InstanceAutoProperties(
        TypeDeclarationSyntax type)
    {
        foreach (var property in type.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (property.Modifiers.Any(modifier =>
                    modifier.IsKind(SyntaxKind.StaticKeyword) ||
                    modifier.IsKind(SyntaxKind.AbstractKeyword)))
                continue;
            if (property.AccessorList is not { } accessors || property.ExpressionBody is not null ||
                property.Initializer is not null ||
                accessors.Accessors.Any(accessor => accessor.Body is not null ||
                    accessor.ExpressionBody is not null))
                continue;
            if (!accessors.Accessors.Any(accessor =>
                    accessor.IsKind(SyntaxKind.GetAccessorDeclaration) ||
                    accessor.IsKind(SyntaxKind.InitAccessorDeclaration) ||
                    accessor.IsKind(SyntaxKind.SetAccessorDeclaration)))
                continue;
            yield return property;
        }
    }

    internal static IEnumerable<PropertyDeclarationSyntax> InstanceReadableAutoProperties(
        TypeDeclarationSyntax type)
        => InstanceAutoProperties(type).Where(property =>
            property.AccessorList!.Accessors.Any(accessor =>
                accessor.IsKind(SyntaxKind.GetAccessorDeclaration) ||
                accessor.IsKind(SyntaxKind.InitAccessorDeclaration)));

    /// <summary>式本体を含む、値を読み取れるinstance propertyを返す。
    /// setter-only propertyをDeconstructの出力へ混ぜない。</summary>
    internal static IEnumerable<PropertyDeclarationSyntax> InstanceReadableProperties(
        TypeDeclarationSyntax type)
    {
        foreach (var property in type.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (property.Modifiers.Any(modifier =>
                    modifier.IsKind(SyntaxKind.StaticKeyword) ||
                    modifier.IsKind(SyntaxKind.AbstractKeyword)))
                continue;
            if (property.ExpressionBody is not null)
            {
                yield return property;
                continue;
            }
            if (property.AccessorList?.Accessors.Any(accessor =>
                    accessor.IsKind(SyntaxKind.GetAccessorDeclaration) ||
                    accessor.IsKind(SyntaxKind.InitAccessorDeclaration)) == true)
                yield return property;
        }
    }

    internal static bool IsConstructorProperty(PropertyDeclarationSyntax property)
        => !property.Modifiers.Any(modifier =>
                modifier.IsKind(SyntaxKind.StaticKeyword) ||
                modifier.IsKind(SyntaxKind.AbstractKeyword)) &&
            property.AccessorList is { } accessors && property.ExpressionBody is null &&
            property.Initializer is null &&
            accessors.Accessors.All(accessor => accessor.Body is null && accessor.ExpressionBody is null) &&
            accessors.Accessors.Any(accessor =>
                accessor.IsKind(SyntaxKind.GetAccessorDeclaration) ||
                accessor.IsKind(SyntaxKind.InitAccessorDeclaration) ||
                accessor.IsKind(SyntaxKind.SetAccessorDeclaration));

    internal static bool IsReadableProperty(PropertyDeclarationSyntax property, bool autoOnly)
    {
        if (property.Modifiers.Any(modifier =>
                modifier.IsKind(SyntaxKind.StaticKeyword) ||
                modifier.IsKind(SyntaxKind.AbstractKeyword)))
            return false;
        if (autoOnly)
            return IsConstructorProperty(property) &&
                property.AccessorList!.Accessors.Any(accessor =>
                    accessor.IsKind(SyntaxKind.GetAccessorDeclaration) ||
                    accessor.IsKind(SyntaxKind.InitAccessorDeclaration));
        return property.ExpressionBody is not null ||
            property.AccessorList?.Accessors.Any(accessor =>
                accessor.IsKind(SyntaxKind.GetAccessorDeclaration) ||
                accessor.IsKind(SyntaxKind.InitAccessorDeclaration)) == true;
    }

    internal static PropertyDeclarationSyntax? GetPropertyDeclaration(IPropertySymbol property)
    {
        foreach (var reference in property.DeclaringSyntaxReferences)
        {
            try
            {
                if (reference.GetSyntax() is PropertyDeclarationSyntax syntax)
                    return syntax;
            }
            catch (InvalidOperationException) { }
        }
        return null;
    }

    internal static IEnumerable<GeneratedFieldInfo> GetSemanticPartialFields(
        INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
    {
        var activeTree = semanticModel.SyntaxTree;
        foreach (var field in typeSymbol.GetMembers().OfType<IFieldSymbol>()
                     .Where(field => !field.IsImplicitlyDeclared && !field.IsStatic && !field.IsConst &&
                         !field.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == activeTree)))
        {
            var modifiers = field.IsReadOnly
                ? SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword))
                : default;
            yield return new GeneratedFieldInfo(
                SyntaxFactory.ParseTypeName(MemberFormat.DisplayGeneratedType(field.Type)),
                GenerationNames.IdentifierToken(field.Name), modifiers, null, field);
        }
    }

    internal static IEnumerable<ISymbol> DeclaredMemberSymbols(
        MemberDeclarationSyntax member, SemanticModel semanticModel, string? memberName = null)
    {
        SemanticModel model;
        try
        {
            model = semanticModel.Compilation.GetSemanticModel(member.SyntaxTree);
        }
        catch (ArgumentException)
        {
            yield break;
        }

        switch (member)
        {
            case MethodDeclarationSyntax method when model.GetDeclaredSymbol(method) is { } methodSymbol:
                yield return methodSymbol;
                break;
            case PropertyDeclarationSyntax property when model.GetDeclaredSymbol(property) is { } propertySymbol:
                yield return propertySymbol;
                break;
            case EventDeclarationSyntax @event when model.GetDeclaredSymbol(@event) is { } eventSymbol:
                yield return eventSymbol;
                break;
            case EventFieldDeclarationSyntax eventFields:
                foreach (var variable in eventFields.Declaration.Variables)
                {
                    if (memberName is not null &&
                        !string.Equals(variable.Identifier.ValueText, memberName, StringComparison.Ordinal))
                        continue;
                    if (model.GetDeclaredSymbol(variable) is { } variableSymbol)
                        yield return variableSymbol;
                }
                break;
        }
    }

    internal static bool IsAbstractInterfaceMember(ISymbol member)
        => member switch
        {
            // Interface members in metadata do not consistently expose IsAbstract across
            // compiler/runtime versions. Instance members without a default body are the
            // contract; static members are intentionally excluded from this MVP generator.
            IMethodSymbol method => method.MethodKind == MethodKind.Ordinary && !method.IsStatic,
            IPropertySymbol property => !property.IsStatic,
            IEventSymbol @event => !@event.IsStatic,
            _ => false,
        };

    internal static bool HasMethod(TypeDeclarationSyntax type, MethodDeclarationSyntax candidate)
        => type.Members.OfType<MethodDeclarationSyntax>().Any(existing =>
            string.Equals(existing.Identifier.ValueText, candidate.Identifier.ValueText, StringComparison.Ordinal)
            && existing.ParameterList.Parameters.Count == candidate.ParameterList.Parameters.Count
            && string.Equals(ParameterShape(existing.ParameterList), ParameterShape(candidate.ParameterList), StringComparison.Ordinal));

    internal static bool HasProperty(TypeDeclarationSyntax type, string name)
        => type.Members.OfType<PropertyDeclarationSyntax>().Any(p =>
            string.Equals(p.Identifier.ValueText, name, StringComparison.Ordinal));

    internal static bool HasEvent(TypeDeclarationSyntax type, string name)
        => type.Members.OfType<EventDeclarationSyntax>().Any(e =>
            string.Equals(e.Identifier.ValueText, name, StringComparison.Ordinal))
            || type.Members.OfType<EventFieldDeclarationSyntax>().SelectMany(e => e.Declaration.Variables)
                .Any(v => string.Equals(v.Identifier.ValueText, name, StringComparison.Ordinal));

    internal static string MethodKey(string name, ParameterListSyntax parameters)
        => "method:" + name + "/" + ParameterShape(parameters);

    private static string ParameterShape(ParameterListSyntax parameters)
        => string.Join(",", parameters.Parameters.Select(parameter =>
            $"{ParameterModifier(parameter)}:{parameter.Type?.ToString() ?? "object"}"));

    private static string ParameterModifier(ParameterSyntax parameter)
        => parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)) ? "ref"
            : parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.OutKeyword)) ? "out"
            : parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword)) ? "in"
            : parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword)) ? "params"
            : "";

    internal static string AccessModifier(SyntaxTokenList modifiers)
        => modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword))
            ? modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)) ? "protected internal" : "protected"
            : modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)) ? "internal" : "public";

    internal static string BaseTypeName(TypeSyntax type)
    {
        var text = type.ToString().TrimEnd('?');
        var lastDot = text.LastIndexOf('.');
        if (lastDot >= 0) text = text[(lastDot + 1)..];
        var generic = text.IndexOf('<');
        return generic >= 0 ? text[..generic] : text;
    }

    internal static bool IsClassOrStruct(TypeDeclarationSyntax type)
        => type is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax;

    internal static LspWorkspaceEdit? InsertBeforeCloseBrace(
        string filePath,
        SourceText source,
        TypeDeclarationSyntax type,
        string generated)
    {
        var close = type.CloseBraceToken;
        if (close.IsMissing) return null;
        var line = source.Lines.GetLineFromPosition(close.SpanStart);
        var closeIndent = source.ToString(TextSpan.FromBounds(line.Start, close.SpanStart));
        if (closeIndent.Any(c => !char.IsWhiteSpace(c))) return null;

        var newline = source.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var memberIndent = FindMemberIndent(source, type, closeIndent);
        // 生成本文の内部インデントは4空白基準。先頭行だけ型のメンバー字下げを足し、
        // それ以降はブロック階層を1段ずつ足す。
        var indented = IndentGeneratedMember(generated, memberIndent, newline);
        var newText = indented + newline;
        var position = new LspPosition(line.LineNumber, 0);
        var range = new LspRange(position, position);
        return new LspWorkspaceEdit(
            new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
            {
                [LspUri.FromPath(Path.GetFullPath(filePath))] = [new LspTextEdit(range, newText)],
            });
    }

    private static string FindMemberIndent(SourceText source, TypeDeclarationSyntax type, string closeIndent)
    {
        var member = type.Members.FirstOrDefault();
        if (member is null) return closeIndent + "    ";
        var line = source.Lines.GetLineFromPosition(member.SpanStart);
        var prefix = source.ToString(TextSpan.FromBounds(line.Start, member.SpanStart));
        return prefix.All(char.IsWhiteSpace) ? prefix : closeIndent + "    ";
    }

    private static string IndentGeneratedMember(string generated, string memberIndent, string newline)
    {
        var lines = generated.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = new List<string>(lines.Length);
        var depth = 0;
        foreach (var line in lines)
        {
            result.Add(memberIndent + new string(' ', depth * 4) + line);
            if (line.Contains('{', StringComparison.Ordinal)) depth++;
            if (line.Contains('}', StringComparison.Ordinal)) depth = Math.Max(0, depth - 1);
        }
        return string.Join(newline, result);
    }

    internal static int ClampToLine(SourceText source, int line, int character)
    {
        var textLine = source.Lines[line];
        return textLine.Start + Math.Clamp(character, 0, textLine.Span.Length);
    }
}

/// <summary>構文フィールドと、partial の別宣言から拾ったシンボルフィールドを同じ形で扱う。</summary>
internal sealed record GeneratedFieldInfo(
    TypeSyntax Type,
    SyntaxToken Identifier,
    SyntaxTokenList Modifiers,
    VariableDeclaratorSyntax? Declarator,
    IFieldSymbol? SemanticSymbol = null);
