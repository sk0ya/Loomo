using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.CSharp.Editor;

/// <summary>
/// C#シンボルの同一文書内ハイライトをRoslynのsymbol identityで解決する。
/// LSPが未接続／documentHighlight非対応でも、同名の別シンボルを混ぜずに読み書きを表示する。
/// </summary>
public static class CSharpDocumentHighlightService
{
    /// <summary>Appが利用するRoslyn非公開の入力境界。</summary>
    public static async Task<IReadOnlyList<DocumentHighlight>> FindAsync(
        SolutionModel? solution,
        string filePath,
        string source,
        LspPosition position,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await Task.Run(() => CSharpWorkspaceOperationContext.Create(
            solution, filePath, source,
            scope: CSharpWorkspaceSourceScope.Solution,
            includeSemanticCompilation: true,
            openTexts: openTexts), cancellationToken);
        return context.SemanticCompilation is { } compilation
            ? await FindAsync(filePath, position, compilation, cancellationToken)
            : [];
    }

    /// <summary>既に作成済みCompilationを使う内部／テスト経路。</summary>
    internal static async Task<IReadOnlyList<DocumentHighlight>> FindAsync(
        string filePath,
        LspPosition position,
        CSharpCompilation compilation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(compilation);

        var activePath = Path.GetFullPath(filePath);
        var tree = compilation.SyntaxTrees.FirstOrDefault(candidate =>
            string.Equals(Path.GetFullPath(candidate.FilePath ?? ""), activePath,
                StringComparison.OrdinalIgnoreCase));
        if (tree is null) return [];

        var text = await tree.GetTextAsync(cancellationToken);
        if (!CSharpSemanticSymbolResolver.TryGetOffset(text, position, out var offset))
            return [];

        using var workspace = CSharpSemanticWorkspace.Create(compilation);
        if (!workspace.DocumentIds.TryGetValue(activePath, out var activeDocumentId))
            return [];
        var document = workspace.Solution.GetDocument(activeDocumentId);
        var model = document is null
            ? null
            : await document.GetSemanticModelAsync(cancellationToken);
        var root = document is null
            ? null
            : await document.GetSyntaxRootAsync(cancellationToken);
        if (model is null || root is null) return [];

        var symbol = CSharpSemanticSymbolResolver.FindSymbol(
            model, root, offset, cancellationToken);
        if (symbol is null) return [];

        var highlights = new List<(TextSpan Span, DocumentHighlightKind Kind)>();
        var referenced = await SymbolFinder.FindReferencesAsync(
            symbol, workspace.Solution, cancellationToken: cancellationToken);
        foreach (var referencedSymbol in referenced)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                if (!IsActiveDocument(location.Document, activePath)) continue;
                highlights.Add((location.Location.SourceSpan,
                    IsWrittenTo(root, location.Location.SourceSpan)
                        ? DocumentHighlightKind.Write
                        : DocumentHighlightKind.Read));
            }
        }

        // FindReferencesAsync does not consistently include the declaration.
        foreach (var location in symbol.Locations)
        {
            if (!location.IsInSource || location.SourceTree?.FilePath is null ||
                !string.Equals(Path.GetFullPath(location.SourceTree.FilePath), activePath,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            highlights.Add((location.SourceSpan, DeclarationKind(symbol)));
        }

        return highlights
            .Where(item => item.Span.Length > 0 && item.Span.Start >= 0 && item.Span.End <= text.Length)
            .DistinctBy(item => item.Span)
            .OrderBy(item => item.Span.Start)
            .Select(item => new DocumentHighlight(ToLspRange(text, item.Span), item.Kind))
            .ToArray();
    }

    private static bool IsActiveDocument(Document document, string activePath)
        => document.FilePath is { } path &&
           string.Equals(Path.GetFullPath(path), activePath, StringComparison.OrdinalIgnoreCase);

    private static DocumentHighlightKind DeclarationKind(ISymbol symbol)
        => symbol.Kind is Microsoft.CodeAnalysis.SymbolKind.Field or
            Microsoft.CodeAnalysis.SymbolKind.Property or
            Microsoft.CodeAnalysis.SymbolKind.Event or
            Microsoft.CodeAnalysis.SymbolKind.Local or
            Microsoft.CodeAnalysis.SymbolKind.Parameter or
            Microsoft.CodeAnalysis.SymbolKind.RangeVariable
            ? DocumentHighlightKind.Write
            : DocumentHighlightKind.Text;

    private static bool IsWrittenTo(SyntaxNode root, TextSpan span)
    {
        var node = root.FindNode(span, getInnermostNodeForTie: true);
        foreach (var ancestor in node.AncestorsAndSelf())
        {
            if (ancestor is AssignmentExpressionSyntax assignment &&
                assignment.Left.Span.Contains(span.Start))
                return true;
            if (ancestor is PrefixUnaryExpressionSyntax prefix &&
                (prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
                 prefix.IsKind(SyntaxKind.PreDecrementExpression)))
                return true;
            if (ancestor is PostfixUnaryExpressionSyntax postfix &&
                (postfix.IsKind(SyntaxKind.PostIncrementExpression) ||
                 postfix.IsKind(SyntaxKind.PostDecrementExpression)))
                return true;
            if (ancestor is ArgumentSyntax argument &&
                (argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) ||
                 argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)))
                return true;
        }
        return false;
    }

    private static LspRange ToLspRange(SourceText text, TextSpan span)
    {
        var start = text.Lines.GetLinePosition(span.Start);
        var end = text.Lines.GetLinePosition(span.End);
        return new(new(start.Line, start.Character), new(end.Line, end.Character));
    }
}
