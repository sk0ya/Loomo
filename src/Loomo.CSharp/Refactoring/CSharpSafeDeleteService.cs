using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>構文参照を全ワークスペースで確認してから、privateメンバーを削除する。
/// 意味モデルなしで外部アセンブリの参照までは保証できないため、公開APIとトップレベル型は対象外にする。</summary>
public static class CSharpSafeDeleteService
{
    public static CSharpCodeGenerationResult Delete(
        string filePath,
        string sourceText,
        LspRange selection,
        IReadOnlyDictionary<string, string>? workspaceTexts = null,
        IReadOnlyDictionary<string, CSharpParseOptions>? workspaceParseOptions = null)
        => DeleteCore(filePath, sourceText, selection, workspaceTexts,
            workspaceParseOptions, semanticCompilation: null);

    /// <summary>Roslyn symbol identityで参照を確認する意味モデル付き経路。
    /// AppへCompilation型を公開しないため、呼び出しはCSharp専用DLL内部に限定する。</summary>
    internal static CSharpCodeGenerationResult Delete(
        string filePath,
        string sourceText,
        LspRange selection,
        IReadOnlyDictionary<string, string>? workspaceTexts,
        IReadOnlyDictionary<string, CSharpParseOptions>? workspaceParseOptions,
        CSharpCompilation semanticCompilation)
        => DeleteCore(filePath, sourceText, selection, workspaceTexts,
            workspaceParseOptions, semanticCompilation);

    private static CSharpCodeGenerationResult DeleteCore(
        string filePath,
        string sourceText,
        LspRange selection,
        IReadOnlyDictionary<string, string>? workspaceTexts,
        IReadOnlyDictionary<string, CSharpParseOptions>? workspaceParseOptions,
        CSharpCompilation? semanticCompilation)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("C# ファイルでのみ安全な削除を実行できます。");

        var source = SourceText.From(sourceText);
        if (!TryGetSelectionSpan(source, selection, out var selectedSpan))
            return Failed("選択範囲が文書の範囲外です。");

        var parseOptions = ParseOptionsFor(filePath, workspaceParseOptions);
        var root = CSharpSyntaxTree.ParseText(source, parseOptions).GetRoot();
        var candidates = FindCandidates(root, selectedSpan);
        if (candidates.Count != 1)
            return Failed("削除する型またはメンバー名全体を選択してください。");

        var (target, name) = candidates[0];
        if (target.Ancestors().OfType<InterfaceDeclarationSyntax>().Any())
            return Failed("interfaceの公開契約は安全な削除の対象外です。");
        if (target is TypeDeclarationSyntax type && !type.Ancestors().OfType<TypeDeclarationSyntax>().Any())
            return Failed("トップレベル型は外部アセンブリから参照される可能性があるため対象外です。");
        if (target is MemberDeclarationSyntax member
            && member.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword)
                || modifier.IsKind(SyntaxKind.ProtectedKeyword)
                || modifier.IsKind(SyntaxKind.InternalKeyword)))
            return Failed("公開・protected・internalメンバーは安全な削除の対象外です。");

        if (target is FieldDeclarationSyntax field && field.Declaration.Variables.Count != 1)
            return Failed("複数宣言を含むフィールドは安全な削除の対象外です。");
        if (target is MethodDeclarationSyntax method)
        {
            var owner = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (owner is null || owner.Members.OfType<MethodDeclarationSyntax>().Count(candidate =>
                    string.Equals(candidate.Identifier.ValueText, name, StringComparison.Ordinal)) != 1)
                return Failed("overloadがあるため、削除するメソッドを一意に解決できません。");
        }

        if (semanticCompilation is not null)
        {
            var semanticModel = CSharpSemanticCompilation.ForFile(semanticCompilation, filePath);
            if (semanticModel is null || FindEquivalentTarget(target, semanticModel) is not { } semanticTarget)
                return Failed("削除対象のsymbolを意味モデルから解決できません。");
            var symbol = DeclaredSymbol(semanticTarget, name, semanticModel);
            if (symbol is null)
                return Failed("削除対象のsymbolを意味モデルから解決できません。");

            var references = FindSemanticReferences(semanticCompilation, symbol);
            if (references > 0)
                return Failed($"「{name}」への参照が {references} 件あるため削除できません。");
        }
        else
        {
            var roots = ParseWorkspaceRoots(filePath, root, workspaceTexts, workspaceParseOptions);
            var references = roots.SelectMany(candidateRoot => candidateRoot.DescendantNodes()
                    .OfType<IdentifierNameSyntax>()
                    .Where(identifier => string.Equals(
                        identifier.Identifier.ValueText, name, StringComparison.Ordinal)))
                .ToList();
            if (references.Count > 0)
                return Failed($"「{name}」への参照が {references.Count} 件あるため削除できません。");
        }

        var uri = LspUri.FromPath(Path.GetFullPath(filePath));
        var edit = new LspWorkspaceEdit(
            new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
            {
                [uri] = [new LspTextEdit(RemovalRange(source, target), "")],
            });
        return new CSharpCodeGenerationResult(edit, $"「{name}」を安全に削除");
    }

    private static SyntaxNode? FindEquivalentTarget(
        SyntaxNode target, SemanticModel semanticModel)
        => semanticModel.SyntaxTree.GetRoot().DescendantNodes()
            .FirstOrDefault(candidate => candidate.RawKind == target.RawKind &&
                candidate.SpanStart == target.SpanStart);

    private static ISymbol? DeclaredSymbol(
        SyntaxNode target, string name, SemanticModel semanticModel)
        => target switch
        {
            FieldDeclarationSyntax field => field.Declaration.Variables
                .Where(variable => string.Equals(variable.Identifier.ValueText, name,
                    StringComparison.Ordinal))
                .Select(variable => semanticModel.GetDeclaredSymbol(variable))
                .FirstOrDefault(),
            EventFieldDeclarationSyntax field => field.Declaration.Variables
                .Where(variable => string.Equals(variable.Identifier.ValueText, name,
                    StringComparison.Ordinal))
                .Select(variable => semanticModel.GetDeclaredSymbol(variable))
                .FirstOrDefault(),
            MemberDeclarationSyntax member => semanticModel.GetDeclaredSymbol(member),
            _ => null,
        };

    private static int FindSemanticReferences(
        CSharpCompilation compilation, ISymbol target)
    {
        var count = 0;
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree, ignoreAccessibility: false);
            foreach (var identifier in tree.GetRoot().DescendantNodes()
                         .OfType<IdentifierNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(identifier).Symbol;
                if (symbol is not null &&
                    SymbolEqualityComparer.Default.Equals(symbol, target))
                    count++;
            }
        }
        return count;
    }

    private static List<(SyntaxNode Node, string Name)> FindCandidates(
        SyntaxNode root, TextSpan selection)
    {
        var candidates = new List<(SyntaxNode, string)>();
        candidates.AddRange(root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(type => type.Identifier.Span == selection)
            .Select(type => ((SyntaxNode)type, type.Identifier.ValueText)));
        candidates.AddRange(root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.Span == selection)
            .Select(method => ((SyntaxNode)method, method.Identifier.ValueText)));
        candidates.AddRange(root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Where(property => property.Identifier.Span == selection)
            .Select(property => ((SyntaxNode)property, property.Identifier.ValueText)));
        candidates.AddRange(root.DescendantNodes().OfType<EventDeclarationSyntax>()
            .Where(@event => @event.Identifier.Span == selection)
            .Select(@event => ((SyntaxNode)@event, @event.Identifier.ValueText)));
        candidates.AddRange(root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(field => field.Declaration.Variables.Any(variable => variable.Identifier.Span == selection))
            .Select(field => ((SyntaxNode)field,
                field.Declaration.Variables.First(variable => variable.Identifier.Span == selection)
                    .Identifier.ValueText)));
        candidates.AddRange(root.DescendantNodes().OfType<EventFieldDeclarationSyntax>()
            .Where(field => field.Declaration.Variables.Any(variable => variable.Identifier.Span == selection))
            .Select(field => ((SyntaxNode)field,
                field.Declaration.Variables.First(variable => variable.Identifier.Span == selection)
                    .Identifier.ValueText)));
        return candidates;
    }

    private static IReadOnlyList<SyntaxNode> ParseWorkspaceRoots(
        string activeFilePath,
        SyntaxNode activeRoot,
        IReadOnlyDictionary<string, string>? workspaceTexts,
        IReadOnlyDictionary<string, CSharpParseOptions>? workspaceParseOptions)
    {
        if (workspaceTexts is null || workspaceTexts.Count == 0)
            return [activeRoot];

        var roots = new List<SyntaxNode> { activeRoot };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(activeFilePath),
        };
        foreach (var (path, text) in workspaceTexts)
        {
            if (!string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase)
                || !seen.Add(Path.GetFullPath(path)))
                continue;
            var fullPath = Path.GetFullPath(path);
            var parseOptions = ParseOptionsFor(fullPath, workspaceParseOptions);
            roots.Add(CSharpSyntaxTree.ParseText(text, parseOptions).GetRoot());
        }
        return roots;
    }

    private static CSharpParseOptions ParseOptionsFor(
        string path, IReadOnlyDictionary<string, CSharpParseOptions>? options)
        => options is not null && options.TryGetValue(Path.GetFullPath(path), out var configured)
            ? configured : CSharpParseOptions.Default;

    private static LspRange RemovalRange(SourceText source, SyntaxNode node)
    {
        var firstLine = source.Lines.GetLineFromPosition(node.SpanStart);
        var lastLine = source.Lines.GetLineFromPosition(node.Span.End);
        var prefix = source.ToString(TextSpan.FromBounds(firstLine.Start, node.SpanStart));
        var suffix = source.ToString(TextSpan.FromBounds(node.Span.End, lastLine.End));
        if (prefix.All(char.IsWhiteSpace) && suffix.All(char.IsWhiteSpace))
        {
            var end = lastLine.EndIncludingLineBreak;
            return end > lastLine.End
                ? new LspRange(
                    new LspPosition(firstLine.LineNumber, 0),
                    new LspPosition(lastLine.LineNumber + 1, 0))
                : new LspRange(
                    new LspPosition(firstLine.LineNumber, 0),
                    new LspPosition(lastLine.LineNumber, lastLine.Span.Length));
        }
        return ToLspRange(source, node.Span);
    }

    private static bool TryGetSelectionSpan(SourceText source, LspRange range, out TextSpan span)
    {
        span = default;
        if (range.Start.Line < 0 || range.End.Line < 0
            || range.Start.Line >= source.Lines.Count || range.End.Line >= source.Lines.Count)
            return false;
        var start = Position(source, range.Start);
        var end = Position(source, range.End);
        if (start > end) (start, end) = (end, start);
        if (start == end) return false;
        span = TextSpan.FromBounds(start, end);
        return true;
    }

    private static int Position(SourceText source, LspPosition position)
    {
        var line = source.Lines[position.Line];
        return line.Start + Math.Clamp(position.Character, 0, line.Span.Length);
    }

    private static LspRange ToLspRange(SourceText source, TextSpan span)
    {
        var start = source.Lines.GetLinePosition(span.Start);
        var end = source.Lines.GetLinePosition(span.End);
        return new LspRange(
            new LspPosition(start.Line, start.Character),
            new LspPosition(end.Line, end.Character));
    }

    private static CSharpCodeGenerationResult Failed(string error)
        => new(null, "", error);
}
