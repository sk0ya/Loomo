using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>選択した式を同じ型の計算プロパティへ導入する。
/// 意味モデルなしでもコンパイル不能な編集を作らないよう、メソッドのローカル変数・
/// 引数を捕捉する式や、複数行・匿名関数を対象外にしている。</summary>
public static class CSharpIntroducePropertyService
{
    public static CSharpCodeGenerationResult Introduce(
        string filePath,
        string sourceText,
        LspRange selection,
        string propertyName,
        string propertyType,
        string accessibility = "private")
        => IntroduceCore(filePath, sourceText, selection, propertyName,
            propertyType, accessibility, semanticCompilation: null);

    internal static CSharpCodeGenerationResult Introduce(
        string filePath,
        string sourceText,
        LspRange selection,
        string propertyName,
        string propertyType,
        string accessibility,
        CSharpCompilation semanticCompilation)
        => IntroduceCore(filePath, sourceText, selection, propertyName,
            propertyType, accessibility, semanticCompilation);

    private static CSharpCodeGenerationResult IntroduceCore(
        string filePath,
        string sourceText,
        LspRange selection,
        string propertyName,
        string propertyType,
        string accessibility,
        CSharpCompilation? semanticCompilation)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("C# ファイルでのみプロパティ導入を実行できます。");
        propertyName = propertyName.Trim();
        propertyType = propertyType.Trim();
        accessibility = accessibility.Trim().ToLowerInvariant();
        if (!SyntaxFacts.IsValidIdentifier(propertyName)
            || SyntaxFacts.GetKeywordKind(propertyName) != SyntaxKind.None)
            return Failed("プロパティ名がC#の識別子として正しくありません。");
        if (accessibility is not ("private" or "internal" or "protected" or "public"))
            return Failed("プロパティのアクセス修飾子はprivate／internal／protected／publicのみです。");

        var parsedType = SyntaxFactory.ParseTypeName(propertyType);
        if (propertyType.Length == 0 || parsedType.ContainsDiagnostics)
            return Failed("プロパティの型がC#構文として正しくありません。");

        var source = SourceText.From(sourceText);
        if (!TryGetSelectionSpan(source, selection, out var selectedSpan))
            return Failed("プロパティへ導入する式全体を選択してください。");
        var selectedText = source.ToString(selectedSpan);
        if (selectedText.Contains("\n", StringComparison.Ordinal)
            || selectedText.Contains("\r", StringComparison.Ordinal))
            return Failed("複数行の式はプロパティへ導入できません。");

        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var expression = root.DescendantNodes().OfType<ExpressionSyntax>()
            .Where(candidate => candidate.Span == selectedSpan)
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();
        if (expression is null || expression.ContainsDiagnostics)
            return Failed("式全体を選択してください。");

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

        if (expression.DescendantNodesAndSelf().Any(node =>
                node is AnonymousFunctionExpressionSyntax or QueryExpressionSyntax))
            return Failed("匿名関数またはクエリ式は安全にプロパティへ導入できません。");

        var containingType = expression.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType is null)
            return Failed("クラス・構造体・レコードの中の式を選択してください。");
        if (containingType.Members.Any(member => string.Equals(
                GetMemberName(member), propertyName, StringComparison.Ordinal)))
            return Failed("同名のメンバーが既にあります。");

        var containingMethod = expression.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod is not null)
        {
            if (semanticModel is not null)
            {
                if (semanticExpression!.DescendantNodesAndSelf().OfType<ThisExpressionSyntax>().Any()
                    && containingMethod.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)))
                    return Failed("staticメソッドからthisを参照する式はプロパティへ導入できません。");
                if (semanticExpression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                    .Select(identifier => FindEquivalent(identifier, semanticModel))
                    .Where(identifier => identifier is not null)
                    .Select(identifier => semanticModel.GetSymbolInfo(identifier!).Symbol)
                    .Any(symbol => symbol is ILocalSymbol or IParameterSymbol ||
                        symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction } ||
                        containingMethod.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)) &&
                        symbol is IFieldSymbol { IsStatic: false } or
                        IPropertySymbol { IsStatic: false } or
                        IEventSymbol { IsStatic: false } or
                        IMethodSymbol { IsStatic: false }))
                    return Failed("ローカル変数・引数またはstaticメソッドからのinstance member参照はプロパティへ導入できません。");
            }
            else if (CapturesLocalOrParameter(expression, containingMethod))
                return Failed("ローカル変数または引数を参照する式はプロパティへ導入できません。");
        }

        var close = containingType.CloseBraceToken;
        if (close.IsMissing)
            return Failed("型の閉じ波括弧を見つけられません。");
        var closeLine = source.Lines.GetLineFromPosition(close.SpanStart);
        var closeIndent = source.ToString(TextSpan.FromBounds(closeLine.Start, close.SpanStart));
        if (closeIndent.Any(character => !char.IsWhiteSpace(character)))
            return Failed("型の閉じ波括弧が行頭にないため、安全に挿入できません。");

        var memberIndent = FindMemberIndent(source, containingType, closeIndent);
        var expressionText = selectedText.Trim();
        var newline = source.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var staticPrefix = containingMethod?.Modifiers.Any(modifier =>
            modifier.IsKind(SyntaxKind.StaticKeyword)) == true ? "static " : "";
        var propertyText = $"{memberIndent}{accessibility} {staticPrefix}{propertyType} {propertyName} => {expressionText};{newline}";
        var uri = LspUri.FromPath(Path.GetFullPath(filePath));
        var insertionLine = new LspPosition(closeLine.LineNumber, 0);
        var edit = new LspWorkspaceEdit(
            new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
            {
                [uri] =
                [
                    new LspTextEdit(ToLspRange(source, selectedSpan), propertyName),
                    new LspTextEdit(new LspRange(insertionLine, insertionLine), propertyText),
                ],
            });
        return new CSharpCodeGenerationResult(edit,
            $"式をプロパティ「{propertyName}」へ導入");
    }

    private static bool CapturesLocalOrParameter(
        ExpressionSyntax expression, BaseMethodDeclarationSyntax method)
    {
        var names = method.ParameterList.Parameters
            .Select(parameter => parameter.Identifier.ValueText)
            .Concat(method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Select(variable => variable.Identifier.ValueText))
            .ToHashSet(StringComparer.Ordinal);
        return expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(identifier =>
            names.Contains(identifier.Identifier.ValueText) && !IsExplicitInstanceMember(identifier));
    }

    private static bool IsExplicitInstanceMember(IdentifierNameSyntax identifier)
        => identifier.Parent is MemberAccessExpressionSyntax member
            && ReferenceEquals(member.Name, identifier)
            && member.Expression is ThisExpressionSyntax or BaseExpressionSyntax;

    private static string? GetMemberName(MemberDeclarationSyntax member)
        => member switch
        {
            BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
            BaseMethodDeclarationSyntax method => method switch
            {
                ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
                MethodDeclarationSyntax methodDeclaration => methodDeclaration.Identifier.ValueText,
                DestructorDeclarationSyntax destructor => destructor.Identifier.ValueText,
                OperatorDeclarationSyntax @operator => @operator.OperatorToken.ValueText,
                ConversionOperatorDeclarationSyntax conversion => conversion.Type.ToString(),
                _ => null,
            },
            BasePropertyDeclarationSyntax property => property switch
            {
                PropertyDeclarationSyntax propertyDeclaration => propertyDeclaration.Identifier.ValueText,
                IndexerDeclarationSyntax => "this[]",
                EventDeclarationSyntax eventDeclaration => eventDeclaration.Identifier.ValueText,
                _ => null,
            },
            FieldDeclarationSyntax field => field.Declaration.Variables.Count == 1
                ? field.Declaration.Variables[0].Identifier.ValueText
                : null,
            EventFieldDeclarationSyntax field => field.Declaration.Variables.Count == 1
                ? field.Declaration.Variables[0].Identifier.ValueText
                : null,
            _ => null,
        };

    private static T? FindEquivalent<T>(T node, SemanticModel semanticModel)
        where T : SyntaxNode
        => semanticModel.SyntaxTree.GetRoot().DescendantNodesAndSelf()
            .OfType<T>()
            .FirstOrDefault(candidate => candidate.RawKind == node.RawKind &&
                candidate.Span == node.Span);

    private static string FindMemberIndent(SourceText source, TypeDeclarationSyntax type, string closeIndent)
    {
        var member = type.Members.FirstOrDefault();
        if (member is null) return closeIndent + "    ";
        var line = source.Lines.GetLineFromPosition(member.SpanStart);
        var prefix = source.ToString(TextSpan.FromBounds(line.Start, member.SpanStart));
        return prefix.All(char.IsWhiteSpace) ? prefix : closeIndent + "    ";
    }

    private static bool TryGetSelectionSpan(SourceText source, LspRange range, out TextSpan span)
    {
        span = default;
        if (range.Start.Line < 0 || range.End.Line < 0
            || range.Start.Line >= source.Lines.Count || range.End.Line >= source.Lines.Count)
            return false;
        var startLine = source.Lines[range.Start.Line];
        var endLine = source.Lines[range.End.Line];
        var start = startLine.Start + Math.Clamp(range.Start.Character, 0, startLine.Span.Length);
        var end = endLine.Start + Math.Clamp(range.End.Character, 0, endLine.Span.Length);
        if (start > end) (start, end) = (end, start);
        if (start == end) return false;
        span = TextSpan.FromBounds(start, end);
        return true;
    }

    private static LspRange ToLspRange(SourceText source, TextSpan span)
    {
        var start = source.Lines.GetLinePosition(span.Start);
        var end = source.Lines.GetLinePosition(span.End);
        return new LspRange(new LspPosition(start.Line, start.Character),
            new LspPosition(end.Line, end.Character));
    }

    private static CSharpCodeGenerationResult Failed(string error)
        => new(null, "", error);
}
