using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>ローカル変数の宣言を取り除き、同じブロック内の安全な参照を初期化式へ置き換える。
/// 意味モデルなしで評価回数やスコープを変えないため、直接のブロック、単一宣言、明示的な書き換えなしに限定する。</summary>
public static class CSharpInlineVariableService
{
    public static CSharpCodeGenerationResult Inline(
        string filePath,
        string sourceText,
        LspRange selection)
        => InlineCore(filePath, sourceText, selection, semanticCompilation: null);

    internal static CSharpCodeGenerationResult Inline(
        string filePath,
        string sourceText,
        LspRange selection,
        CSharpCompilation semanticCompilation)
        => InlineCore(filePath, sourceText, selection, semanticCompilation);

    private static CSharpCodeGenerationResult InlineCore(
        string filePath,
        string sourceText,
        LspRange selection,
        CSharpCompilation? semanticCompilation)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("C# ファイルでのみローカル変数のインライン化を実行できます。");

        var source = SourceText.From(sourceText);
        if (!TryGetSelectionSpan(source, selection, out var selectedSpan))
            return Failed("選択範囲が文書の範囲外です。");

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var selectedName = root.DescendantNodes().OfType<IdentifierNameSyntax>()
            .FirstOrDefault(node => node.Span == selectedSpan);
        var declaration = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(node => node.Identifier.Span == selectedSpan);

        var semanticModel = semanticCompilation is { } compilation
            ? CSharpSemanticCompilation.ForFile(compilation, filePath)
            : null;
        ILocalSymbol? semanticLocal = null;
        if (semanticModel is not null)
        {
            if (declaration is not null && FindEquivalent(declaration, semanticModel) is { } equivalentDeclaration)
                semanticLocal = semanticModel.GetDeclaredSymbol(equivalentDeclaration) as ILocalSymbol;
            else if (selectedName is not null && FindEquivalent(selectedName, semanticModel) is { } equivalentName)
            {
                semanticLocal = semanticModel.GetSymbolInfo(equivalentName).Symbol as ILocalSymbol;
                if (semanticLocal is not null)
                {
                    var declaredSyntax = semanticLocal.DeclaringSyntaxReferences
                        .Select(reference => reference.GetSyntax())
                        .OfType<VariableDeclaratorSyntax>()
                        .FirstOrDefault();
                    declaration = declaredSyntax is null
                        ? null
                        : root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                            .FirstOrDefault(candidate => candidate.Span == declaredSyntax.Span);
                }
            }
        }

        string? name = declaration?.Identifier.ValueText ?? selectedName?.Identifier.ValueText;
        if (name is null)
            return Failed("ローカル変数名全体を選択してください。");

        var local = declaration?.AncestorsAndSelf().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
        BlockSyntax? block = local?.Parent as BlockSyntax;
        if (semanticLocal is not null && declaration is not null)
        {
            local = declaration.AncestorsAndSelf().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
            block = local?.Parent as BlockSyntax;
        }
        if (local is null)
        {
            block = selectedName?.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
            local = FindVisibleDeclaration(block, name, selectedSpan.Start);
        }

        if (local?.Parent is not BlockSyntax declarationBlock || block is null || !ReferenceEquals(block, declarationBlock))
            return Failed("同じブロック内のローカル変数を選択してください。");
        if (local.Declaration.Variables.Count != 1)
            return Failed("複数宣言を含むローカル変数はインライン化できません。");
        if (!local.Declaration.Type.IsVar)
            return Failed("型変換を変えないため、varで宣言された変数だけをインライン化できます。");

        var variable = local.Declaration.Variables[0];
        if (semanticCompilation is not null && semanticLocal is null)
            return Failed("対象ローカル変数をC#の意味モデルから解決できません。");
        if (semanticLocal is not null && semanticModel is not null &&
            (FindEquivalent(variable, semanticModel) is not { } equivalentVariable ||
             semanticModel.GetDeclaredSymbol(equivalentVariable) is not ILocalSymbol resolvedSymbol ||
             !SymbolEqualityComparer.Default.Equals(semanticLocal, resolvedSymbol)))
            return Failed("選択したローカル変数のsymbolが一致しません。");
        if (!string.Equals(variable.Identifier.ValueText, name, StringComparison.Ordinal)
            || variable.Initializer?.Value is not ExpressionSyntax initializer)
            return Failed("初期化式を持つローカル変数を選択してください。");
        if ((semanticLocal is not null && semanticModel is not null &&
             initializer.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(node =>
                 FindEquivalent(node, semanticModel) is { } equivalent &&
                 SymbolEqualityComparer.Default.Equals(
                     semanticModel.GetSymbolInfo(equivalent).Symbol, semanticLocal)))
            || (semanticLocal is null && initializer.DescendantNodesAndSelf()
                .OfType<IdentifierNameSyntax>()
                .Any(node => string.Equals(node.Identifier.ValueText, name, StringComparison.Ordinal))))
            return Failed("自身を参照する初期化式はインライン化できません。");

        var references = semanticLocal is not null && semanticModel is not null
            ? declarationBlock.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Where(node => node.SpanStart >= variable.Span.End)
                .Where(node => FindEquivalent(node, semanticModel) is { } equivalent &&
                    SymbolEqualityComparer.Default.Equals(
                        semanticModel.GetSymbolInfo(equivalent).Symbol, semanticLocal))
                .ToList()
            : declarationBlock.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Where(node => string.Equals(node.Identifier.ValueText, name, StringComparison.Ordinal))
                .Where(node => node.SpanStart >= variable.Span.End)
                .Where(node => IsDirectReference(node, declarationBlock))
                .ToList();
        if (semanticLocal is not null && references.Any(reference =>
                reference.Ancestors().Any(ancestor => ancestor is AnonymousFunctionExpressionSyntax
                    or LocalFunctionStatementSyntax)))
            return Failed("ラムダ式またはローカル関数内の参照は評価時点を変えるためインライン化できません。");
        if (references.Count == 0)
            return Failed("インライン化できる参照が見つかりません。");
        if (references.Any(HasWriteContext))
            return Failed("代入・ref・outで書き換えられる変数はインライン化できません。");
        if (references.Count > 1 && !IsRepeatable(initializer))
            return Failed("初期化式が複数回評価されるため、安全にインライン化できません。");

        var line = source.Lines.GetLineFromPosition(local.SpanStart);
        var prefix = source.ToString(TextSpan.FromBounds(line.Start, local.SpanStart));
        var suffix = source.ToString(TextSpan.FromBounds(local.Span.End, line.End));
        if (prefix.Any(c => !char.IsWhiteSpace(c)) || suffix.Trim().Length > 0)
            return Failed("宣言は単独行に置かれている必要があります。");

        var removeEnd = line.EndIncludingLineBreak;
        var removeRange = new LspRange(
            new LspPosition(line.LineNumber, 0),
            removeEnd > line.End
                ? new LspPosition(line.LineNumber + 1, 0)
                : new LspPosition(line.LineNumber, line.Span.Length));
        var replacement = $"({initializer})";
        var uri = LspUri.FromPath(Path.GetFullPath(filePath));
        var edits = new List<LspTextEdit>
        {
            new(removeRange, ""),
        };
        edits.AddRange(references.Select(reference =>
            new LspTextEdit(ToLspRange(source, reference.Span), replacement)));

        return new CSharpCodeGenerationResult(
            new LspWorkspaceEdit(new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
            {
                [uri] = edits,
            }),
            $"ローカル変数「{name}」をインライン化");
    }

    private static LocalDeclarationStatementSyntax? FindVisibleDeclaration(
        BlockSyntax? block, string name, int position)
        => block?.Statements
            .OfType<LocalDeclarationStatementSyntax>()
            .SelectMany(statement => statement.Declaration.Variables.Select(variable => (statement, variable)))
            .Where(pair => pair.variable.SpanStart < position
                && string.Equals(pair.variable.Identifier.ValueText, name, StringComparison.Ordinal))
            .OrderByDescending(pair => pair.variable.SpanStart)
            .Select(pair => pair.statement)
            .FirstOrDefault();

    private static bool IsDirectReference(IdentifierNameSyntax node, BlockSyntax block)
    {
        if (node.Ancestors().OfType<BlockSyntax>().FirstOrDefault() is not BlockSyntax nearest
            || !ReferenceEquals(nearest, block))
            return false;
        if (node.Parent is MemberAccessExpressionSyntax member && ReferenceEquals(member.Name, node))
            return false;
        if (node.Ancestors().Any(ancestor => ancestor is AnonymousFunctionExpressionSyntax
                or LocalFunctionStatementSyntax)
            || node.Ancestors().OfType<InvocationExpressionSyntax>().Any(invocation =>
                invocation.Expression is IdentifierNameSyntax identifier
                && string.Equals(identifier.Identifier.ValueText, "nameof", StringComparison.Ordinal)))
            return false;
        if (node.Parent is NameColonSyntax or NameEqualsSyntax)
            return false;
        return true;
    }

    private static bool HasWriteContext(IdentifierNameSyntax node)
    {
        if (node.AncestorsAndSelf().OfType<AssignmentExpressionSyntax>()
            .Any(assignment => assignment.Left.Span.Contains(node.Span)))
            return true;
        if (node.AncestorsAndSelf().OfType<PrefixUnaryExpressionSyntax>()
            .Any(unary => unary.IsKind(SyntaxKind.PreIncrementExpression)
                || unary.IsKind(SyntaxKind.PreDecrementExpression)))
            return true;
        if (node.AncestorsAndSelf().OfType<PostfixUnaryExpressionSyntax>()
            .Any(unary => unary.IsKind(SyntaxKind.PostIncrementExpression)
                || unary.IsKind(SyntaxKind.PostDecrementExpression)))
            return true;
        return node.Ancestors().OfType<ArgumentSyntax>()
            .Any(argument => argument.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword);
    }

    private static bool IsRepeatable(ExpressionSyntax expression)
        => expression is LiteralExpressionSyntax
            or IdentifierNameSyntax
            or ThisExpressionSyntax
            or BaseExpressionSyntax;

    private static T? FindEquivalent<T>(T node, SemanticModel semanticModel)
        where T : SyntaxNode
        => semanticModel.SyntaxTree.GetRoot().DescendantNodesAndSelf()
            .OfType<T>()
            .FirstOrDefault(candidate => candidate.RawKind == node.RawKind &&
                candidate.Span == node.Span);

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
