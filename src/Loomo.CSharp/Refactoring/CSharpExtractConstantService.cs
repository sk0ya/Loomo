using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>定数として安全に表現できるリテラルを型のメンバーへ抽出する。
/// 意味解決なしで型が変わる式は扱わず、文字列・文字・bool・数値リテラルに限定する。</summary>
public static class CSharpExtractConstantService
{
    public static CSharpCodeGenerationResult Extract(
        string filePath,
        string sourceText,
        LspRange selection,
        string constantName)
        => ExtractCore(filePath, sourceText, selection, constantName,
            semanticCompilation: null);

    internal static CSharpCodeGenerationResult Extract(
        string filePath,
        string sourceText,
        LspRange selection,
        string constantName,
        CSharpCompilation semanticCompilation)
        => ExtractCore(filePath, sourceText, selection, constantName,
            semanticCompilation);

    private static CSharpCodeGenerationResult ExtractCore(
        string filePath,
        string sourceText,
        LspRange selection,
        string constantName,
        CSharpCompilation? semanticCompilation)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("C# ファイルでのみ定数抽出を実行できます。");
        if (!SyntaxFacts.IsValidIdentifier(constantName)
            || SyntaxFacts.GetKeywordKind(constantName) != SyntaxKind.None)
            return Failed("定数名がC#の識別子として正しくありません。");

        var source = SourceText.From(sourceText);
        if (!TryGetSelectionSpan(source, selection, out var span))
            return Failed("選択範囲が文書の範囲外です。");

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var expression = root.DescendantNodes().OfType<ExpressionSyntax>()
            .Where(candidate => candidate.Span == span)
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();
        if (expression is null)
            return Failed("文字列・文字・bool・数値リテラル全体を選択してください。");
        var semanticModel = semanticCompilation is { } compilation
            ? CSharpSemanticCompilation.ForFile(compilation, filePath)
            : null;
        if (semanticCompilation is not null && semanticModel is null)
            return Failed("対象ファイルをC#の意味モデルから解決できません。");
        var semanticExpression = semanticModel is not null
            ? FindEquivalent(expression, semanticModel)
            : null;
        if (semanticModel is not null && semanticExpression is null)
            return Failed("選択式をC#の意味モデルへ対応付けられません。");
        var hasConstantType = semanticModel is not null
            ? TryGetSemanticConstantType(semanticExpression!, semanticModel, out var typeName)
            : TryGetConstantType(expression, out typeName);
        if (!hasConstantType)
            return Failed(semanticModel is null
                ? "文字列・文字・bool・数値リテラル全体を選択してください。"
                : "コンパイル時定数として扱える式全体を選択してください。");

        var type = expression.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (type is null || type.CloseBraceToken.IsMissing)
            return Failed("型の中にあるリテラルを選択してください。");
        if (type.Members.Any(member => member.DescendantTokens().Any(token =>
                token.IsKind(SyntaxKind.IdentifierToken)
                && string.Equals(token.ValueText, constantName, StringComparison.Ordinal))))
            return Failed("同名の定数またはメンバーが既にあります。");

        var close = type.CloseBraceToken;
        var closeLine = source.Lines.GetLineFromPosition(close.SpanStart);
        var closeIndent = source.ToString(TextSpan.FromBounds(closeLine.Start, close.SpanStart));
        if (closeIndent.Any(c => !char.IsWhiteSpace(c)))
            return Failed("型の閉じ括弧を含む行の字下げを解釈できません。");

        var member = type.Members.FirstOrDefault();
        var memberIndent = member is null
            ? closeIndent + "    "
            : source.ToString(TextSpan.FromBounds(
                source.Lines.GetLineFromPosition(member.SpanStart).Start, member.SpanStart));
        if (memberIndent.Any(c => !char.IsWhiteSpace(c))) memberIndent = closeIndent + "    ";

        var newline = source.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var literal = source.ToString(span);
        var declaration = $"{memberIndent}private const {typeName} {constantName} = {literal};{newline}";
        var uri = LspUri.FromPath(Path.GetFullPath(filePath));
        var edit = new LspWorkspaceEdit(
            new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
            {
                [uri] =
                [
                    new LspTextEdit(ToLspRange(source, span), constantName),
                    new LspTextEdit(
                        new LspRange(
                            new LspPosition(closeLine.LineNumber, 0),
                            new LspPosition(closeLine.LineNumber, 0)),
                        declaration),
                ],
            });
        return new CSharpCodeGenerationResult(edit, $"定数「{constantName}」を抽出");
    }

    private static bool TryGetConstantType(ExpressionSyntax expression, out string typeName)
    {
        typeName = "";
        var literal = expression as LiteralExpressionSyntax;
        if (literal is null && expression is PrefixUnaryExpressionSyntax prefix
            && prefix.IsKind(SyntaxKind.UnaryMinusExpression)
            && prefix.Operand is LiteralExpressionSyntax negativeLiteral)
            literal = negativeLiteral;
        if (literal is null) return false;

        typeName = literal.Kind() switch
        {
            SyntaxKind.StringLiteralExpression => "string",
            SyntaxKind.CharacterLiteralExpression => "char",
            SyntaxKind.TrueLiteralExpression or SyntaxKind.FalseLiteralExpression => "bool",
            SyntaxKind.NumericLiteralExpression => NumericType(literal.Token.Text),
            _ => "",
        };
        return typeName.Length > 0;
    }

    private static bool TryGetSemanticConstantType(
        ExpressionSyntax expression, SemanticModel semanticModel, out string typeName)
    {
        typeName = "";
        if (!semanticModel.GetConstantValue(expression).HasValue)
            return false;
        var type = semanticModel.GetTypeInfo(expression).ConvertedType ??
                   semanticModel.GetTypeInfo(expression).Type;
        if (type is null || type is IErrorTypeSymbol || !IsValidConstantType(type))
            return false;
        typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return typeName.Length > 0;
    }

    private static bool IsValidConstantType(ITypeSymbol type)
        => type.TypeKind == TypeKind.Enum || type.SpecialType is
            SpecialType.System_Boolean or SpecialType.System_Byte or
            SpecialType.System_SByte or SpecialType.System_Char or
            SpecialType.System_Decimal or SpecialType.System_Double or
            SpecialType.System_Single or SpecialType.System_Int16 or
            SpecialType.System_UInt16 or SpecialType.System_Int32 or
            SpecialType.System_UInt32 or SpecialType.System_Int64 or
            SpecialType.System_UInt64 or SpecialType.System_String;

    private static T? FindEquivalent<T>(T node, SemanticModel semanticModel)
        where T : SyntaxNode
        => semanticModel.SyntaxTree.GetRoot().DescendantNodesAndSelf()
            .OfType<T>()
            .FirstOrDefault(candidate => candidate.RawKind == node.RawKind &&
                candidate.Span == node.Span);

    private static string NumericType(string token)
    {
        var value = token.Trim();
        if (value.EndsWith("m", StringComparison.OrdinalIgnoreCase)) return "decimal";
        if (value.EndsWith("f", StringComparison.OrdinalIgnoreCase)) return "float";
        if (value.EndsWith("d", StringComparison.OrdinalIgnoreCase)
            || value.Contains('.', StringComparison.Ordinal)
            || value.Contains('e', StringComparison.OrdinalIgnoreCase)) return "double";
        if (value.EndsWith("ul", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("lu", StringComparison.OrdinalIgnoreCase)) return "ulong";
        if (value.EndsWith("u", StringComparison.OrdinalIgnoreCase)) return "uint";
        if (value.EndsWith("l", StringComparison.OrdinalIgnoreCase)) return "long";
        return "int";
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
