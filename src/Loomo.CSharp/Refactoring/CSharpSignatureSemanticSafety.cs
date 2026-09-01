using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>Change Signatureの意味モデル側の安全確認。LSPのreferencesがmethod groupを
/// 返さない実装でも、対象メソッドと同じsymbolへの非呼び出し参照を検出する。</summary>
internal static class CSharpSignatureSemanticSafety
{
    /// <summary>
    /// 同じpartial型の別宣言を含め、変更後にoverloadが衝突しないかを意味モデルで確認する。
    /// 構文だけの検査では別ファイルpartialや型aliasを見落とすため、Compilation上の型identityで比較する。
    /// </summary>
    internal static string? FindSignatureConflict(
        CSharpCompilation compilation, MethodSignature original, SignatureChange change)
    {
        var targetTree = FindTree(compilation, original.FilePath);
        if (targetTree is null) return null;
        var model = compilation.GetSemanticModel(targetTree, ignoreAccessibility: false);
        var declaration = FindDeclaration(targetTree, original.NamePosition);
        var target = GetDeclaredMethod(model, declaration);
        if (target?.ContainingType is null) return null;
        var relatedSymbols = FindRelatedMethods(compilation, target);

        var desired = new List<(ITypeSymbol? Type, RefKind RefKind)>();
        foreach (var changed in change.Parameters)
        {
            var typeSyntax = SyntaxFactory.ParseTypeName(changed.Parameter.Type);
            var type = model.GetSpeculativeTypeInfo(
                declaration!.SpanStart, typeSyntax,
                SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
            if (type is null || type.TypeKind == TypeKind.Error) return null;
            desired.Add((type, RefKindOf(changed.Parameter.Modifiers)));
        }

        foreach (var related in relatedSymbols)
        {
            var candidates = related.ContainingType.GetMembers(related.Name).OfType<IMethodSymbol>()
                .Where(candidate => !relatedSymbols.Any(existing => Same(candidate, existing)));
            foreach (var candidate in candidates)
            {
                if (candidate.Parameters.Length != desired.Count) continue;
                if (candidate.Parameters.Select((parameter, index) =>
                        SymbolEqualityComparer.Default.Equals(parameter.Type, desired[index].Type) &&
                        parameter.RefKind == desired[index].RefKind)
                    .All(match => match))
                    return target.MethodKind == MethodKind.Constructor
                        ? "変更後のコンストラクターがpartial／継承契約を含む型のoverloadと衝突します。"
                        : "変更後のシグネチャがpartial／継承契約を含む型のoverloadと衝突します。";
            }
        }
        return null;
    }

    /// <summary>
    /// Roslynの意味モデルから、対象メソッドへ実際にbindしている呼び出し位置を収集する。
    /// LSPのreferencesが未接続／空応答でも、同じCompilationに含まれるソースについては
    /// 構文上の同名検索より正確にChange Signatureの対象を決められる。
    /// </summary>
    internal static IReadOnlyList<LspLocation>? FindInvocationReferences(
        CSharpCompilation compilation, MethodSignature original)
    {
        var targetTree = FindTree(compilation, original.FilePath);
        if (targetTree is null) return null;

        var targetModel = compilation.GetSemanticModel(targetTree, ignoreAccessibility: false);
        var targetDeclaration = FindDeclaration(targetTree, original.NamePosition);
        var targetSymbol = GetDeclaredMethod(targetModel, targetDeclaration);
        if (targetSymbol is null) return null;
        var relatedSymbols = FindRelatedMethods(compilation, targetSymbol);

        var locations = new List<LspLocation>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree, ignoreAccessibility: false);
            var root = tree.GetRoot();

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (!Matches(model.GetSymbolInfo(invocation), relatedSymbols)) continue;
                var name = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax member => member.Name,
                    MemberBindingExpressionSyntax binding => binding.Name,
                    SimpleNameSyntax simple => simple,
                    _ => null,
                };
                if (name is null) continue;
                locations.Add(ToLocation(tree, name.Span, name.SyntaxTree?.FilePath));
            }

            foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                if (!Matches(model.GetSymbolInfo(creation), relatedSymbols)) continue;
                locations.Add(ToLocation(tree, creation.Type.Span, tree.FilePath));
            }

            foreach (var initializer in root.DescendantNodes().OfType<ConstructorInitializerSyntax>())
            {
                if (!Matches(model.GetSymbolInfo(initializer), relatedSymbols)) continue;
                locations.Add(ToLocation(tree, initializer.ThisOrBaseKeyword.Span, tree.FilePath));
            }
        }

        return locations
            .GroupBy(location => (location.Uri, location.Range.Start.Line,
                location.Range.Start.Character, location.Range.End.Line,
                location.Range.End.Character))
            .Select(group => group.First())
            .ToList();
    }

    /// <summary>
    /// 対象メソッドと同じoverride／interface契約に属するソース宣言の位置を返す。
    /// Change Signatureは呼び出し側だけでなく、基底メソッド・interface・overrideの
    /// すべてを同じWorkspaceEditへ含めないと、変更後に契約が壊れる。
    /// </summary>
    internal static IReadOnlyList<LspLocation>? FindRelatedDeclarationReferences(
        CSharpCompilation compilation, MethodSignature original)
    {
        var targetTree = FindTree(compilation, original.FilePath);
        if (targetTree is null) return null;
        var targetModel = compilation.GetSemanticModel(targetTree, ignoreAccessibility: false);
        var targetDeclaration = FindDeclaration(targetTree, original.NamePosition);
        var targetSymbol = GetDeclaredMethod(targetModel, targetDeclaration);
        if (targetSymbol is null) return null;

        var result = new List<LspLocation>();
        foreach (var symbol in FindRelatedMethods(compilation, targetSymbol))
        {
            foreach (var reference in symbol.DeclaringSyntaxReferences)
            {
                var tree = reference.SyntaxTree;
                var node = reference.GetSyntax() switch
                {
                    MethodDeclarationSyntax method => method.Identifier,
                    ConstructorDeclarationSyntax constructor => constructor.Identifier,
                    LocalFunctionStatementSyntax localFunction => localFunction.Identifier,
                    _ => default,
                };
                if (node == default || node.Span.Length == 0) continue;
                result.Add(ToLocation(tree, node.Span, tree.FilePath));
            }
        }

        return result
            .GroupBy(location => (location.Uri, location.Range.Start.Line,
                location.Range.Start.Character, location.Range.End.Line,
                location.Range.End.Character))
            .Select(group => group.First())
            .ToList();
    }

    internal static string? FindMethodGroupHazard(
        CSharpCompilation compilation, MethodSignature original)
    {
        var targetTree = FindTree(compilation, original.FilePath);
        if (targetTree is null) return null;

        var targetModel = compilation.GetSemanticModel(targetTree, ignoreAccessibility: false);
        var targetDeclaration = FindDeclaration(targetTree, original.NamePosition);
        var targetSymbol = GetDeclaredMethod(targetModel, targetDeclaration);
        if (targetSymbol is null) return null;
        var relatedSymbols = FindRelatedMethods(compilation, targetSymbol);

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree, ignoreAccessibility: false);
            var root = tree.GetRoot();
            foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>()
                         .Where(candidate => candidate.Identifier.ValueText == original.Name))
            {
                var info = model.GetSymbolInfo(name);
                if (!Matches(info, relatedSymbols)) continue;
                if (IsInvocationTarget(name)) continue;

                var line = tree.GetLineSpan(name.Span).StartLinePosition.Line + 1;
                return IsNameOf(name)
                    ? $"{line}行目: nameof で参照されています。手で直してください。"
                    : $"{line}行目: メソッドグループ／メンバー参照を安全に変更できないため中止しました。";
            }
        }

        return null;
    }

    private static SyntaxTree? FindTree(CSharpCompilation compilation, string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        return compilation.SyntaxTrees.FirstOrDefault(tree =>
            string.Equals(Path.GetFullPath(tree.FilePath ?? ""), fullPath,
                StringComparison.OrdinalIgnoreCase));
    }

    private static SyntaxNode? FindDeclaration(SyntaxTree tree, LspPosition position)
    {
        var source = tree.GetText();
        if (source.Lines.Count == 0) return null;
        var line = source.Lines[Math.Clamp(position.Line, 0, source.Lines.Count - 1)];
        var offset = line.Start + Math.Clamp(position.Character, 0, line.End - line.Start);
        return tree.GetRoot().FindToken(offset).Parent?.AncestorsAndSelf()
            .FirstOrDefault(node => node is MethodDeclarationSyntax or
                ConstructorDeclarationSyntax or LocalFunctionStatementSyntax);
    }

    private static IMethodSymbol? GetDeclaredMethod(SemanticModel model, SyntaxNode? declaration)
        => declaration switch
        {
            MethodDeclarationSyntax method => model.GetDeclaredSymbol(method),
            ConstructorDeclarationSyntax constructor => model.GetDeclaredSymbol(constructor),
            LocalFunctionStatementSyntax localFunction => model.GetDeclaredSymbol(localFunction),
            _ => null,
        };

    private static bool Matches(SymbolInfo info, IReadOnlyList<IMethodSymbol> targets)
        => info.Symbol is IMethodSymbol symbol && targets.Any(target => Same(symbol, target))
           || info.CandidateSymbols.OfType<IMethodSymbol>()
               .Any(symbol => targets.Any(target => Same(symbol, target)));

    private static bool Same(IMethodSymbol left, IMethodSymbol right)
        => SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition);

    /// <summary>Compilation内のソースメソッドから、override／interface契約の同値集合を作る。
    /// SymbolFinderを別Workspaceで起動せず、現在の選択TFM Compilationを正本にする。</summary>
    private static IReadOnlyList<IMethodSymbol> FindRelatedMethods(
        CSharpCompilation compilation, IMethodSymbol target)
    {
        var all = compilation.SyntaxTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes()
                .Select(node => node switch
                {
                    MethodDeclarationSyntax method => compilation.GetSemanticModel(tree)
                        .GetDeclaredSymbol(method),
                    ConstructorDeclarationSyntax constructor => compilation.GetSemanticModel(tree)
                        .GetDeclaredSymbol(constructor),
                    LocalFunctionStatementSyntax localFunction => compilation.GetSemanticModel(tree)
                        .GetDeclaredSymbol(localFunction),
                    _ => null,
                }))
            .OfType<IMethodSymbol>()
            .GroupBy(symbol => symbol.OriginalDefinition, SymbolEqualityComparer.Default)
            .Select(group => group.First())
            .ToArray();

        var related = new List<IMethodSymbol> { target };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var candidate in all)
            {
                if (related.Any(existing => Same(existing, candidate))) continue;
                if (!related.Any(existing => AreContractRelated(candidate, existing))) continue;
                related.Add(candidate);
                changed = true;
            }
        }
        return related;
    }

    private static bool AreContractRelated(IMethodSymbol left, IMethodSymbol right)
    {
        if (Same(left, right)) return true;
        if (OverrideChainContains(left, right) || OverrideChainContains(right, left))
            return true;

        if (left.ContainingType.TypeKind == TypeKind.Interface &&
            right.ContainingType.TypeKind != TypeKind.Interface)
            return Implements(right, left);
        if (right.ContainingType.TypeKind == TypeKind.Interface &&
            left.ContainingType.TypeKind != TypeKind.Interface)
            return Implements(left, right);

        // Interface継承の同一メンバーも契約に含める。
        return left.ContainingType.TypeKind == TypeKind.Interface &&
               right.ContainingType.TypeKind == TypeKind.Interface &&
               (IsInterfaceBaseOf(left.ContainingType, right.ContainingType) ||
                IsInterfaceBaseOf(right.ContainingType, left.ContainingType)) &&
               SameContractSignature(left, right);
    }

    private static bool OverrideChainContains(IMethodSymbol candidate, IMethodSymbol target)
    {
        for (var current = candidate.OverriddenMethod; current is not null;
             current = current.OverriddenMethod)
            if (Same(current, target)) return true;
        return false;
    }

    private static bool Implements(IMethodSymbol implementation, IMethodSymbol interfaceMember)
    {
        if (implementation.ContainingType.TypeKind == TypeKind.Interface)
            return false;
        var resolved = implementation.ContainingType.FindImplementationForInterfaceMember(interfaceMember);
        return resolved is IMethodSymbol method && Same(method, implementation);
    }

    private static bool SameContractSignature(IMethodSymbol left, IMethodSymbol right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
            left.Arity != right.Arity || left.Parameters.Length != right.Parameters.Length)
            return false;

        return left.Parameters.Zip(right.Parameters).All(pair =>
            pair.First.RefKind == pair.Second.RefKind &&
            (SymbolEqualityComparer.Default.Equals(pair.First.Type, pair.Second.Type) ||
             string.Equals(
                 pair.First.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                 pair.Second.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                 StringComparison.Ordinal)));
    }

    private static bool IsInterfaceBaseOf(INamedTypeSymbol possibleBase, INamedTypeSymbol possibleDerived)
        => possibleDerived.AllInterfaces.Any(@interface =>
            SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition,
                possibleBase.OriginalDefinition));

    private static RefKind RefKindOf(string modifiers)
        => modifiers.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(modifier => modifier is "ref" or "out" or "in") switch
        {
            "ref" => RefKind.Ref,
            "out" => RefKind.Out,
            "in" => RefKind.In,
            _ => RefKind.None,
        };

    private static LspLocation ToLocation(SyntaxTree tree, Microsoft.CodeAnalysis.Text.TextSpan span,
        string? filePath)
    {
        var source = tree.GetText();
        var lineSpan = source.Lines.GetLinePositionSpan(span);
        return new LspLocation(
            LspUri.FromPath(Path.GetFullPath(filePath ?? tree.FilePath ?? "")),
            new LspRange(
                new LspPosition(lineSpan.Start.Line, lineSpan.Start.Character),
                new LspPosition(lineSpan.End.Line, lineSpan.End.Character)));
    }

    private static bool IsInvocationTarget(SimpleNameSyntax name)
    {
        if (name.Parent is InvocationExpressionSyntax invocation &&
            ReferenceEquals(invocation.Expression, name)) return true;
        if (name.Parent is MemberAccessExpressionSyntax member &&
            ReferenceEquals(member.Name, name) &&
            member.Parent is InvocationExpressionSyntax memberInvocation &&
            ReferenceEquals(memberInvocation.Expression, member)) return true;
        if (name.Parent is MemberBindingExpressionSyntax binding &&
            ReferenceEquals(binding.Name, name) &&
            binding.Parent?.Parent is InvocationExpressionSyntax conditionalInvocation &&
            ReferenceEquals(conditionalInvocation.Expression, binding)) return true;
        return false;
    }

    private static bool IsNameOf(SimpleNameSyntax name)
        => name.AncestorsAndSelf().OfType<InvocationExpressionSyntax>()
            .Any(invocation => invocation.Expression is IdentifierNameSyntax
                { Identifier.ValueText: "nameof" });
}
