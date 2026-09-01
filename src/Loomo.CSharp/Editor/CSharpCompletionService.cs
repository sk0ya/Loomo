using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Xml;
using System.Xml.Linq;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Editor;

/// <summary>LSPが未接続・空応答のときに使うRoslynベースのC#補完。</summary>
public static class CSharpCompletionService
{
    public static async Task<IReadOnlyList<LspCompletionItem>> GetAsync(
        SolutionModel? solution,
        string filePath,
        string source,
        int line,
        int character,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? openTexts = null)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(source))
            return [];

        var context = CSharpWorkspaceOperationContext.Create(
            solution, filePath, source,
            scope: CSharpWorkspaceSourceScope.Solution,
            includeSemanticCompilation: true,
            openTexts: openTexts);
        if (context.SemanticCompilation is not { } compilation)
            return [];

        using var workspace = Refactoring.CSharpSemanticWorkspace.Create(compilation);
        var fullPath = Path.GetFullPath(filePath);
        var tree = compilation.SyntaxTrees.FirstOrDefault(candidate =>
            string.Equals(Path.GetFullPath(candidate.FilePath ?? ""), fullPath,
                StringComparison.OrdinalIgnoreCase));
        if (tree is null) return [];
        if (!workspace.DocumentIds.TryGetValue(fullPath, out var documentId))
            return [];
        var document = workspace.Solution.GetDocument(documentId);
        if (document is null) return [];

        var text = await document.GetTextAsync(cancellationToken);
        var offset = ToOffset(text, line, character);
        var service = CompletionService.GetService(document);
        if (service is null)
            return BuildMemberFallback(compilation, tree, text, offset, cancellationToken);

        var list = await service.GetCompletionsAsync(
            document, offset, CompletionTrigger.Invoke,
            cancellationToken: cancellationToken);
        if (list is null || !list.ItemsList.Any())
            return BuildMemberFallback(compilation, tree, text, offset, cancellationToken);

        var result = new List<LspCompletionItem>();
        foreach (var item in list.ItemsList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var change = await service.GetChangeAsync(document, item, null, cancellationToken);
            var textChange = change.TextChange;
            var range = ToLspRange(text, textChange.Span);
            var insertText = textChange.NewText;
            if (string.IsNullOrEmpty(insertText)) continue;
            var additionalTextEdits = change.TextChanges
                .Where(candidate => candidate.Span != textChange.Span ||
                                    !string.Equals(candidate.NewText, textChange.NewText,
                                        StringComparison.Ordinal))
                .Select(candidate => candidate.NewText is { } newText
                    ? new LspTextEdit(ToLspRange(text, candidate.Span), newText)
                    : null)
                .OfType<LspTextEdit>()
                .ToArray();

            // CompletionItemのInlineDescriptionは短い型情報だけで、XML documentationを
            // 含まないことがある。Editorの候補詳細ペインへRoslynの説明をそのまま渡し、
            // LSPが空応答へfallbackした場合もC#のdocumentation popupを維持する。
            string? documentation = null;
            try
            {
                var description = await service.GetDescriptionAsync(
                    document, item, cancellationToken);
                documentation = string.IsNullOrWhiteSpace(description?.Text)
                    ? null
                    : description?.Text;
            }
            catch (NotSupportedException)
            {
                // 一部のCompletionProviderはdescriptionを遅延生成できない。
            }
            catch (InvalidOperationException)
            {
                // 解析中の不完全文書では候補自体を失わず、説明だけ省略する。
            }

            result.Add(new LspCompletionItem(
                item.DisplayText,
                MapKind(item.Tags),
                item.InlineDescription,
                insertText,
                item.FilterText,
                documentation,
                InsertTextFormat.PlainText,
                item.SortText,
                false,
                new LspTextEdit(range, insertText)
#if LOOMO_EDITOR_COMPLETION_ADDITIONAL_EDITS
                ,
                AdditionalTextEdits: additionalTextEdits.Length == 0
                    ? null : additionalTextEdits));
#else
                ));
#endif
        }

        return result.Count > 0
            ? result
            .GroupBy(item => (item.Label, item.TextEdit?.Range.Start.Line,
                item.TextEdit?.Range.Start.Character, item.InsertText))
            .Select(group => group.First())
            .ToArray()
            : BuildMemberFallback(compilation, tree, text, offset, cancellationToken);
    }

    private static IReadOnlyList<LspCompletionItem> BuildMemberFallback(
        CSharpCompilation compilation,
        SyntaxTree tree,
        SourceText text,
        int offset,
        CancellationToken cancellationToken)
    {
        var root = tree.GetRoot(cancellationToken);
        var memberAccess = root.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(node => node.OperatorToken.Span.End <= offset &&
                           offset <= node.Name.Span.End)
            .OrderBy(node => node.Span.Length)
            .FirstOrDefault();
        if (memberAccess is null)
            return BuildScopeFallback(compilation, tree, text, offset, cancellationToken);

        var model = compilation.GetSemanticModel(tree, ignoreAccessibility: false);
        var receiverType = model.GetTypeInfo(memberAccess.Expression, cancellationToken).Type;
        var receiverMembers = receiverType is INamedTypeSymbol namedType
            ? EnumerateTypeMembers(namedType)
            : model.GetSymbolInfo(memberAccess.Expression, cancellationToken).Symbol
                is INamespaceSymbol @namespace
                    ? @namespace.GetMembers()
                    : [];
        if (!receiverMembers.Any()) return [];

        var nameStart = memberAccess.Name.SpanStart;
        var nameEnd = Math.Min(offset, memberAccess.Name.Span.End);
        var prefix = nameEnd > nameStart
            ? text.ToString(new TextSpan(nameStart, nameEnd - nameStart))
            : string.Empty;
        var range = ToLspRange(text, new TextSpan(nameStart, Math.Max(0, nameEnd - nameStart)));
        return receiverMembers
            .Where(member => member is not IMethodSymbol method ||
                             method.MethodKind != MethodKind.Constructor)
            .Where(member => string.IsNullOrEmpty(prefix) ||
                             member.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(member => member.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
            .Select(member => new LspCompletionItem(
                member.Name,
                MapSymbolKind(member),
                member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                member.Name,
                member.Name,
                GetDocumentation(member),
                InsertTextFormat.PlainText,
                member.Name,
                false,
                new LspTextEdit(range, member.Name)))
            .ToArray();
    }

    private static IReadOnlyList<LspCompletionItem> BuildScopeFallback(
        CSharpCompilation compilation,
        SyntaxTree tree,
        SourceText text,
        int offset,
        CancellationToken cancellationToken)
    {
        var start = offset;
        while (start > 0 && IsIdentifierPart(text[start - 1])) start--;
        var prefix = text.ToString(new TextSpan(start, Math.Max(0, offset - start)));
        if (prefix.Length == 0) return [];

        var model = compilation.GetSemanticModel(tree, ignoreAccessibility: false);
        var range = ToLspRange(text, new TextSpan(start, offset - start));
        return model.LookupSymbols(offset)
            .Where(symbol => symbol.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(symbol => symbol.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
            .Select(symbol => new LspCompletionItem(
                symbol.Name,
                MapSymbolKind(symbol),
                symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                symbol.Name,
                symbol.Name,
                GetDocumentation(symbol),
                InsertTextFormat.PlainText,
                symbol.Name,
                false,
                new LspTextEdit(range, symbol.Name)))
            .ToArray();
    }

    private static bool IsIdentifierPart(char character)
        => char.IsLetterOrDigit(character) || character is '_' or '@';

    private static IEnumerable<ISymbol> EnumerateTypeMembers(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            foreach (var member in current.GetMembers())
                yield return member;
    }

    private static int ToOffset(SourceText text, int line, int character)
    {
        if (text.Lines.Count == 0) return 0;
        var lineIndex = Math.Clamp(line, 0, text.Lines.Count - 1);
        var lineText = text.Lines[lineIndex];
        return lineText.Start + Math.Clamp(character, 0, lineText.Span.Length);
    }

    private static LspRange ToLspRange(SourceText text, TextSpan span)
    {
        var start = text.Lines.GetLinePosition(span.Start);
        var end = text.Lines.GetLinePosition(span.End);
        return new(new(start.Line, start.Character), new(end.Line, end.Character));
    }

    private static CompletionItemKind MapKind(IEnumerable<string> tags)
    {
        var set = tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (set.Contains("Method") || set.Contains("Function")) return CompletionItemKind.Method;
        if (set.Contains("Constructor")) return CompletionItemKind.Constructor;
        if (set.Contains("Property")) return CompletionItemKind.Property;
        if (set.Contains("Field") || set.Contains("Constant")) return CompletionItemKind.Field;
        if (set.Contains("Class")) return CompletionItemKind.Class;
        if (set.Contains("Interface")) return CompletionItemKind.Interface;
        if (set.Contains("Enum")) return CompletionItemKind.Enum;
        if (set.Contains("Keyword")) return CompletionItemKind.Keyword;
        if (set.Contains("Namespace")) return CompletionItemKind.Module;
        if (set.Contains("Parameter") || set.Contains("Local")) return CompletionItemKind.Variable;
        return CompletionItemKind.Text;
    }

    private static CompletionItemKind MapSymbolKind(ISymbol symbol) => symbol switch
    {
        IMethodSymbol => CompletionItemKind.Method,
        IPropertySymbol => CompletionItemKind.Property,
        IFieldSymbol => CompletionItemKind.Field,
        IEventSymbol => CompletionItemKind.Property,
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => CompletionItemKind.Interface,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => CompletionItemKind.Enum,
        INamedTypeSymbol => CompletionItemKind.Class,
        _ => CompletionItemKind.Variable,
    };

    private static string? GetDocumentation(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var summary = XDocument.Parse(xml).Descendants("summary").FirstOrDefault()?.Value.Trim();
            return string.IsNullOrWhiteSpace(summary) ? null : summary;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
