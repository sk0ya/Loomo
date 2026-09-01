using Editor.Core.Syntax;

namespace sk0ya.Loomo.CSharp.Editor;

/// <summary>LoomoのC#向けsyntax language。C#固有の字句判定をEditor本体から差し替え可能にする。</summary>
public sealed class CSharpSyntaxLanguage : ISyntaxLanguage
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char",
        "checked", "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "from",
        "get", "global", "goto", "group", "if", "implicit", "in", "init", "int", "interface", "internal",
        "into", "is", "join", "let", "lock", "long", "managed", "namespace", "new", "not", "null", "nint",
        "nuint", "object", "on", "operator", "or", "orderby", "out", "override", "params", "private",
        "protected", "public", "readonly", "record", "ref", "return", "sbyte", "scoped", "sealed", "select",
        "set", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
        "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "value", "var",
        "virtual", "void", "volatile", "when", "where", "while", "with", "yield", "required", "file",
        "ascending", "descending", "equals", "by", "dynamic", "nameof", "and", "unmanaged",
        // C# 13/14で追加・拡張されたcontextual keyword。構文fallbackでもLSP semantic tokenの
        // modifier／keyword範囲と大きくずれないよう、通常の識別子より先に扱う。
        "add", "alias", "allows", "args", "extension", "field", "partial", "remove",
    };

    public string Name => "C#";
    public string[] Extensions => [".cs"];
    public string? LineCommentPrefix => "//";
    public string? BlockCommentPrefix => "/*";
    public string? BlockCommentSuffix => "*/";

    public LineTokens[] Tokenize(string[] lines)
    {
        var result = new LineTokens[lines.Length];
        var blockComment = false;
        var stringState = StringState.None;
        var attributeDepth = 0;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var tokens = new List<SyntaxToken>();
            TokenizeLine(lines[lineIndex], tokens, ref blockComment, ref stringState, ref attributeDepth);
            result[lineIndex] = new LineTokens(lineIndex, [.. tokens]);
        }
        return result;
    }

    private static void TokenizeLine(string line, List<SyntaxToken> tokens,
        ref bool blockComment, ref StringState stringState, ref int attributeDepth)
    {
        var i = 0;
        while (i < line.Length)
        {
            if (attributeDepth > 0)
            {
                var end = ScanAttribute(line, i, attributeDepth, out var remainingDepth);
                if (end > i) tokens.Add(new SyntaxToken(i, end - i, TokenKind.Attribute));
                attributeDepth = remainingDepth;
                if (attributeDepth > 0) return;
                i = end;
                continue;
            }
            if (stringState != StringState.None)
            {
                if (stringState.IsInterpolated)
                {
                    var continuationEnd = TokenizeInterpolatedContinuation(
                        line, stringState, tokens, out stringState);
                    if (continuationEnd >= 0)
                    {
                        i = continuationEnd;
                        continue;
                    }
                    return;
                }
                // 継続行では引用符の開始位置がこの行に無い。検索開始位置を負の
                // quote長で補正し、行頭のraw/verbatim終端（""" や ")も見落とさない。
                var end = FindStringEnd(line,
                    stringState.IsRaw ? -stringState.RawQuotes : -1, stringState);
                if (end < 0) { tokens.Add(new SyntaxToken(0, line.Length, TokenKind.String)); return; }
                tokens.Add(new SyntaxToken(0, end, TokenKind.String));
                i = end;
                stringState = StringState.None;
                continue;
            }
            if (blockComment)
            {
                var end = line.IndexOf("*/", i, StringComparison.Ordinal);
                if (end < 0) { tokens.Add(new SyntaxToken(i, line.Length - i, TokenKind.Comment)); return; }
                tokens.Add(new SyntaxToken(i, end + 2 - i, TokenKind.Comment));
                i = end + 2;
                blockComment = false;
                continue;
            }
            if (i == 0 && line.TrimStart().StartsWith('#'))
            {
                tokens.Add(new SyntaxToken(0, line.Length, TokenKind.Preprocessor));
                return;
            }
            if (i + 1 < line.Length && line[i..].StartsWith("//", StringComparison.Ordinal))
            {
                if (IsDocumentationCommentStart(line, i))
                    TokenizeDocumentationComment(line, i, tokens);
                else
                    tokens.Add(new SyntaxToken(i, line.Length - i, TokenKind.Comment));
                return;
            }
            if (i + 1 < line.Length && line[i..].StartsWith("/*", StringComparison.Ordinal))
            {
                var end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    tokens.Add(new SyntaxToken(i, line.Length - i, TokenKind.Comment));
                    blockComment = true;
                    return;
                }
                tokens.Add(new SyntaxToken(i, end + 2 - i, TokenKind.Comment));
                i = end + 2;
                continue;
            }
            if (line[i] == '[' && IsAttributeStart(line, i))
            {
                var attributeEnd = ScanAttribute(line, i, 0, out attributeDepth);
                if (attributeEnd > i)
                    tokens.Add(new SyntaxToken(i, attributeEnd - i, TokenKind.Attribute));
                if (attributeDepth > 0) return;
                i = attributeEnd;
                continue;
            }
            if (TryStringStart(line, i, out var quote, out var state))
            {
                var end = FindStringEnd(line, quote, state);
                if (end < 0)
                {
                    if (state.IsInterpolated)
                    {
                        var close = TokenizeInterpolatedStringLine(
                            line, i, quote, state, tokens, out stringState);
                        if (close >= 0)
                        {
                            i = close;
                            continue;
                        }
                        return;
                    }
                    tokens.Add(new SyntaxToken(i, line.Length - i, TokenKind.String));
                    stringState = state;
                    return;
                }
                if (state.IsInterpolated)
                    TokenizeInterpolatedString(line, i, quote, end, state, tokens);
                else
                    tokens.Add(new SyntaxToken(i, end - i, TokenKind.String));
                i = end;
                continue;
            }
            if (line[i] == '\'')
            {
                var start = i++;
                while (i < line.Length && line[i] != '\'')
                {
                    if (line[i] == '\\' && i + 1 < line.Length) i++;
                    i++;
                }
                if (i < line.Length) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }
            if (char.IsDigit(line[i]) || (line[i] == '.' && i + 1 < line.Length && char.IsDigit(line[i + 1])))
            {
                var start = i++;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is '.' or '_')) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }
            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                var start = i++;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                var word = line[start..i];
                tokens.Add(new SyntaxToken(start, i - start,
                    Keywords.Contains(word) ? TokenKind.Keyword : SyntaxHeuristics.ClassifyIdentifier(line, start, i)));
                continue;
            }
            if (line[i] == '@' && i + 1 < line.Length
                && (char.IsLetter(line[i + 1]) || line[i + 1] == '_'))
            {
                var start = i++;
                i++;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                tokens.Add(new SyntaxToken(start, i - start,
                    ClassifyEscapedIdentifier(line, start, i)));
                continue;
            }
            i++;
        }
    }

    private static bool IsDocumentationCommentStart(string line, int start)
        => start + 3 <= line.Length && line.AsSpan(start, 3).SequenceEqual("///") &&
           (start + 3 == line.Length || line[start + 3] != '/');

    /// <summary>XML documentationのタグだけをAttributeとして着色し、本文とコメント記号は
    /// Commentのまま保持する。不完全な入力や本文中の比較記号は安全側でCommentに残す。</summary>
    private static void TokenizeDocumentationComment(
        string line, int start, List<SyntaxToken> tokens)
    {
        var segmentStart = start;
        var cursor = start + 3;
        while (cursor < line.Length)
        {
            if (line[cursor] != '<' || cursor + 1 >= line.Length ||
                !(char.IsLetter(line[cursor + 1]) || line[cursor + 1] is '/' or '!' or '?'))
            {
                cursor++;
                continue;
            }

            var close = FindDocumentationTagEnd(line, cursor + 1);
            if (close < 0)
            {
                cursor++;
                continue;
            }

            if (cursor > segmentStart)
                tokens.Add(new SyntaxToken(segmentStart, cursor - segmentStart, TokenKind.Comment));
            tokens.Add(new SyntaxToken(cursor, close + 1 - cursor, TokenKind.Attribute));
            segmentStart = close + 1;
            cursor = segmentStart;
        }

        if (segmentStart < line.Length)
            tokens.Add(new SyntaxToken(segmentStart, line.Length - segmentStart, TokenKind.Comment));
    }

    private static int FindDocumentationTagEnd(string line, int start)
    {
        var quote = '\0';
        for (var i = start; i < line.Length; i++)
        {
            var character = line[i];
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                continue;
            }
            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }
            if (character == '>') return i;
        }
        return -1;
    }

    private static TokenKind ClassifyEscapedIdentifier(string line, int start, int end)
    {
        var next = end;
        while (next < line.Length && line[next] is ' ' or '\t') next++;
        if (next < line.Length && line[next] == '(') return TokenKind.Function;
        var previous = start - 1;
        while (previous >= 0 && line[previous] is ' ' or '\t') previous--;
        if (previous >= 0 && line[previous] == '.') return TokenKind.Identifier;
        return start + 1 < line.Length && char.IsUpper(line[start + 1])
            ? TokenKind.Type : TokenKind.Identifier;
    }

    private static bool IsAttributeStart(string line, int start)
    {
        if (start != 0 && !string.IsNullOrWhiteSpace(line[..start])) return false;
        var next = start + 1;
        while (next < line.Length && line[next] is ' ' or '\t') next++;
        return next < line.Length && (char.IsLetter(line[next]) || line[next] is '_' or '@');
    }

    private static int ScanAttribute(string line, int start, int depth, out int remainingDepth)
    {
        var inString = false;
        var inChar = false;
        for (var i = start; i < line.Length; i++)
        {
            var c = line[i];
            if (inString)
            {
                if (c == '\\' && i + 1 < line.Length) { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (inChar)
            {
                if (c == '\\' && i + 1 < line.Length) { i++; continue; }
                if (c == '\'') inChar = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '\'') { inChar = true; continue; }
            if (c == '[') { depth++; continue; }
            if (c != ']' || --depth > 0) continue;

            remainingDepth = 0;
            return i + 1;
        }

        remainingDepth = depth;
        return line.Length;
    }

    private static bool TryStringStart(string line, int start, out int quote, out StringState state)
    {
        quote = start;
        state = StringState.None;
        var cursor = start;
        var dollarCount = 0;
        while (cursor < line.Length && line[cursor] is '$' or '@')
        {
            if (line[cursor] == '$') dollarCount++;
            cursor++;
        }
        if (cursor >= line.Length || line[cursor] != '"') return false;
        var count = 0;
        while (cursor + count < line.Length && line[cursor + count] == '"') count++;
        var interpolated = dollarCount > 0;
        if (count >= 3) state = StringState.Raw(count, interpolated ? dollarCount : 0);
        else if (line[start..cursor].Contains('@')) state = StringState.Verbatim(interpolated);
        else state = StringState.Regular(interpolated);
        quote = cursor;
        return true;
    }

    /// <summary>補間文字列の式部分だけを通常のC#字句として色付けする。
    /// 複数行の補間文字列は状態をまたぐため、継続行では安全側に全体をStringとして扱う。</summary>
    private static void TokenizeInterpolatedString(
        string line, int start, int quote, int end, StringState state,
        List<SyntaxToken> tokens)
    {
        var delimiterLength = state.IsRaw ? state.RawQuotes : 1;
        var contentStart = quote + delimiterLength;
        var contentEnd = end - delimiterLength;
        var segmentStart = start;
        var i = contentStart;
        while (i < contentEnd)
        {
            if (line[i] == '{' &&
                CountRun(line, i, '{') >= state.InterpolationBraces &&
                (state.InterpolationBraces > 1 || i + 1 >= contentEnd || line[i + 1] != '{') &&
                FindInterpolationEnd(line, i, contentEnd, state.InterpolationBraces) is var close && close >= 0)
            {
                var expression = line[(i + state.InterpolationBraces)..close];
                tokens.Add(new SyntaxToken(segmentStart,
                    i + state.InterpolationBraces - segmentStart, TokenKind.String));
                var expressionTokens = new List<SyntaxToken>();
                var blockComment = false;
                var expressionState = StringState.None;
                var attributeDepth = 0;
                TokenizeLine(expression, expressionTokens,
                    ref blockComment, ref expressionState, ref attributeDepth);
                tokens.AddRange(expressionTokens.Select(token => token with
                {
                    StartColumn = token.StartColumn + i + state.InterpolationBraces,
                }));
                segmentStart = close;
                i = close + 1;
                continue;
            }

            // {{ and }} are literal braces, not interpolation delimiters.
            i += line[i] is '{' or '}' && i + 1 < contentEnd && line[i + 1] == line[i] ? 2 : 1;
        }

        if (segmentStart < end)
            tokens.Add(new SyntaxToken(segmentStart, end - segmentStart, TokenKind.String));
    }

    /// <summary>行をまたぐinterpolated stringの継続部分を字句化する。通常のstring tokenを
    /// そのまま延長すると、別行の補間式まで文字列色になるため、補間の深度だけを状態として保持する。
    /// 文字列内のformat specifierや不完全入力は安全側でStringへ残す。</summary>
    private static int TokenizeInterpolatedContinuation(
        string line,
        StringState state,
        List<SyntaxToken> tokens,
        out StringState nextState)
        => TokenizeInterpolatedStringLine(line, 0, 0, state, tokens, out nextState,
            startsNewString: false);

    private static int TokenizeInterpolatedStringLine(
        string line,
        int start,
        int quote,
        StringState state,
        List<SyntaxToken> tokens,
        out StringState nextState,
        bool startsNewString = true)
    {
        var delimiterLength = state.IsRaw ? state.RawQuotes : 1;
        var cursor = state.InInterpolation
            ? 0
            : startsNewString ? quote + delimiterLength : 0;
        var segmentStart = startsNewString ? start : 0;

        if (state.InInterpolation)
        {
            var close = FindInterpolationClose(line, 0, state.InterpolationDepth,
                state.InterpolationBraces);
            if (close < 0)
            {
                TokenizeInterpolationExpression(line, 0, line.Length, tokens);
                nextState = state;
                return -1;
            }

            TokenizeInterpolationExpression(line, 0, close, tokens);
            segmentStart = close;
            cursor = close + 1;
            state = state with { InInterpolation = false, InterpolationDepth = 0 };
        }

        while (cursor < line.Length)
        {
            var special = FindInterpolatedStringSpecial(line, cursor, state);
            if (special.Kind == InterpolatedStringSpecialKind.OpenInterpolation)
            {
                tokens.Add(new SyntaxToken(segmentStart, special.End - segmentStart,
                    TokenKind.String));
                var close = FindInterpolationEnd(line, special.Position, line.Length,
                    state.InterpolationBraces);
                if (close < 0)
                {
                    TokenizeInterpolationExpression(line, special.End, line.Length, tokens);
                    nextState = state with { InInterpolation = true, InterpolationDepth =
                        UnclosedInterpolationDepth(line, special.Position + 1) };
                    return -1;
                }

                TokenizeInterpolationExpression(line, special.End, close, tokens);
                segmentStart = close;
                cursor = close + 1;
                continue;
            }

            if (special.Kind == InterpolatedStringSpecialKind.CloseString)
            {
                tokens.Add(new SyntaxToken(segmentStart, special.End - segmentStart, TokenKind.String));
                nextState = StringState.None;
                return special.End;
            }

            tokens.Add(new SyntaxToken(segmentStart, line.Length - segmentStart, TokenKind.String));
            nextState = state;
            return -1;
        }

        if (segmentStart < line.Length)
            tokens.Add(new SyntaxToken(segmentStart, line.Length - segmentStart, TokenKind.String));
        nextState = state;
        return -1;
    }

    private static void TokenizeInterpolationExpression(
        string line, int start, int end, List<SyntaxToken> tokens)
    {
        if (end <= start) return;
        var expressionTokens = new List<SyntaxToken>();
        var blockComment = false;
        var expressionState = StringState.None;
        var attributeDepth = 0;
        TokenizeLine(line[start..end], expressionTokens,
            ref blockComment, ref expressionState, ref attributeDepth);
        tokens.AddRange(expressionTokens.Select(token => token with
        {
            StartColumn = token.StartColumn + start,
        }));
    }

    private static InterpolatedStringSpecial FindInterpolatedStringSpecial(
        string line, int start, StringState state)
    {
        var closing = state.IsRaw ? new string('"', state.RawQuotes) : "\"";
        for (var i = start; i < line.Length; i++)
        {
            if (state.InterpolationBraces == 1 &&
                line[i] == '{' && i + 1 < line.Length && line[i + 1] == '{')
            {
                i++;
                continue;
            }
            if (state.InterpolationBraces == 1 &&
                line[i] == '}' && i + 1 < line.Length && line[i + 1] == '}')
            {
                i++;
                continue;
            }
            if (line[i] == '"')
            {
                if (!state.IsRaw && state.Kind == "verbatim" && i + 1 < line.Length && line[i + 1] == '"')
                {
                    i++;
                    continue;
                }
                if (!state.IsRaw && state.Kind != "verbatim" && i > start && line[i - 1] == '\\')
                    continue;
                if (line[i..].StartsWith(closing, StringComparison.Ordinal))
                    return new(InterpolatedStringSpecialKind.CloseString, i, i + closing.Length);
            }
            if (line[i] == '{' && CountRun(line, i, '{') >= state.InterpolationBraces)
                return new(InterpolatedStringSpecialKind.OpenInterpolation, i,
                    i + state.InterpolationBraces);
        }
        return new(InterpolatedStringSpecialKind.None, line.Length, line.Length);
    }

    private static int UnclosedInterpolationDepth(string line, int start)
    {
        var depth = 1;
        for (var i = start; i < line.Length; i++)
        {
            if (line[i] is '"' or '\'')
            {
                var quote = line[i++];
                while (i < line.Length)
                {
                    if (quote == '"' && line[i] == '\\' && i + 1 < line.Length) { i += 2; continue; }
                    if (line[i] == quote) break;
                    i++;
                }
                continue;
            }
            if (line[i] == '{') depth++;
            else if (line[i] == '}') depth--;
        }
        return Math.Max(1, depth);
    }

    private static int FindInterpolationClose(string line, int start, int depth, int closeBraceCount = 1)
        => FindInterpolationEndCore(line, start, Math.Max(0, depth - 1), line.Length, closeBraceCount);

    private static int FindInterpolationEnd(string line, int open, int contentEnd, int openBraceCount = 1)
        => FindInterpolationEndCore(line, open + openBraceCount, 0, contentEnd, openBraceCount);

    private static int FindInterpolationEndCore(
        string line, int start, int depth, int contentEnd, int closeBraceCount = 1)
    {
        for (var i = start; i < contentEnd; i++)
        {
            if (line[i] is '"' or '\'')
            {
                var quote = line[i];
                i++;
                while (i < contentEnd)
                {
                    if (quote == '"' && line[i] == '\\' && i + 1 < contentEnd) { i += 2; continue; }
                    if (line[i] == quote) break;
                    i++;
                }
                continue;
            }
            if (line[i] == '{')
            {
                var count = CountRun(line, i, '{');
                depth += count;
                i += count - 1;
                continue;
            }
            if (line[i] == '}')
            {
                var count = CountRun(line, i, '}');
                if (depth == 0 && count >= closeBraceCount) return i;

                var consumed = Math.Min(depth, count);
                depth -= consumed;
                if (depth == 0 && count - consumed >= closeBraceCount)
                    return i + consumed;
                i += count - 1;
            }
        }
        return -1;
    }

    private static int CountRun(string line, int start, char character)
    {
        var end = start;
        while (end < line.Length && line[end] == character) end++;
        return end - start;
    }

    private static int FindStringEnd(string line, int quote, StringState state)
    {
        if (state.IsRaw)
        {
            var needle = new string('"', state.RawQuotes);
            var end = line.IndexOf(needle, quote + state.RawQuotes, StringComparison.Ordinal);
            return end < 0 ? -1 : end + state.RawQuotes;
        }
        var verbatim = state.Kind == "verbatim";
        for (var i = quote + 1; i < line.Length; i++)
        {
            if (verbatim && line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"') { i++; continue; }
            if (!verbatim && line[i] == '\\') { i++; continue; }
            if (line[i] == '"') return i + 1;
        }
        return -1;
    }

    private readonly record struct StringState(
        string Kind,
        int RawQuotes,
        bool IsInterpolated,
        int InterpolationBraces = 0,
        bool InInterpolation = false,
        int InterpolationDepth = 0)
    {
        public static StringState None => new("", 0, false, 0, false, 0);
        public static StringState Regular(bool interpolated = false) => new("regular", 0, interpolated,
            interpolated ? 1 : 0);
        public static StringState Verbatim(bool interpolated = false) => new("verbatim", 0, interpolated,
            interpolated ? 1 : 0);
        public static StringState Raw(int quotes, int interpolationBraces = 0)
            => new("raw", quotes, interpolationBraces > 0, interpolationBraces);
        public bool IsRaw => RawQuotes > 0;
    }

    private readonly record struct InterpolatedStringSpecial(
        InterpolatedStringSpecialKind Kind, int Position, int End);

    private enum InterpolatedStringSpecialKind
    {
        None,
        OpenInterpolation,
        CloseString,
    }
}
