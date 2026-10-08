using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace sk0ya.Loomo.CSharp.Editor;

/// <summary>構文木のどの並びを動かすか。Rider の「Move Statement」と「Move Element」の区別に倣う。</summary>
public enum StructuralListKind
{
    /// <summary>行で並ぶもの：文・メンバー・switch のセクション・using・アクセサー。上下に動かす。</summary>
    Lines,

    /// <summary>カンマで並ぶもの：引数・パラメーター・型引数・初期化子の要素・enum メンバーなど。左右に動かす。</summary>
    Separated,
}

/// <summary>構文木編集の結果。<see cref="Edits"/> が null なら <see cref="Error"/> に理由がある。
/// <see cref="Selection"/> は適用<b>後</b>の本文での位置（削除では始点＝終点のキャレット位置）。</summary>
public sealed record StructuralEditResult(
    IReadOnlyList<LspTextEdit>? Edits,
    LspRange? Selection,
    string? Summary,
    string? Error)
{
    internal static StructuralEditResult Fail(string error) => new(null, null, null, error);
}

/// <summary>
/// 構文木を単位にした編集（設計書 §35.2）。テキストの行や字ではなく「この引数」「この文」「このメソッド」を
/// 掴んで、兄弟と入れ替える・区切りごと消す。1 ファイルの構文だけを見るので意味解析もプロジェクトも要らず、
/// 結果は 1 回の undo になる TextEdit 群として返す（適用はホスト）。
/// <para>対象の決め方：選択があればそれを含む最も内側の並びの要素、無ければキャレット位置の最も内側の要素。
/// 選択を §24.9 の「意味的に広げる」で育ててから動かせば、引数→文→メソッドと狙う段を選べる。</para>
/// <para>守ること：(1) 適用後に再パースし、<b>構文エラーを新しく増やす</b>編集は返さない。
/// (2) <c>#region</c>／<c>#if</c> などのディレクティブをまたぐ入れ替えはしない（条件付きコンパイルの意味が変わる）。
/// (3) 動かせないときは理由を返す（黙って何もしない、を作らない）。</para>
/// </summary>
public static class CSharpStructuralEditing
{
    public static StructuralEditResult Move(
        string text, LspRange? selection, LspPosition caret,
        StructuralListKind kind, bool forward)
    {
        var context = Parse(text);
        if (!context.TryFindTarget(selection, caret, kind, out var target))
            return StructuralEditResult.Fail(kind == StructuralListKind.Lines
                ? "ここには上下に動かせる文・メンバーがありません。"
                : "ここには左右に動かせる要素（引数・パラメーターなど）がありません。");

        var index = target.Index;
        var siblingIndex = forward ? index + 1 : index - 1;
        if (siblingIndex < 0 || siblingIndex >= target.Siblings.Count)
            return StructuralEditResult.Fail(
                $"{Describe(target.Node)}はこれ以上{Direction(kind, forward)}へ動かせません。");

        var sibling = target.Siblings[siblingIndex];
        var (first, second) = forward ? (target.Node, sibling) : (sibling, target.Node);
        var (firstSpan, secondSpan) = context.SwapSpans(first, second, kind);
        if (context.HasDirectiveBetween(TextSpan.FromBounds(firstSpan.Start, secondSpan.End)))
            return StructuralEditResult.Fail(
                "プリプロセッサディレクティブ（#region／#if など）をまたぐ入れ替えはしません。");

        var firstText = text.Substring(firstSpan.Start, firstSpan.Length);
        var secondText = text.Substring(secondSpan.Start, secondSpan.Length);
        var edits = new[]
        {
            new LspTextEdit(context.ToRange(firstSpan), secondText),
            new LspTextEdit(context.ToRange(secondSpan), firstText),
        };

        // 動かした要素の、入れ替え後の位置。前の塊が後ろの塊の長さに置き換わるぶんだけ後ろがずれる。
        var movedBlock = forward ? firstSpan : secondSpan;
        var movedBlockStart = forward
            ? secondSpan.Start + secondSpan.Length - firstSpan.Length
            : firstSpan.Start;
        var movedStart = movedBlockStart + (target.Node.SpanStart - movedBlock.Start);
        return context.Finish(
            edits, TextSpan.FromBounds(movedStart, movedStart + target.Node.Span.Length),
            $"{Describe(target.Node)}を{Direction(kind, forward)}へ移動しました。");
    }

    /// <summary>キャレット（または選択）の要素を、区切りや行ごと削除する。
    /// 並びの種類は問わず、最も内側の要素を選ぶ（引数の上なら引数、文の頭なら文）。</summary>
    public static StructuralEditResult Delete(string text, LspRange? selection, LspPosition caret)
    {
        var context = Parse(text);
        if (!context.TryFindTarget(selection, caret, kind: null, out var target))
            return StructuralEditResult.Fail("ここには削除できる構文要素（文・メンバー・引数など）がありません。");

        var span = target.Kind == StructuralListKind.Separated
            ? context.SeparatedDeleteSpan(target)
            : context.LineDeleteSpan(target.Node);
        if (context.HasDirectiveBetween(span))
            return StructuralEditResult.Fail(
                "プリプロセッサディレクティブ（#region／#if など）を含む範囲は削除しません。");

        return context.Finish(
            [new LspTextEdit(context.ToRange(span), "")],
            new TextSpan(span.Start, 0),
            $"{Describe(target.Node)}を削除しました。");
    }

    private static string Direction(StructuralListKind kind, bool forward)
        => (kind, forward) switch
        {
            (StructuralListKind.Lines, true) => "下",
            (StructuralListKind.Lines, false) => "上",
            (_, true) => "右",
            _ => "左",
        };

    /// <summary>状態行に出す名前。分からない種類は「要素」「文」「メンバー」に丸める（推測で名乗らない）。</summary>
    internal static string Describe(SyntaxNode node) => node switch
    {
        ArgumentSyntax or AttributeArgumentSyntax => "引数",
        ParameterSyntax => "パラメーター",
        TypeParameterSyntax => "型パラメーター",
        EnumMemberDeclarationSyntax => "enum メンバー",
        AttributeSyntax => "属性",
        BaseTypeSyntax => "基底型",
        VariableDeclaratorSyntax => "変数宣言子",
        SwitchExpressionArmSyntax => "switch 式の分岐",
        UsingDirectiveSyntax => "using ディレクティブ",
        SwitchSectionSyntax => "switch セクション",
        AccessorDeclarationSyntax => "アクセサー",
        MethodDeclarationSyntax => "メソッド",
        ConstructorDeclarationSyntax => "コンストラクター",
        PropertyDeclarationSyntax => "プロパティ",
        FieldDeclarationSyntax => "フィールド",
        EventDeclarationSyntax or EventFieldDeclarationSyntax => "イベント",
        BaseTypeDeclarationSyntax or DelegateDeclarationSyntax => "型",
        MemberDeclarationSyntax => "メンバー",
        StatementSyntax => "文",
        TypeSyntax { Parent: TypeArgumentListSyntax } => "型引数",
        _ => "要素",
    };

    private static EditingContext Parse(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        return new EditingContext(text, tree.GetText(), tree.GetRoot());
    }

    private readonly record struct Target(
        SyntaxNode Node, IReadOnlyList<SyntaxNode> Siblings, int Index, StructuralListKind Kind);

    private sealed class EditingContext(string text, SourceText source, SyntaxNode root)
    {
        public LspRange ToRange(TextSpan span)
            => new(ToPosition(source, span.Start), ToPosition(source, span.End));

        public bool HasDirectiveBetween(TextSpan span)
            => root.DescendantTrivia(span, descendIntoTrivia: true)
                .Any(trivia => trivia.IsDirective && span.Contains(trivia.SpanStart));

        /// <summary>適用後の本文を作り、構文エラーが増えていないことを確かめてから結果にする。</summary>
        public StructuralEditResult Finish(IReadOnlyList<LspTextEdit> edits, TextSpan selection, string summary)
        {
            var changes = edits.Select(edit => new TextChange(
                TextSpan.FromBounds(ToOffset(source, edit.Range.Start), ToOffset(source, edit.Range.End)),
                edit.NewText));
            var updated = source.WithChanges(changes);
            var after = CSharpSyntaxTree.ParseText(updated);
            if (ErrorCount(after.GetDiagnostics()) > ErrorCount(root.SyntaxTree.GetDiagnostics()))
                return StructuralEditResult.Fail("この編集は構文エラーを生むため中止しました。");

            var range = new LspRange(
                ToPosition(updated, selection.Start), ToPosition(updated, selection.End));
            return new StructuralEditResult(edits, range, summary, null);
        }

        public bool TryFindTarget(
            LspRange? selection, LspPosition caret, StructuralListKind? kind, out Target target)
        {
            target = default;
            SyntaxNode? start;
            TextSpan? selected = null;
            if (selection is not null && ToOffset(source, selection.Start) is var s &&
                ToOffset(source, selection.End) is var e && e > s)
            {
                selected = TextSpan.FromBounds(s, e);
                start = root.FindNode(selected.Value, getInnermostNodeForTie: true);
            }
            else
            {
                start = TokenAtCaret(ToOffset(source, caret)).Parent;
            }

            for (var node = start; node is not null; node = node.Parent)
            {
                if (selected is { } span && !node.Span.Contains(TrimToContent(span)))
                    continue;
                if (kind is null or StructuralListKind.Lines && LineSiblings(node) is { } lines)
                {
                    target = new Target(node, lines, IndexOf(lines, node), StructuralListKind.Lines);
                    return true;
                }
                if (kind is null or StructuralListKind.Separated && SeparatedSiblings(node) is { } items)
                {
                    target = new Target(node, items, IndexOf(items, node), StructuralListKind.Separated);
                    return true;
                }
            }
            return false;
        }

        /// <summary>選択の前後の空白は対象の判定に含めない（行選択や前後に余白を含む選択でも掴めるように）。</summary>
        private TextSpan TrimToContent(TextSpan span)
        {
            var start = span.Start;
            var end = span.End;
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            return TextSpan.FromBounds(start, end);
        }

        /// <summary>キャレットの直前で終わるトークンを優先する。<c>Foo(a|, b)</c> の a、行末の <c>;|</c> の文。</summary>
        private SyntaxToken TokenAtCaret(int offset)
        {
            var token = root.FindToken(Math.Min(offset, Math.Max(0, text.Length - 1)));
            if (offset <= 0) return token;
            var previous = root.FindToken(offset - 1);
            var touchesPrevious = previous.Span.End == offset;
            var insideToken = token.Span.Start < offset && offset < token.Span.End;
            if (touchesPrevious && !insideToken &&
                (token.Span.Start != offset || IsPunctuation(token)))
                return previous;
            return token;
        }

        private static bool IsPunctuation(SyntaxToken token)
            => token.IsKind(SyntaxKind.CommaToken) || token.IsKind(SyntaxKind.CloseParenToken) ||
               token.IsKind(SyntaxKind.CloseBracketToken) || token.IsKind(SyntaxKind.CloseBraceToken) ||
               token.IsKind(SyntaxKind.SemicolonToken) || token.IsKind(SyntaxKind.GreaterThanToken) ||
               token.IsKind(SyntaxKind.EndOfFileToken);

        private static int IndexOf(IReadOnlyList<SyntaxNode> list, SyntaxNode node)
        {
            for (var i = 0; i < list.Count; i++)
                if (list[i].RawKind == node.RawKind && list[i].Span == node.Span) return i;
            return -1;
        }

        /// <summary>行で並ぶ兄弟。埋め込み文（<c>if (x) Foo();</c> の Foo()）は並びではないので親へ上がる。</summary>
        private static IReadOnlyList<SyntaxNode>? LineSiblings(SyntaxNode node) => (node, node.Parent) switch
        {
            (StatementSyntax, BlockSyntax block) => block.Statements,
            (StatementSyntax, SwitchSectionSyntax section) => section.Statements,
            (SwitchSectionSyntax, SwitchStatementSyntax @switch) => @switch.Sections,
            (UsingDirectiveSyntax, CompilationUnitSyntax unit) => unit.Usings,
            (UsingDirectiveSyntax, BaseNamespaceDeclarationSyntax ns) => ns.Usings,
            (AccessorDeclarationSyntax, AccessorListSyntax accessors) => accessors.Accessors,
            (MemberDeclarationSyntax, TypeDeclarationSyntax type) => type.Members,
            (MemberDeclarationSyntax, BaseNamespaceDeclarationSyntax ns) => ns.Members,
            (MemberDeclarationSyntax, CompilationUnitSyntax unit) => unit.Members,
            _ => null,
        };

        /// <summary>カンマで並ぶ兄弟。構文の種類を列挙せず「親の子の並びでカンマに隣接している」で判定するので、
        /// 引数・パラメーター・型引数・初期化子・タプル・パターン・基底型・属性などを一律に扱える。
        /// 要素が1つだけの並び（<c>Foo(a)</c>）は動かす相手がいないので、外側の並びへ上がる。</summary>
        private static IReadOnlyList<SyntaxNode>? SeparatedSiblings(SyntaxNode node)
        {
            if (node.Parent is not { } parent) return null;
            var children = parent.ChildNodesAndTokens();
            var index = -1;
            for (var i = 0; i < children.Count; i++)
                if (children[i].AsNode() == node) { index = i; break; }
            if (index < 0) return null;

            bool IsComma(int i) => i >= 0 && i < children.Count && children[i].IsKind(SyntaxKind.CommaToken);
            if (!IsComma(index - 1) && !IsComma(index + 1)) return null;

            var first = index;
            while (IsComma(first - 1) && first - 2 >= 0 && children[first - 2].IsNode) first -= 2;
            var list = new List<SyntaxNode>();
            for (var i = first; i < children.Count && children[i].AsNode() is { } element; i += 2)
            {
                list.Add(element);
                if (!IsComma(i + 1)) break;
            }
            return list.Count > 1 ? list : null;
        }

        /// <summary>入れ替える2つの塊。行の並びで両方が行を独占しているなら、直前に付いたコメント
        /// （<c>///</c> を含む）と行末コメントも連れて行の単位で動かす。どちらかが他の文と同じ行にあるなら、
        /// 字下げが壊れないよう構文の範囲だけを入れ替える。</summary>
        public (TextSpan First, TextSpan Second) SwapSpans(
            SyntaxNode first, SyntaxNode second, StructuralListKind kind)
        {
            if (kind == StructuralListKind.Lines &&
                LineBlock(first) is { } a && LineBlock(second) is { } b)
                return (a, b);
            return (first.Span, second.Span);
        }

        /// <summary>要素が行を独占していれば、付随コメント込みの「行頭〜行末（改行の手前）」を返す。</summary>
        private TextSpan? LineBlock(SyntaxNode node)
        {
            var firstLine = source.Lines.GetLineFromPosition(node.SpanStart);
            var lastLine = source.Lines.GetLineFromPosition(node.Span.End);
            var before = text[firstLine.Start..node.SpanStart];
            var after = text[node.Span.End..lastLine.End];
            if (!string.IsNullOrWhiteSpace(before)) return null;
            var rest = after.TrimStart();
            if (rest.Length > 0 && !rest.StartsWith("//", StringComparison.Ordinal)) return null;

            var startLine = firstLine.LineNumber;
            var fullStartLine = source.Lines.GetLineFromPosition(node.FullSpan.Start).LineNumber;
            var commentLines = CommentLines(node);
            while (startLine - 1 >= fullStartLine && commentLines.Contains(startLine - 1))
                startLine--;
            return TextSpan.FromBounds(source.Lines[startLine].Start, lastLine.End);
        }

        /// <summary>要素の前置トリビアにあるコメントが占める行。空行を挟んだ先のコメントは別物として扱う。</summary>
        private HashSet<int> CommentLines(SyntaxNode node)
        {
            var lines = new HashSet<int>();
            foreach (var trivia in node.GetLeadingTrivia())
            {
                if (!(trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                      trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                      trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                      trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)))
                    continue;
                var from = source.Lines.GetLineFromPosition(trivia.SpanStart).LineNumber;
                var to = source.Lines.GetLineFromPosition(Math.Max(trivia.SpanStart, trivia.Span.End - 1)).LineNumber;
                for (var line = from; line <= to; line++) lines.Add(line);
            }
            return lines;
        }

        /// <summary>カンマで並ぶ要素を区切りごと消す。後ろに要素があれば「要素, 」を、
        /// 末尾なら「, 要素」を消す（複数行に並んだ引数でも改行と字下げが崩れない）。</summary>
        public TextSpan SeparatedDeleteSpan(Target target)
        {
            var node = target.Node;
            if (target.Index + 1 < target.Siblings.Count)
                return TextSpan.FromBounds(node.SpanStart, target.Siblings[target.Index + 1].SpanStart);
            return TextSpan.FromBounds(target.Siblings[target.Index - 1].Span.End, node.Span.End);
        }

        /// <summary>行の並びの要素を、付随コメントと改行ごと消す。前後が空行なら空行が2つ続かないよう1つ詰め、
        /// 型の最後のメンバーなら閉じ括弧の前に空行を残さない。</summary>
        public TextSpan LineDeleteSpan(SyntaxNode node)
        {
            if (LineBlock(node) is not { } block)
            {
                var end = node.Span.End;
                while (end < text.Length && text[end] is ' ' or '\t') end++;
                return TextSpan.FromBounds(node.SpanStart, end);
            }

            var startLine = source.Lines.GetLineFromPosition(block.Start).LineNumber;
            var endLine = source.Lines.GetLineFromPosition(block.End).LineNumber;
            var start = block.Start;
            var stop = source.Lines[endLine].EndIncludingLineBreak;
            var previousBlank = startLine > 0 && IsBlank(startLine - 1);
            if (previousBlank && endLine + 1 < source.Lines.Count)
            {
                if (IsBlank(endLine + 1))
                    stop = source.Lines[endLine + 1].EndIncludingLineBreak;
                else if (source.Lines[endLine + 1].ToString().TrimStart().StartsWith('}'))
                    start = source.Lines[startLine - 1].Start;
            }
            return TextSpan.FromBounds(start, stop);
        }

        private bool IsBlank(int line) => string.IsNullOrWhiteSpace(source.Lines[line].ToString());
    }

    private static int ErrorCount(IEnumerable<Diagnostic> diagnostics)
        => diagnostics.Count(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

    private static int ToOffset(SourceText source, LspPosition position)
    {
        if (position.Line >= source.Lines.Count) return source.Length;
        var line = source.Lines[Math.Max(0, position.Line)];
        return line.Start + Math.Clamp(position.Character, 0, line.End - line.Start);
    }

    private static LspPosition ToPosition(SourceText source, int offset)
    {
        var line = source.Lines.GetLineFromPosition(offset);
        return new LspPosition(line.LineNumber, offset - line.Start);
    }
}
