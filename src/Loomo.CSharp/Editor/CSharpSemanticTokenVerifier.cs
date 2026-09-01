using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Editor.Core.Syntax;

namespace sk0ya.Loomo.CSharp.Editor;

/// <summary>Roslyn等のLSP semantic tokenと、CSharpSyntaxLanguageのfallbackが同じ本文範囲を
/// 指しているかを検証する。semantic tokenが存在する箇所はLSPを正本とし、fallbackの文字列・
/// コメント・プリプロセッサを意味トークンが誤って塗り替えないことだけを厳密に確認する。</summary>
public static class CSharpSemanticTokenVerifier
{
    public static CSharpSemanticTokenComparison Compare(
        IReadOnlyList<string> lines, IReadOnlyList<SemanticToken> semanticTokens)
    {
        var fallback = new CSharpSyntaxLanguage().Tokenize(lines.ToArray());
        var mismatches = new List<CSharpSemanticTokenMismatch>();
        var compared = 0;
        foreach (var token in semanticTokens)
        {
            if (token.Line < 0 || token.Line >= lines.Count
                || token.StartChar < 0 || token.Length <= 0
                || token.StartChar + token.Length > lines[token.Line].Length)
            {
                mismatches.Add(new CSharpSemanticTokenMismatch(
                    token.Line, token.StartChar, token.Length, token.TokenType,
                    "semantic tokenの範囲が本文の範囲外です。"));
                continue;
            }

            compared++;
            var overlapping = fallback[token.Line].Tokens
                .Where(candidate => candidate.StartColumn < token.StartChar + token.Length
                    && token.StartChar < candidate.StartColumn + candidate.Length)
                .ToArray();
            if (overlapping.Any(candidate => candidate.Kind is TokenKind.String
                    or TokenKind.Comment or TokenKind.Preprocessor)
                && !IsSameProtectedKind(token.TokenType, overlapping))
            {
                mismatches.Add(new CSharpSemanticTokenMismatch(
                    token.Line, token.StartChar, token.Length, token.TokenType,
                    "文字列・コメント・プリプロセッサをsemantic tokenが誤って上書きします。"));
                continue;
            }

            if (overlapping.Length > 0 && !IsCompatible(token.TokenType, overlapping))
                mismatches.Add(new CSharpSemanticTokenMismatch(
                    token.Line, token.StartChar, token.Length, token.TokenType,
                    "semantic tokenとC# fallbackの分類が一致しません。"));
        }
        return new CSharpSemanticTokenComparison(compared, mismatches);
    }

    private static bool IsSameProtectedKind(
        string semanticType, IReadOnlyList<SyntaxToken> fallback)
        => semanticType switch
        {
            "string" or "regexp" => fallback.Any(token => token.Kind == TokenKind.String),
            "comment" => fallback.Any(token => token.Kind == TokenKind.Comment),
            "macro" => fallback.Any(token => token.Kind == TokenKind.Preprocessor),
            _ => false,
        };

    private static bool IsCompatible(
        string semanticType, IReadOnlyList<SyntaxToken> fallback)
    {
        var kinds = fallback.Select(token => token.Kind).ToHashSet();
        return semanticType switch
        {
            "namespace" or "type" or "class" or "struct" or "enum" or "interface"
                or "typeParameter" => kinds.Contains(TokenKind.Type)
                    || kinds.Contains(TokenKind.Identifier) || kinds.Contains(TokenKind.Keyword),
            "function" or "method" => kinds.Contains(TokenKind.Function)
                    || kinds.Contains(TokenKind.Identifier) || kinds.Contains(TokenKind.Type),
            "keyword" or "modifier" => kinds.Contains(TokenKind.Keyword),
            "string" or "regexp" => kinds.Contains(TokenKind.String),
            "number" => kinds.Contains(TokenKind.Number),
            "decorator" or "attribute" => kinds.Contains(TokenKind.Attribute),
            // The fallback intentionally cannot distinguish fields, properties, parameters,
            // and locals without a semantic model. Its only contract here is to avoid protected
            // lexical ranges; EditorCanvas lets the semantic token win over the heuristic color.
            "variable" or "parameter" or "property" or "enumMember" or "event" =>
                kinds.Contains(TokenKind.Identifier) || kinds.Contains(TokenKind.Type)
                    || kinds.Contains(TokenKind.Function),
            "operator" => true,
            _ => true,
        };
    }
}

public sealed record CSharpSemanticTokenMismatch(
    int Line, int StartChar, int Length, string TokenType, string Message);

public sealed record CSharpSemanticTokenComparison(
    int ComparedTokens, IReadOnlyList<CSharpSemanticTokenMismatch> Mismatches)
{
    public bool IsCompatible => Mismatches.Count == 0;
}
