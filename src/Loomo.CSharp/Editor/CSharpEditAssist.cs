using Editor.Core.Editing;
using Editor.Core.Models;
using Editor.Core.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace sk0ya.Loomo.CSharp.Editor;

/// <summary>C#の波括弧内でEnterしたときのsmart indentと空ブロック整形。</summary>
public sealed class CSharpEditAssist : EditAssistBase
{
    public override bool AppliesTo(string? filePath)
        => string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase);

    public override EditResult OnEnter(EditContext ctx)
    {
        var line = ctx.Buffer.GetLine(ctx.Cursor.Line);
        var column = Math.Clamp(ctx.Cursor.Column, 0, line.Length);
        var before = line[..column];
        var after = line[column..];
        if (IsProtected(ctx, column)) return EditResult.NotHandled;
        if (before.TrimStart().StartsWith("//", StringComparison.Ordinal)
            || before.TrimStart().StartsWith("/*", StringComparison.Ordinal))
            return EditResult.NotHandled;

        var indent = LeadingWhitespace(line);
        var unit = ctx.ExpandTab ? new string(' ', Math.Max(1, ctx.ShiftWidth)) : "\t";
        if (before.TrimEnd().EndsWith('{'))
        {
            ctx.Buffer.BreakLine(ctx.Cursor.Line, column);
            var newLine = ctx.Cursor.Line + 1;
            var closing = after.TrimStart();
            if (closing == "}")
            {
                ctx.Buffer.ReplaceLine(newLine, indent + "}");
                ctx.Buffer.InsertText(newLine, 0, "\n");
                ctx.Buffer.ReplaceLine(newLine, indent + unit);
            }
            else
            {
                ctx.Buffer.ReplaceLine(newLine, indent + unit + after.TrimStart());
            }
            return EditResult.Done(new CursorPosition(newLine, (indent + unit).Length));
        }

        // Enter immediately before a closing brace creates an empty body line and
        // realigns the brace to the structural level after it. This also handles
        // `} else {` without counting braces inside strings or comments.
        if (after.TrimStart().StartsWith("}", StringComparison.Ordinal))
        {
            var depthBefore = BraceDepth(ctx, ctx.Cursor.Line, column);
            var bodyIndent = IndentForDepth(depthBefore, unit);
            var closingIndent = IndentForDepth(Math.Max(0, depthBefore - 1), unit);
            ctx.Buffer.BreakLine(ctx.Cursor.Line, column);
            var closingLine = ctx.Cursor.Line + 1;
            ctx.Buffer.ReplaceLine(closingLine, closingIndent + after.TrimStart());
            var bodyLine = ctx.Cursor.Line;
            if (string.IsNullOrWhiteSpace(before))
                ctx.Buffer.ReplaceLine(ctx.Cursor.Line, bodyIndent);
            else
            {
                ctx.Buffer.InsertLines(ctx.Cursor.Line, [bodyIndent]);
                bodyLine++;
            }
            return EditResult.Done(new CursorPosition(bodyLine, bodyIndent.Length));
        }

        // A switch arm has an implicit body indentation even though it has no brace.
        if (IsSwitchLabel(before))
        {
            ctx.Buffer.BreakLine(ctx.Cursor.Line, column);
            var newLine = ctx.Cursor.Line + 1;
            ctx.Buffer.ReplaceLine(newLine, indent + unit + after.TrimStart());
            return EditResult.Done(new CursorPosition(newLine, (indent + unit).Length));
        }

        var structuralIndent = IndentForDepth(BraceDepth(ctx, ctx.Cursor.Line, column), unit);
        if (string.Equals(structuralIndent, indent, StringComparison.Ordinal))
            return EditResult.NotHandled;

        // Keep the engine's normal newline path when no structural correction is
        // needed; only normalize a line whose brace depth disagrees with its prefix.
        ctx.Buffer.BreakLine(ctx.Cursor.Line, column);
        var correctedLine = ctx.Cursor.Line + 1;
        ctx.Buffer.ReplaceLine(correctedLine, structuralIndent + after.TrimStart());
        return EditResult.Done(new CursorPosition(correctedLine, structuralIndent.Length));
    }

    /// <summary>
    /// C#の安全な文だけを明示的に完了する。式文、ローカル宣言、return／throw等は
    /// Roslynの構文木で確認して末尾のセミコロンを補うが、ifやメソッド宣言などの
    /// ブロック構文は変更しない。末尾コメントはコメントの前へ挿入する。
    /// </summary>
#if LOOMO_EDITOR_STATEMENT_COMPLETION
    public override EditResult OnCompleteStatement(EditContext ctx)
    {
        var line = ctx.Buffer.GetLine(ctx.Cursor.Line);
        var column = Math.Clamp(ctx.Cursor.Column, 0, line.Length);
        var tokens = new CSharpSyntaxLanguage().Tokenize([line])[0].Tokens;
        if (IsProtected(ctx, column) && !HasTrailingCommentWithCode(line, column, tokens))
            return EditResult.NotHandled;
        var comment = tokens.FirstOrDefault(token => token.Kind == TokenKind.Comment &&
            token.StartColumn <= column);
        var hasComment = tokens.Any(token => token.Kind == TokenKind.Comment &&
            token.StartColumn <= column);
        var statementEnd = column;
        if (hasComment)
        {
            var commentEnd = comment.StartColumn + comment.Length;
            if (comment.StartColumn < column && line[commentEnd..].Trim().Length > 0)
                return EditResult.NotHandled;
            statementEnd = Math.Min(statementEnd, comment.StartColumn);
        }
        else if (!string.IsNullOrWhiteSpace(line[column..]))
        {
            return EditResult.NotHandled;
        }

        while (statementEnd > 0 && char.IsWhiteSpace(line[statementEnd - 1])) statementEnd--;
        if (statementEnd == 0) return EditResult.NotHandled;
        var statementText = line[..statementEnd];
        if (statementText.EndsWith(';') || statementText.EndsWith('}') ||
            statementText.EndsWith('{') || statementText.EndsWith(':'))
            return EditResult.NotHandled;

        var root = CSharpSyntaxTree.ParseText(statementText + ";").GetCompilationUnitRoot();
        var statement = root.Members.OfType<GlobalStatementSyntax>().SingleOrDefault()?.Statement;
        if (statement is not (ExpressionStatementSyntax or LocalDeclarationStatementSyntax or
            ReturnStatementSyntax or ThrowStatementSyntax or BreakStatementSyntax or
            ContinueStatementSyntax or YieldStatementSyntax))
            return EditResult.NotHandled;

        ctx.Buffer.InsertChar(ctx.Cursor.Line, statementEnd, ';');
        var newColumn = column >= statementEnd ? column + 1 : column;
        return EditResult.Done(ctx.Cursor with { Column = newColumn });
    }
#endif

    private static bool HasTrailingCommentWithCode(string line, int column, SyntaxToken[] tokens)
    {
        var comment = tokens.FirstOrDefault(token => token.Kind == TokenKind.Comment &&
            token.StartColumn <= column);
        if (comment.Length == 0 || string.IsNullOrWhiteSpace(line[..comment.StartColumn])) return false;
        var commentEnd = Math.Min(line.Length, comment.StartColumn + comment.Length);
        return string.IsNullOrWhiteSpace(line[commentEnd..]);
    }

    public override string? OpenLinePrefix(EditContext ctx, bool above)
    {
        var line = ctx.Buffer.GetLine(ctx.Cursor.Line);
        var column = Math.Clamp(ctx.Cursor.Column, 0, line.Length);
        if (IsProtected(ctx, column)) return null;
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith("/*", StringComparison.Ordinal))
            return null;

        var unit = ctx.ExpandTab ? new string(' ', Math.Max(1, ctx.ShiftWidth)) : "\t";
        var depth = BraceDepth(ctx, ctx.Cursor.Line, above ? 0 : line.Length);
        if (!above && IsSwitchLabel(line)) depth++;
        return IndentForDepth(depth, unit);
    }

    /// <summary>C#のコード位置で括弧・引用符を閉じる。文字列・コメント・プリプロセッサ内は
    /// 字句判定を優先して通常入力へ戻し、既にある閉じ文字は二重入力せずcaretだけ進める。</summary>
    public override EditResult OnChar(EditContext ctx, char typed)
    {
        if (typed is not ('{' or '}' or '(' or ')' or '[' or ']' or '"' or '\''))
            return EditResult.NotHandled;

        var line = ctx.Buffer.GetLine(ctx.Cursor.Line);
        var column = Math.Clamp(ctx.Cursor.Column, 0, line.Length);
        if (IsProtected(ctx, column)) return EditResult.NotHandled;

        if (IsClosing(typed) && column < line.Length && line[column] == typed)
            return EditResult.Done(ctx.Cursor with { Column = column + 1 });

        if (IsOpening(typed))
        {
            var closing = ClosingCharacter(typed);
            ctx.Buffer.InsertChar(ctx.Cursor.Line, column, typed);
            ctx.Buffer.InsertChar(ctx.Cursor.Line, column + 1, closing);
            return EditResult.Done(ctx.Cursor with { Column = column + 1 });
        }

        return EditResult.NotHandled;
    }

    private static bool IsProtected(EditContext ctx, int column)
    {
        var lineTokens = new CSharpSyntaxLanguage().Tokenize(
            ctx.Buffer.GetLines(0, Math.Max(0, ctx.Buffer.LineCount - 1)));
        if (ctx.Cursor.Line < 0 || ctx.Cursor.Line >= lineTokens.Length) return true;
        var line = ctx.Buffer.GetLine(ctx.Cursor.Line);
        return lineTokens[ctx.Cursor.Line].Tokens.Any(token =>
            column >= token.StartColumn &&
            (column < token.StartColumn + token.Length ||
             IsUnterminatedProtectedToken(line, token, column)) &&
            token.Kind is TokenKind.String or TokenKind.Comment or TokenKind.Preprocessor);
    }

    private static int BraceDepth(EditContext ctx, int throughLine, int throughColumn)
    {
        var lines = ctx.Buffer.GetLines(0, Math.Max(0, ctx.Buffer.LineCount - 1));
        var tokenLines = new CSharpSyntaxLanguage().Tokenize(lines);
        var depth = 0;
        for (var lineIndex = 0; lineIndex <= throughLine && lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var end = lineIndex == throughLine ? Math.Clamp(throughColumn, 0, line.Length) : line.Length;
            var tokens = tokenLines[lineIndex].Tokens;
            for (var column = 0; column < end; column++)
            {
                if (tokens.Any(token => column >= token.StartColumn &&
                    column < token.StartColumn + token.Length &&
                    token.Kind is TokenKind.String or TokenKind.Comment or TokenKind.Preprocessor))
                    continue;

                if (line[column] == '{') depth++;
                else if (line[column] == '}') depth = Math.Max(0, depth - 1);
            }
        }

        return depth;
    }

    private static string IndentForDepth(int depth, string unit)
        => string.Concat(Enumerable.Repeat(unit, Math.Max(0, depth)));

    private static bool IsSwitchLabel(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.EndsWith(":", StringComparison.Ordinal)) return false;
        return trimmed.StartsWith("case ", StringComparison.Ordinal) ||
            trimmed.StartsWith("case\t", StringComparison.Ordinal) ||
            string.Equals(trimmed, "default:", StringComparison.Ordinal);
    }

    private static bool IsUnterminatedProtectedToken(string line, SyntaxToken token, int column)
    {
        if (column != token.StartColumn + token.Length) return false;
        if (token.Kind == TokenKind.Preprocessor) return true;
        if (token.Kind != TokenKind.Comment) return false;
        var comment = line[token.StartColumn..Math.Min(column, line.Length)].TrimStart();
        return comment.StartsWith("//", StringComparison.Ordinal) ||
            (comment.StartsWith("/*", StringComparison.Ordinal) &&
             !comment.Contains("*/", StringComparison.Ordinal));
    }

    private static bool IsOpening(char value) => value is '{' or '(' or '[' or '"' or '\'';

    private static bool IsClosing(char value) => value is '}' or ')' or ']' or '"' or '\'';

    private static char ClosingCharacter(char value) => value switch
    {
        '{' => '}',
        '(' => ')',
        '[' => ']',
        '"' => '"',
        '\'' => '\'',
        _ => value,
    };

    private static string LeadingWhitespace(string line)
    {
        var length = 0;
        while (length < line.Length && line[length] is ' ' or '\t') length++;
        return line[..length];
    }
}
