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
    /// <summary>
    /// 補完の入口。<b>中身を丸ごと背景スレッドへ逃がす。</b>
    ///
    /// <para>エディタはこの供給元を<b>UI スレッドから</b>呼ぶ（LSP が空応答のときの fallback で、
    /// 打鍵 300ms 後の DispatcherTimer から走る）。本体は最初の <c>await</c> に着く前に
    /// ソリューション全体のソース読み込み・構文解析・Workspace 構築を同期で済ませるので、
    /// ここで包まないとその時間ぶん<b>打鍵が止まる</b>——実測で 1 回 1.3〜3 秒だった。
    /// semanticTokens／signatureHelp／inlayHint／hover の供給元と同じ形に揃えてある。</para>
    /// </summary>
    public static Task<IReadOnlyList<LspCompletionItem>> GetAsync(
        SolutionModel? solution,
        string filePath,
        string source,
        int line,
        int character,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? openTexts = null)
        => Task.Run(() => GetCoreAsync(
            solution, filePath, source, line, character, cancellationToken, openTexts), cancellationToken);

    private static async Task<IReadOnlyList<LspCompletionItem>> GetCoreAsync(
        SolutionModel? solution,
        string filePath,
        string source,
        int line,
        int character,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? openTexts)
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
        foreach (var item in SelectItemsToExpand(list, text))
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
                new LspTextEdit(range, insertText),
                AdditionalTextEdits: additionalTextEdits.Length == 0
                    ? null : additionalTextEdits));
        }

        return result.Count > 0
            ? result
            .GroupBy(item => (item.Label, item.TextEdit?.Range.Start.Line,
                item.TextEdit?.Range.Start.Character, item.InsertText))
            .Select(group => group.First())
            .ToArray()
            : BuildMemberFallback(compilation, tree, text, offset, cancellationToken);
    }

    /// <summary>1 回の応答で中身まで組み立てる候補の上限。</summary>
    private const int MaxExpandedItems = 300;

    /// <summary>
    /// 中身まで展開する候補を選ぶ。
    ///
    /// <para>Roslyn の <see cref="CompletionService"/> は「その位置で構文的にあり得る候補」を
    /// <b>全部</b>返す——絞り込みは IDE の仕事だからで、Loomo 自身の solution に対して実測 11,100 件だった。
    /// 以前はその全件に <see cref="CompletionService.GetChangeAsync"/> と
    /// <see cref="CompletionService.GetDescriptionAsync"/> を 1 件ずつ掛けていたので、
    /// 1 回の補完が<b>約 22 秒</b>かかっていた（しかも UI スレッドの上で）。</para>
    ///
    /// <para>そこで、打ったプレフィックス（<see cref="CompletionList.Span"/> が指す範囲の本文）で
    /// 先に絞り、さらに上限を掛ける。実測で「w」223 件・「wo」64 件・「workspa」41 件まで落ち、
    /// 展開は 22 秒から 0.2〜1.5 秒になる。エディタ側は返した一覧をさらに絞り込むので、
    /// <b>打てば打つほど正確になる</b>方向のずれしか残らない——上限に当たるのは
    /// プレフィックスが無いまま手動で呼んだ場合（Ctrl+Space）だけで、そこは
    /// 1 画面に出る数をはるかに超えている。</para>
    /// </summary>
    private static IReadOnlyList<CompletionItem> SelectItemsToExpand(
        CompletionList list, SourceText text)
    {
        IReadOnlyList<CompletionItem> items = list.ItemsList;

        var span = list.Span;
        if (span.Length > 0 && span.End <= text.Length)
        {
            var prefix = text.ToString(span);
            if (prefix.Length > 0)
            {
                var matched = items
                    .Where(item => MatchText(item).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                // 一件も前方一致しないときは絞らない（CamelCase 入力などを切り捨てないため）。
                if (matched.Length > 0) items = matched;
            }
        }

        return items.Count <= MaxExpandedItems
            ? items
            : items
                .OrderBy(item => item.SortText ?? item.DisplayText, StringComparer.OrdinalIgnoreCase)
                .Take(MaxExpandedItems)
                .ToArray();
    }

    private static string MatchText(CompletionItem item)
        => item.FilterText is { Length: > 0 } filter ? filter : item.DisplayText;

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
