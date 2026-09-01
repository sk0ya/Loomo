using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Editor;

/// <summary>
/// LSP semanticTokens が利用できないときのC#意味色付けを、RoslynのSemanticModelから作る。
/// 字句解析の代替ではなく、解決できた識別子だけを返すため、未完成入力でも推測で全体を
/// 上書きしない。描画・LSPの契約はEditor.Coreに留め、Roslyn依存はこのDLLから出さない。
/// </summary>
public static class CSharpSemanticTokenService
{
    public static Task<IReadOnlyList<SemanticToken>> GetAsync(
        SolutionModel? solution,
        string filePath,
        string source,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? openTexts = null)
        => Task.Run(() => Get(solution, filePath, source, cancellationToken, openTexts), cancellationToken);

    public static IReadOnlyList<SemanticToken> Get(
        SolutionModel? solution,
        string filePath,
        string source,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? openTexts = null)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(source))
            return [];

        var context = CSharpWorkspaceOperationContext.Create(
            solution, filePath, source,
            // 色付けはアクティブ文書の型解決に必要な参照グラフだけで足りる。
            // solution全体を読むと、大規模solutionで入力ごとのRoslyn負荷が不必要に増える。
            scope: CSharpWorkspaceSourceScope.ProjectGraph,
            includeSemanticCompilation: true,
            openTexts: openTexts);
        if (context.SemanticCompilation is not { } compilation)
            return [];

        var fullPath = Path.GetFullPath(filePath);
        var tree = compilation.SyntaxTrees.FirstOrDefault(candidate =>
            string.Equals(Path.GetFullPath(candidate.FilePath ?? string.Empty), fullPath,
                StringComparison.OrdinalIgnoreCase));
        if (tree is null) return [];

        var model = compilation.GetSemanticModel(tree, ignoreAccessibility: false);
        var text = tree.GetText(cancellationToken);
        var root = tree.GetRoot(cancellationToken);
        var tokens = new List<SemanticToken>();
        foreach (var token in root.DescendantTokens(descendIntoTrivia: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken))
                continue;

            var semanticToken = CreateToken(model, token, text, cancellationToken);
            if (semanticToken is not null)
                tokens.Add(semanticToken);
        }

        return tokens;
    }

    private static SemanticToken? CreateToken(
        SemanticModel model,
        SyntaxToken token,
        SourceText text,
        CancellationToken cancellationToken)
    {
        if (token.Parent is null) return null;

        // Attribute names resolve to a constructor in SymbolInfo; preserve the more useful
        // attribute category before asking for the ordinary symbol below. Restrict this to the
        // attribute name itself: named arguments such as DiagnosticId are ordinary symbols and
        // must retain their property/field classification.
        if (IsAttributeNameToken(token))
            return MakeToken(token, text, "attribute", null);

        var symbol = GetSymbol(model, token, cancellationToken);
        if (symbol is IAliasSymbol alias)
            symbol = alias.Target;
        if (symbol is null) return null;

        // event fieldのVariableDeclaratorはRoslyn上でIFieldSymbolになる実装があるため、
        // symbolの種類だけでは通常のfieldと区別できない。宣言構文との組み合わせで
        // eventだけを分類し、同じeventの参照側の解決結果は通常どおりsymbolへ任せる。
        var tokenType = IsEventFieldDeclaration(token) && symbol is IFieldSymbol
            ? "event"
            : MapTokenType(symbol);
        if (tokenType is null) return null;
        return MakeToken(token, text, tokenType, GetModifiers(symbol, token));
    }

    private static bool IsAttributeNameToken(SyntaxToken token)
        => token.Parent?.AncestorsAndSelf().OfType<AttributeSyntax>().Any(attribute =>
            attribute.Name.Span.Contains(token.Span)) == true;

    private static ISymbol? GetSymbol(
        SemanticModel model,
        SyntaxToken token,
        CancellationToken cancellationToken)
    {
        var parent = token.Parent!;
        ISymbol? declared = parent switch
        {
            BaseTypeDeclarationSyntax node => model.GetDeclaredSymbol(node, cancellationToken),
            DelegateDeclarationSyntax node => model.GetDeclaredSymbol(node, cancellationToken),
            MethodDeclarationSyntax node => model.GetDeclaredSymbol(node, cancellationToken),
            ConstructorDeclarationSyntax node => model.GetDeclaredSymbol(node, cancellationToken),
            DestructorDeclarationSyntax node => model.GetDeclaredSymbol(node, cancellationToken),
            PropertyDeclarationSyntax node => model.GetDeclaredSymbol(node, cancellationToken),
            EventDeclarationSyntax node => model.GetDeclaredSymbol(node, cancellationToken),
            EventFieldDeclarationSyntax node => null,
            VariableDeclaratorSyntax node => model.GetDeclaredSymbol(node, cancellationToken),
            ParameterSyntax node => model.GetDeclaredSymbol(node, cancellationToken),
            TypeParameterSyntax node => model.GetDeclaredSymbol(node, cancellationToken),
            EnumMemberDeclarationSyntax node => model.GetDeclaredSymbol(node, cancellationToken),
            LocalFunctionStatementSyntax node => model.GetDeclaredSymbol(node, cancellationToken),
            NamespaceDeclarationSyntax node => model.GetDeclaredSymbol(node, cancellationToken),
            FileScopedNamespaceDeclarationSyntax node => model.GetDeclaredSymbol(node, cancellationToken),
            _ => null,
        };
        if (declared is not null) return declared;

        // パターン変数、foreach／catchの変数、分解代入のdesignationなどは、
        // 個別の構文型を列挙しなくてもRoslynの宣言APIで解決できる。ここを
        // SymbolInfoだけに任せると、未保存本文で有効なローカル宣言が色付けから
        // 抜け落ちるため、既知の宣言ノードのfallbackとして問い合わせる。
        declared = model.GetDeclaredSymbol(parent, cancellationToken);
        if (declared is not null) return declared;

        var symbolInfo = model.GetSymbolInfo(parent, cancellationToken);
        return symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
    }

    private static string? MapTokenType(ISymbol symbol)
        => symbol switch
        {
            INamespaceSymbol => "namespace",
            INamedTypeSymbol namedType => namedType.TypeKind switch
            {
                TypeKind.Class => "class",
                TypeKind.Struct => "struct",
                TypeKind.Interface => "interface",
                TypeKind.Enum => "enum",
                _ => "type",
            },
            IMethodSymbol => "method",
            IPropertySymbol => "property",
            IEventSymbol => "event",
            IFieldSymbol field when field.ContainingType?.TypeKind == TypeKind.Enum => "enumMember",
            IFieldSymbol => "variable",
            IParameterSymbol => "parameter",
            ITypeParameterSymbol => "typeParameter",
            ILocalSymbol or IRangeVariableSymbol => "variable",
            _ => null,
        };

    private static bool IsEventFieldDeclaration(SyntaxToken token)
        => token.Parent?.AncestorsAndSelf().OfType<EventFieldDeclarationSyntax>().Any() == true;

    private static string[] GetModifiers(ISymbol symbol, SyntaxToken token)
    {
        var modifiers = new List<string>(4);
        if (IsDeclarationIdentifier(token)) modifiers.Add("declaration");
        if (symbol.IsStatic) modifiers.Add("static");
        if (symbol.IsAbstract) modifiers.Add("abstract");
        if (symbol is IFieldSymbol field && (field.IsReadOnly || field.IsConst) ||
            symbol is IMethodSymbol { IsReadOnly: true } ||
            symbol is IPropertySymbol { IsReadOnly: true } ||
            symbol is ITypeSymbol { IsReadOnly: true })
            modifiers.Add("readonly");
        if (symbol is IMethodSymbol { IsAsync: true }) modifiers.Add("async");
        if (IsReassigned(token)) modifiers.Add("ReassignedVariable");
        if (symbol.GetAttributes().Any(attribute =>
                string.Equals(attribute.AttributeClass?.ToDisplayString(),
                    "System.ObsoleteAttribute", StringComparison.Ordinal)))
            modifiers.Add("deprecated");
        if (IsDefaultLibrarySymbol(symbol)) modifiers.Add("defaultLibrary");
        return modifiers.ToArray();
    }

    /// <summary>Roslynの <c>defaultLibrary</c> に合わせ、ソース宣言を持たない参照記号へ
    /// modifierを付ける。Locationsが空の特殊記号は既定ライブラリとは断定しない。</summary>
    private static bool IsDefaultLibrarySymbol(ISymbol symbol)
        => symbol.Locations.Length > 0 && symbol.Locations.All(location => !location.IsInSource);

    /// <summary>Roslyn Language Serverの <c>ReassignedVariable</c> に合わせ、代入・増減・ref/out
    /// の受け手だけへmodifierを付ける。宣言の初期化子は再代入ではないため対象外にする。</summary>
    private static bool IsReassigned(SyntaxToken token)
    {
        var parent = token.Parent;
        if (parent is null) return false;

        // 分解宣言やpattern／foreach／catchの宣言も構文上はassignmentの左辺に
        // 現れることがあるが、宣言時の初期化は再代入ではない。通常のlocal／
        // parameter宣言も含め、宣言識別子にはReassignedVariableを付けない。
        if (IsDeclarationIdentifier(token)) return false;

        if (parent.AncestorsAndSelf().OfType<AssignmentExpressionSyntax>()
            .Any(assignment => IsWriteTarget(assignment.Left, token)))
            return true;
        if (parent.AncestorsAndSelf().OfType<PrefixUnaryExpressionSyntax>()
            .Any(unary => (unary.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreIncrementExpression) ||
                           unary.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreDecrementExpression)) &&
                          IsWriteTarget(unary.Operand, token)))
            return true;
        if (parent.AncestorsAndSelf().OfType<PostfixUnaryExpressionSyntax>()
            .Any(unary => (unary.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PostIncrementExpression) ||
                           unary.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PostDecrementExpression)) &&
                          IsWriteTarget(unary.Operand, token)))
            return true;
        return parent.AncestorsAndSelf().OfType<ArgumentSyntax>()
            .Any(argument =>
                (argument.RefKindKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.RefKeyword) ||
                 argument.RefKindKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.OutKeyword)) &&
                          argument.Expression is { } expression && IsWriteTarget(expression, token));
    }

    private static bool IsDeclarationIdentifier(SyntaxToken token)
    {
        var position = token.SpanStart;
        return token.Parent?.AncestorsAndSelf().Any(node => node switch
        {
            VariableDeclaratorSyntax declarator => declarator.Identifier.SpanStart == position,
            ParameterSyntax parameter => parameter.Identifier.SpanStart == position,
            TypeParameterSyntax typeParameter => typeParameter.Identifier.SpanStart == position,
            BaseTypeDeclarationSyntax type => type.Identifier.SpanStart == position,
            DelegateDeclarationSyntax @delegate => @delegate.Identifier.SpanStart == position,
            MethodDeclarationSyntax method => method.Identifier.SpanStart == position,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.SpanStart == position,
            DestructorDeclarationSyntax destructor => destructor.Identifier.SpanStart == position,
            PropertyDeclarationSyntax property => property.Identifier.SpanStart == position,
            EventDeclarationSyntax @event => @event.Identifier.SpanStart == position,
            EnumMemberDeclarationSyntax member => member.Identifier.SpanStart == position,
            LocalFunctionStatementSyntax localFunction => localFunction.Identifier.SpanStart == position,
            NamespaceDeclarationSyntax @namespace => @namespace.Name.Span.Contains(position),
            FileScopedNamespaceDeclarationSyntax @namespace => @namespace.Name.Span.Contains(position),
            ForEachStatementSyntax forEach => forEach.Identifier.SpanStart == position,
            CatchDeclarationSyntax @catch => @catch.Identifier.SpanStart == position,
            SingleVariableDesignationSyntax designation => designation.Identifier.SpanStart == position,
            _ => false,
        }) == true;
    }

    /// <summary>複合代入やref/outで、receiver（<c>obj</c>）まで書き換え扱いにしない。
    /// 末尾の識別子、tupleの各要素、親括弧内だけを代入対象として扱う。</summary>
    private static bool IsWriteTarget(ExpressionSyntax expression, SyntaxToken token)
        => expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.SpanStart == token.SpanStart,
            GenericNameSyntax generic => generic.Identifier.SpanStart == token.SpanStart,
            MemberAccessExpressionSyntax member => IsWriteTargetName(member.Name, token),
            MemberBindingExpressionSyntax member => IsWriteTargetName(member.Name, token),
            ParenthesizedExpressionSyntax parenthesized => IsWriteTarget(parenthesized.Expression, token),
            TupleExpressionSyntax tuple => tuple.Arguments.Any(argument =>
                IsWriteTarget(argument.Expression, token)),
            DeclarationExpressionSyntax declaration => IsWriteTargetDesignation(declaration.Designation, token),
            ConditionalExpressionSyntax conditional =>
                IsWriteTarget(conditional.WhenTrue, token) || IsWriteTarget(conditional.WhenFalse, token),
            _ => false,
        };

    private static bool IsWriteTargetName(SimpleNameSyntax name, SyntaxToken token)
        => name switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.SpanStart == token.SpanStart,
            GenericNameSyntax generic => generic.Identifier.SpanStart == token.SpanStart,
            _ => false,
        };

    private static bool IsWriteTargetDesignation(VariableDesignationSyntax designation, SyntaxToken token)
        => designation switch
        {
            SingleVariableDesignationSyntax single =>
                single.Identifier.SpanStart == token.SpanStart,
            ParenthesizedVariableDesignationSyntax parenthesized => parenthesized.Variables.Any(variable =>
                IsWriteTargetDesignation(variable, token)),
            DiscardDesignationSyntax => false,
            _ => false,
        };

    private static SemanticToken? MakeToken(
        SyntaxToken token,
        SourceText text,
        string tokenType,
        string[]? modifiers)
    {
        var lineSpan = text.Lines.GetLinePositionSpan(token.Span);
        if (lineSpan.Start.Line != lineSpan.End.Line || token.Span.Length <= 0)
            return null;
        return new SemanticToken(
            lineSpan.Start.Line,
            lineSpan.Start.Character,
            token.Span.Length,
            tokenType,
            modifiers ?? []);
    }
}
