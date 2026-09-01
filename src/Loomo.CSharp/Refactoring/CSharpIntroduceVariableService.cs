using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>選択した式を同じブロックの直前へローカル変数として導入する。
/// 意味モデルを持たないため型は <c>var</c> に限定し、コンパイラーが型を推論できない
/// 式とスコープが曖昧な位置は変更しない。</summary>
public static class CSharpIntroduceVariableService
{
    public static CSharpCodeGenerationResult Introduce(
        string filePath,
        string sourceText,
        LspRange selection,
        string variableName)
        => IntroduceCore(filePath, sourceText, selection, variableName,
            semanticCompilation: null);

    internal static CSharpCodeGenerationResult Introduce(
        string filePath,
        string sourceText,
        LspRange selection,
        string variableName,
        CSharpCompilation semanticCompilation)
        => IntroduceCore(filePath, sourceText, selection, variableName,
            semanticCompilation);

    private static CSharpCodeGenerationResult IntroduceCore(
        string filePath,
        string sourceText,
        LspRange selection,
        string variableName,
        CSharpCompilation? semanticCompilation)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("C# ファイルでのみローカル変数の導入を実行できます。");
        if (!SyntaxFacts.IsValidIdentifier(variableName)
            || SyntaxFacts.GetKeywordKind(variableName) != SyntaxKind.None)
            return Failed("変数名がC#の識別子として正しくありません。");

        var source = SourceText.From(sourceText);
        if (!TryGetSelectionSpan(source, selection, out var span))
            return Failed("選択範囲が文書の範囲外です。");

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var expression = root.DescendantNodes().OfType<ExpressionSyntax>()
            .Where(candidate => candidate.Span == span)
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();
        if (expression is null)
            return Failed("式全体を選択してください。");

        var semanticModel = semanticCompilation is { } compilation
            ? CSharpSemanticCompilation.ForFile(compilation, filePath)
            : null;
        if (semanticCompilation is not null && semanticModel is null)
            return Failed("対象ファイルをC#の意味モデルから解決できません。");
        if (semanticModel is not null)
        {
            if (FindEquivalent(expression, semanticModel) is not { } semanticExpression)
                return Failed("選択式をC#の意味モデルへ対応付けられません。");
            var typeInfo = semanticModel.GetTypeInfo(semanticExpression);
            var expressionType = typeInfo.ConvertedType ?? typeInfo.Type;
            if (expressionType is null || expressionType.SpecialType == SpecialType.System_Void ||
                expressionType is IErrorTypeSymbol)
                return Failed("戻り値のない式や型を解決できない式はローカル変数へ導入できません。");
        }
        if (expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression }
            or AnonymousFunctionExpressionSyntax
            or ImplicitObjectCreationExpressionSyntax)
            return Failed("選択した式はvarへ安全に導入できません。");

        var statement = expression.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
        if (statement?.Parent is not BlockSyntax block)
            return Failed("同じブロック内の文から導入してください。");
        if (statement is not ExpressionStatementSyntax
            and not ReturnStatementSyntax
            and not ThrowStatementSyntax
            and not LocalDeclarationStatementSyntax)
            return Failed("条件式やループ式からは安全にローカル変数を導入できません。");
        var line = source.Lines.GetLineFromPosition(statement.SpanStart);
        var indentation = source.ToString(TextSpan.FromBounds(line.Start, statement.SpanStart));
        if (indentation.Any(c => !char.IsWhiteSpace(c)))
            return Failed("文の開始位置を安全に判定できません。");

        if (block.DescendantNodes().OfType<VariableDeclaratorSyntax>().Any(variable =>
                string.Equals(variable.Identifier.ValueText, variableName, StringComparison.Ordinal))
            || block.Ancestors().OfType<BaseMethodDeclarationSyntax>().SelectMany(method =>
                method.ParameterList.Parameters).Any(parameter =>
                string.Equals(parameter.Identifier.ValueText, variableName, StringComparison.Ordinal)))
            return Failed("同名のローカル変数または引数が既にあります。");

        var expressionText = source.ToString(span);
        var newline = source.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var insertion = $"{indentation}var {variableName} = {expressionText};{newline}";
        var uri = LspUri.FromPath(Path.GetFullPath(filePath));
        var edit = new LspWorkspaceEdit(
            new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
            {
                [uri] =
                [
                    new LspTextEdit(ToLspRange(source, span), variableName),
                    new LspTextEdit(
                        new LspRange(
                            new LspPosition(line.LineNumber, 0),
                            new LspPosition(line.LineNumber, 0)),
                        insertion),
                ],
            });
        return new CSharpCodeGenerationResult(edit, $"変数「{variableName}」を導入");
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

    private static T? FindEquivalent<T>(T node, SemanticModel semanticModel)
        where T : SyntaxNode
        => semanticModel.SyntaxTree.GetRoot().DescendantNodesAndSelf()
            .OfType<T>()
            .FirstOrDefault(candidate => candidate.RawKind == node.RawKind &&
                candidate.Span == node.Span);

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
