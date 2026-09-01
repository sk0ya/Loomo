using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>選択した式を型のreadonlyフィールドへ抽出する。
/// 意味モデルなしで初期化時期を変える操作なので、ローカル変数・引数を捕捉する式と
/// 式形式のメソッド以外の曖昧な位置は拒否する。</summary>
public static class CSharpExtractFieldService
{
    public static CSharpCodeGenerationResult Extract(
        string filePath,
        string sourceText,
        LspRange selection,
        string fieldName)
        => ExtractCore(filePath, sourceText, selection, fieldName,
            semanticCompilation: null);

    internal static CSharpCodeGenerationResult Extract(
        string filePath,
        string sourceText,
        LspRange selection,
        string fieldName,
        CSharpCompilation semanticCompilation)
        => ExtractCore(filePath, sourceText, selection, fieldName, semanticCompilation);

    private static CSharpCodeGenerationResult ExtractCore(
        string filePath,
        string sourceText,
        LspRange selection,
        string fieldName,
        CSharpCompilation? semanticCompilation)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("C# ファイルでのみフィールド抽出を実行できます。");
        if (!SyntaxFacts.IsValidIdentifier(fieldName)
            || SyntaxFacts.GetKeywordKind(fieldName) != SyntaxKind.None)
            return Failed("フィールド名がC#の識別子として正しくありません。");

        var source = SourceText.From(sourceText);
        if (!TryGetSelectionSpan(source, selection, out var selectedSpan))
            return Failed("選択範囲が文書の範囲外です。");

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var expression = root.DescendantNodes().OfType<ExpressionSyntax>()
            .Where(candidate => candidate.Span == selectedSpan)
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();
        var semanticModel = semanticCompilation is { } compilation
            ? CSharpSemanticCompilation.ForFile(compilation, filePath)
            : null;
        if (semanticCompilation is not null && semanticModel is null)
            return Failed("対象ファイルをC#の意味モデルから解決できません。");
        if (expression is null)
            return Failed("型を推測できる式全体を選択してください。");
        var semanticExpression = semanticModel is not null
            ? FindEquivalent(expression, semanticModel)
            : null;
        if (semanticModel is not null && semanticExpression is null)
            return Failed("選択式をC#の意味モデルへ対応付けられません。");
        var analyzedExpression = semanticExpression ?? expression;
        if (!TryGetFieldType(analyzedExpression, semanticModel, out var fieldType))
            return Failed("型を推測できる式全体を選択してください。");

        if (expression.Ancestors().Any(ancestor => ancestor is AnonymousFunctionExpressionSyntax
                or LocalFunctionStatementSyntax))
            return Failed("ラムダ式やローカル関数の中からはフィールドを抽出できません。");

        var method = expression.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Body is not null || candidate.ExpressionBody is not null);
        if (method is null)
            return Failed("メソッドまたはコンストラクター内の式を選択してください。");
        if (semanticModel is not null)
        {
            if (semanticExpression!.DescendantNodesAndSelf().OfType<ThisExpressionSyntax>().Any())
                return Failed("thisを含む式はフィールド初期化子へ安全に移動できません。");
            if (semanticExpression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Select(identifier => FindEquivalent(identifier, semanticModel))
                .Where(identifier => identifier is not null)
                .Select(identifier => semanticModel.GetSymbolInfo(identifier!).Symbol)
                .Any(symbol => symbol is ILocalSymbol or IParameterSymbol ||
                    symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction } ||
                    method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)) &&
                    symbol is IFieldSymbol { IsStatic: false } or
                    IPropertySymbol { IsStatic: false } or
                    IEventSymbol { IsStatic: false } or
                    IMethodSymbol { IsStatic: false }))
                return Failed("ローカル変数・引数またはstaticメソッドからのinstance member参照はフィールドへ抽出できません。");
        }
        else if (CapturesLocalOrParameter(expression, method))
            return Failed("ローカル変数や引数を捕捉する式はフィールドへ抽出できません。");

        var type = expression.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (type is null || type.OpenBraceToken.IsMissing || type.CloseBraceToken.IsMissing)
            return Failed("型の中にある式を選択してください。");
        if (type.Members.SelectMany(member => member.DescendantTokens())
            .Any(token => token.IsKind(SyntaxKind.IdentifierToken)
                && string.Equals(token.ValueText, fieldName, StringComparison.Ordinal)))
            return Failed("同名のフィールドまたはメンバーが既にあります。");

        var openLine = source.Lines.GetLineFromPosition(type.OpenBraceToken.Span.End);
        var openSuffix = source.ToString(TextSpan.FromBounds(
            type.OpenBraceToken.Span.End, openLine.End));
        if (openSuffix.Trim().Length > 0)
            return Failed("型の開き括弧と同じ行にコードがあるため、安全に挿入できません。");

        var firstMember = type.Members.FirstOrDefault();
        var indentation = firstMember is null
            ? source.ToString(TextSpan.FromBounds(
                source.Lines.GetLineFromPosition(type.CloseBraceToken.SpanStart).Start,
                type.CloseBraceToken.SpanStart)) + "    "
            : source.ToString(TextSpan.FromBounds(
                source.Lines.GetLineFromPosition(firstMember.SpanStart).Start,
                firstMember.SpanStart));
        if (indentation.Any(character => !char.IsWhiteSpace(character)))
            return Failed("型メンバーの字下げを解釈できません。");

        var newline = source.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var literal = source.ToString(selectedSpan);
        var access = method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword))
            ? "private static readonly "
            : "private readonly ";
        var declaration = indentation + access + fieldType + " " + fieldName
            + " = " + literal + ";" + newline;
        var uri = LspUri.FromPath(Path.GetFullPath(filePath));
        var edit = new LspWorkspaceEdit(
            new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
            {
                [uri] =
                [
                    new LspTextEdit(
                        new LspRange(
                            new LspPosition(openLine.LineNumber + 1, 0),
                            new LspPosition(openLine.LineNumber + 1, 0)),
                        declaration),
                    new LspTextEdit(ToLspRange(source, selectedSpan), fieldName),
                ],
            });
        return new CSharpCodeGenerationResult(edit, "フィールド「" + fieldName + "」を抽出");
    }

    private static bool CapturesLocalOrParameter(
        ExpressionSyntax expression, BaseMethodDeclarationSyntax method)
    {
        var names = method.ParameterList.Parameters
            .Select(parameter => parameter.Identifier.ValueText)
            .Concat(method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Select(variable => variable.Identifier.ValueText))
            .Concat(method.DescendantNodes().OfType<ForEachStatementSyntax>()
                .Select(loop => loop.Identifier.ValueText))
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        return expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Any(identifier => names.Contains(identifier.Identifier.ValueText));
    }

    private static bool TryGetFieldType(
        ExpressionSyntax expression,
        SemanticModel? semanticModel,
        out string typeName)
    {
        if (semanticModel is not null)
        {
            var info = semanticModel.GetTypeInfo(expression);
            var type = info.ConvertedType ?? info.Type;
            if (type is not null && type is not IErrorTypeSymbol)
            {
                typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (typeName.Length > 0) return true;
            }
            typeName = "";
            return false;
        }

        typeName = expression switch
        {
            LiteralExpressionSyntax literal => LiteralType(literal),
            InterpolatedStringExpressionSyntax => "string",
            ObjectCreationExpressionSyntax creation => creation.Type.ToString(),
            ArrayCreationExpressionSyntax array => array.Type.ToString(),
            CastExpressionSyntax cast => cast.Type.ToString(),
            DefaultExpressionSyntax defaultExpression => defaultExpression.Type.ToString(),
            _ => "",
        };
        return typeName.Length > 0;
    }

    private static T? FindEquivalent<T>(T node, SemanticModel semanticModel)
        where T : SyntaxNode
        => semanticModel.SyntaxTree.GetRoot().DescendantNodesAndSelf()
            .OfType<T>()
            .FirstOrDefault(candidate => candidate.RawKind == node.RawKind &&
                candidate.Span == node.Span);

    private static string LiteralType(LiteralExpressionSyntax literal)
        => literal.Kind() switch
        {
            SyntaxKind.StringLiteralExpression => "string",
            SyntaxKind.CharacterLiteralExpression => "char",
            SyntaxKind.TrueLiteralExpression or SyntaxKind.FalseLiteralExpression => "bool",
            SyntaxKind.NumericLiteralExpression => NumericType(literal.Token.Text),
            _ => "",
        };

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
