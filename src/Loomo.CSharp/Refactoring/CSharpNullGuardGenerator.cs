using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>メソッド／コンストラクターの参照型引数へ ArgumentNullException.ThrowIfNull を挿入する。</summary>
internal static class CSharpNullGuardGenerator
{
    /// <summary>型の意味解決が無い場合は、明らかな値型だけを構文で除外する。</summary>
    internal static CSharpCodeGenerationResult Generate(
        string filePath, string text, int line, int character,
        CSharpCompilation? semanticCompilation = null)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return CSharpCodeGenerationResult.Failed("C# ファイルでのみコード生成を実行できます。");
        var source = SourceText.From(text);
        if (line < 0 || line >= source.Lines.Count)
            return CSharpCodeGenerationResult.Failed("キャレット位置が文書の範囲外です。");

        var position = GenerationSyntax.ClampToLine(source, line, character);
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var method = root.FindToken(position).Parent?
            .AncestorsAndSelf()
            .OfType<BaseMethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Body is not null);
        if (method?.Body is not { } body)
            return CSharpCodeGenerationResult.Failed("本文を持つメソッドまたはコンストラクターの中にキャレットを置いてください。");

        var semanticModel = semanticCompilation is { } compilation
            ? CSharpSemanticCompilation.ForFile(compilation, filePath)
            : null;
        var semanticMethod = semanticModel is null
            ? null
            : GenerationSyntax.FindEquivalentMethod(method, semanticModel);
        var semanticParameters = semanticMethod is null
            ? null
            : semanticMethod.ParameterList.Parameters
                .Select(parameter => (parameter, symbol: semanticModel.GetDeclaredSymbol(parameter)))
                .ToDictionary(pair => pair.parameter.SpanStart, pair => pair.symbol);
        var parameters = method.ParameterList.Parameters
            .Where(p => p.Type is not null && !p.Modifiers.Any(m =>
                m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword)) &&
                (semanticParameters is not null
                    ? semanticParameters.TryGetValue(p.SpanStart, out var symbol) &&
                      IsReferenceLike(symbol)
                    : IsReferenceLike(p.Type)))
            .ToList();
        if (parameters.Count == 0)
            return CSharpCodeGenerationResult.Failed("null guardを生成できる参照型の引数がありません。");

        var bodyText = body.ToString();
        parameters = parameters.Where(p => !bodyText.Contains(
            $"ThrowIfNull({p.Identifier.Text}", StringComparison.Ordinal)).ToList();
        if (parameters.Count == 0)
            return CSharpCodeGenerationResult.Failed("引数のnull guardは既にあります。");

        var target = body.Statements.FirstOrDefault()?.SpanStart ?? body.CloseBraceToken.SpanStart;
        var targetLine = source.Lines.GetLineFromPosition(target);
        var prefix = source.ToString(TextSpan.FromBounds(targetLine.Start, target));
        if (prefix.Any(c => !char.IsWhiteSpace(c)))
            return CSharpCodeGenerationResult.Failed("メソッド本文が1行に書かれているため、安全に挿入できません。");
        var newline = source.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var insertion = string.Join(newline, parameters.Select(p =>
            $"{prefix}global::System.ArgumentNullException.ThrowIfNull({p.Identifier.Text});")) + newline;
        var range = new LspRange(
            new LspPosition(targetLine.LineNumber, 0),
            new LspPosition(targetLine.LineNumber, 0));
        var edit = new LspWorkspaceEdit(
            new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
            {
                [LspUri.FromPath(Path.GetFullPath(filePath))] = [new LspTextEdit(range, insertion)],
            });
        return new CSharpCodeGenerationResult(edit, "null guardを生成");
    }

    private static bool IsReferenceLike(TypeSyntax type)
    {
        if (type is NullableTypeSyntax or ArrayTypeSyntax or FunctionPointerTypeSyntax)
            return true;
        if (type is not PredefinedTypeSyntax predefined) return true;
        return predefined.Keyword.Kind() is not (
            SyntaxKind.BoolKeyword or SyntaxKind.ByteKeyword or SyntaxKind.SByteKeyword or
            SyntaxKind.ShortKeyword or SyntaxKind.UShortKeyword or SyntaxKind.IntKeyword or
            SyntaxKind.UIntKeyword or SyntaxKind.LongKeyword or SyntaxKind.ULongKeyword or
            SyntaxKind.FloatKeyword or
            SyntaxKind.DoubleKeyword or SyntaxKind.DecimalKeyword or SyntaxKind.CharKeyword);
    }

    private static bool IsReferenceLike(IParameterSymbol? parameter)
    {
        if (parameter is null) return false;
        if (parameter.Type.IsReferenceType) return true;
        return parameter.Type is INamedTypeSymbol named &&
               named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
    }
}
