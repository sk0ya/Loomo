using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace sk0ya.Loomo.CSharp.Editor;

/// <summary>
/// C# の Live Template と Postfix Template（Rider の同名機能・設計書 §36）。補完一覧へ「常に足す」候補
/// （Editor の <c>HostSupplementalCompletionProvider</c>）として出す。
///
/// <list type="bullet">
/// <item><b>Live</b>：文の先頭なら <c>foreach</c> <c>if</c> <c>try</c>…、型の本体なら <c>prop</c> <c>ctor</c>…、
/// 名前空間の直下なら <c>class</c> <c>enum</c>…。出す場所を構文木で決めるので、式の途中で
/// <c>foreach</c> の雛形が割り込むことはない。</item>
/// <item><b>Postfix</b>：<c>items.foreach</c> → <c>foreach (var item in items) { }</c>。式の範囲は、
/// <c>.foreach</c> を外した本文を構文解析して「ドットの直前で終わる最大の式」とする（文字列の切り出しで
/// 推測しない）。文を作るもの（<c>.if</c> <c>.var</c>…）は、その式が文そのものであるときだけ出す。</item>
/// </list>
///
/// <para>本文は LSP の snippet 書式（<c>${1:既定値}</c>・<c>$0</c>）で返す。字下げはここで現在行に合わせて
/// 付ける（Editor の snippet 展開は 2 行目以降を 0 桁から置くため）。</para>
/// </summary>
public static class CSharpTemplateCompletion
{
    private enum Place { Statement, Member, Type }

    private sealed record Template(string Name, Place Place, string Body, string Description);

    private sealed record Postfix(string Name, bool StatementOnly, Func<string, ExpressionSyntax, string> Body, string Description);

    private static readonly Template[] LiveTemplates =
    [
        new("cw", Place.Statement, "Console.WriteLine($0);", "Console.WriteLine"),
        new("foreach", Place.Statement, "foreach (var ${1:item} in ${2:collection})\n{\n\t$0\n}", "foreach 文"),
        new("for", Place.Statement, "for (var i = 0; i < ${1:length}; i++)\n{\n\t$0\n}", "for 文"),
        new("forr", Place.Statement, "for (var i = ${1:length} - 1; i >= 0; i--)\n{\n\t$0\n}", "逆順の for 文"),
        new("if", Place.Statement, "if (${1:condition})\n{\n\t$0\n}", "if 文"),
        new("else", Place.Statement, "else\n{\n\t$0\n}", "else 節"),
        new("while", Place.Statement, "while (${1:condition})\n{\n\t$0\n}", "while 文"),
        new("do", Place.Statement, "do\n{\n\t$0\n} while (${1:condition});", "do-while 文"),
        new("try", Place.Statement, "try\n{\n\t$0\n}\ncatch (${1:Exception} ex)\n{\n\tthrow;\n}", "try-catch"),
        new("tryf", Place.Statement, "try\n{\n\t$0\n}\nfinally\n{\n}", "try-finally"),
        new("using", Place.Statement, "using (${1:resource})\n{\n\t$0\n}", "using 文"),
        new("lock", Place.Statement, "lock (${1:this})\n{\n\t$0\n}", "lock 文"),
        new("switch", Place.Statement, "switch (${1:value})\n{\n\tcase ${2:value}:\n\t\t$0\n\t\tbreak;\n\tdefault:\n\t\tbreak;\n}", "switch 文"),
        new("prop", Place.Member, "public ${1:int} ${2:Name} { get; set; }$0", "自動実装プロパティ"),
        new("propg", Place.Member, "public ${1:int} ${2:Name} { get; }$0", "get だけのプロパティ"),
        new("ctor", Place.Member, "public {TYPE}($1)\n{\n\t$0\n}", "コンストラクター"),
        new("svm", Place.Member, "static void Main(string[] args)\n{\n\t$0\n}", "static void Main"),
        new("sim", Place.Member, "static int Main(string[] args)\n{\n\t$0\n\treturn 0;\n}", "static int Main"),
        new("test", Place.Member, "[Fact]\npublic void ${1:Test}()\n{\n\t$0\n}", "xUnit のテストメソッド"),
        new("class", Place.Type, "public class ${1:{FILE}}\n{\n\t$0\n}", "クラス"),
        new("interface", Place.Type, "public interface ${1:I{FILE}}\n{\n\t$0\n}", "インターフェース"),
        new("record", Place.Type, "public record ${1:{FILE}}($0);", "レコード"),
        new("enum", Place.Type, "public enum ${1:{FILE}}\n{\n\t$0\n}", "列挙型"),
    ];

    private static readonly Postfix[] PostfixTemplates =
    [
        new("if", true, (e, _) => $"if ({e})\n{{\n\t$0\n}}", "if (expr)"),
        new("null", true, (e, _) => $"if ({e} == null)\n{{\n\t$0\n}}", "if (expr == null)"),
        new("notnull", true, (e, _) => $"if ({e} != null)\n{{\n\t$0\n}}", "if (expr != null)"),
        new("foreach", true, (e, _) => $"foreach (var ${{1:item}} in {e})\n{{\n\t$0\n}}", "foreach (var item in expr)"),
        new("for", true, (e, _) => $"for (var i = 0; i < {e}; i++)\n{{\n\t$0\n}}", "for (… i < expr …)"),
        new("forr", true, (e, _) => $"for (var i = {e} - 1; i >= 0; i--)\n{{\n\t$0\n}}", "逆順の for"),
        new("while", true, (e, _) => $"while ({e})\n{{\n\t$0\n}}", "while (expr)"),
        new("using", true, (e, _) => $"using ({e})\n{{\n\t$0\n}}", "using (expr)"),
        new("var", true, (e, _) => $"var ${{1:name}} = {e};$0", "var name = expr;"),
        new("return", true, (e, _) => $"return {e};$0", "return expr;"),
        new("throw", true, (e, _) => $"throw {e};$0", "throw expr;"),
        new("cw", true, (e, _) => $"Console.WriteLine({e});$0", "Console.WriteLine(expr);"),
        new("not", false, (e, node) => NeedsParentheses(node) ? $"!({e})$0" : $"!{e}$0", "!expr"),
        new("par", false, (e, _) => $"({e})$0", "(expr)"),
        new("await", false, (e, _) => $"await {e}$0", "await expr"),
        new("nameof", false, (e, _) => $"nameof({e})$0", "nameof(expr)"),
        new("cast", false, (e, node) => NeedsParentheses(node) ? $"((${{1:Type}})({e}))$0" : $"((${{1:Type}}){e})$0", "((Type)expr)"),
    ];

    /// <summary>キャレット位置で出せる Live／Postfix の候補。<c>.cs</c> 以外・コメント／文字列の中では空。</summary>
    public static IReadOnlyList<LspCompletionItem> Get(string filePath, string source, int line, int character)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return [];
        var offset = OffsetOf(source, line, character);
        if (offset < 0) return [];

        var wordStart = offset;
        while (wordStart > 0 && (char.IsLetterOrDigit(source[wordStart - 1]) || source[wordStart - 1] == '_'))
            wordStart--;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        if (IsInCommentOrString(root, offset)) return [];

        var indent = LineIndent(source, offset);
        var unit = indent.Contains('\t') ? "\t" : "    ";

        if (wordStart > 0 && source[wordStart - 1] == '.')
            return PostfixItems(source, wordStart - 1, offset, indent, unit);
        return LiveItems(filePath, source, root, wordStart, offset, indent, unit);
    }

    // ── Live ────────────────────────────────────────────────────────────────

    private static IReadOnlyList<LspCompletionItem> LiveItems(
        string filePath, string source, SyntaxNode root, int wordStart, int offset, string indent, string unit)
    {
        if (PlaceAt(root, wordStart) is not { } place) return [];
        var typeName = root.FindToken(Math.Max(0, wordStart - 1)).Parent?.AncestorsAndSelf()
            .OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var range = RangeOf(source, wordStart, offset);

        var items = new List<LspCompletionItem>();
        foreach (var template in LiveTemplates.Where(t => t.Place == place))
        {
            if (template.Name == "ctor" && string.IsNullOrEmpty(typeName)) continue;
            var body = template.Body.Replace("{TYPE}", typeName ?? "").Replace("{FILE}", fileName);
            items.Add(Item(template.Name, template.Description, Indent(body, indent, unit), range, "Live Template"));
        }
        return items;
    }

    /// <summary>キャレットの語が、文・メンバー・型のどれを書き始める位置か。どれでもなければ null
    /// （式の途中・引数の中など——ここへ雛形を出すと邪魔になる）。</summary>
    private static Place? PlaceAt(SyntaxNode root, int wordStart)
    {
        var token = root.FindToken(wordStart);
        // 語がまだ無い（Ctrl+Space を空白で押した）ときは、手前のトークンの直後＝次の文／メンバーの頭として扱う。
        var startsHere = token.SpanStart == wordStart && token.Span.Length > 0;
        if (!startsHere)
        {
            var previous = root.FindToken(Math.Max(0, wordStart - 1));
            if (previous.IsKind(SyntaxKind.OpenBraceToken) || previous.IsKind(SyntaxKind.CloseBraceToken) ||
                previous.IsKind(SyntaxKind.SemicolonToken))
                return previous.Parent switch
                {
                    BlockSyntax => Place.Statement,
                    BaseTypeDeclarationSyntax => Place.Member,
                    _ when previous.Parent?.AncestorsAndSelf().Any(a => a is BlockSyntax) == true => Place.Statement,
                    _ when previous.Parent?.AncestorsAndSelf().Any(a => a is BaseTypeDeclarationSyntax) == true => Place.Member,
                    _ => Place.Type,
                };
            return null;
        }

        var node = token.Parent;
        if (node is null) return null;
        if (node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault() is { } statement &&
            statement.Parent is BlockSyntax or SwitchSectionSyntax or GlobalStatementSyntax)
            return statement.GetFirstToken() == token ? Place.Statement : null;
        if (node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault() is { } member)
        {
            if (member.GetFirstToken() != token) return null;
            return member.Parent is TypeDeclarationSyntax ? Place.Member : Place.Type;
        }
        return null;
    }

    // ── Postfix ─────────────────────────────────────────────────────────────

    private static IReadOnlyList<LspCompletionItem> PostfixItems(
        string source, int dotOffset, int offset, string indent, string unit)
    {
        // `.foreach` を外した本文で式を読む。外さないと `items.foreach` が「items のメンバー foreach」として
        // 壊れた構文木になり、式の切れ目を構文で決められない。
        var stripped = source[..dotOffset] + source[offset..];
        var root = CSharpSyntaxTree.ParseText(stripped).GetRoot();
        var token = root.FindToken(Math.Max(0, dotOffset - 1));
        if (token.Span.End != dotOffset) return [];

        // 末尾のトークンから上へ、ドットの直前で終わるノードを辿り、その中で最も外側の式を採る。
        // `Compute()` の `)` の親は引数リスト（式ではない）なので、式でないノードも通り抜ける。
        ExpressionSyntax? expression = null;
        // 代入とラムダは越えない——`x = items.foreach` で代入ごと包む、は誰も望まない（右辺だけを採る）。
        for (var node = token.Parent; node is not null && node is not StatementSyntax && node.Span.End == dotOffset; node = node.Parent)
        {
            if (node is AssignmentExpressionSyntax or LambdaExpressionSyntax) break;
            if (node is ExpressionSyntax candidate)
                expression = candidate;
        }
        if (expression is null || expression.SpanStart >= dotOffset) return [];

        var text = stripped[expression.SpanStart..dotOffset];
        var isStatement = expression.Parent is ExpressionStatementSyntax statement &&
                          statement.Expression == expression;
        var range = RangeOf(source, expression.SpanStart, offset);

        var items = new List<LspCompletionItem>();
        foreach (var postfix in PostfixTemplates)
        {
            if (postfix.StatementOnly && !isStatement) continue;
            var body = postfix.Body(text, expression);
            items.Add(Item(postfix.Name, postfix.Description, Indent(body, indent, unit), range, "Postfix Template"));
        }
        return items;
    }

    private static bool NeedsParentheses(ExpressionSyntax expression)
        => expression is BinaryExpressionSyntax or AssignmentExpressionSyntax or ConditionalExpressionSyntax
            or IsPatternExpressionSyntax or LambdaExpressionSyntax or CastExpressionSyntax or SwitchExpressionSyntax;

    // ── 共通 ────────────────────────────────────────────────────────────────

    private static LspCompletionItem Item(string name, string description, string body, LspRange range, string source)
        => new(name,
            Kind: CompletionItemKind.Snippet,
            Detail: $"{description}（{source}）",
            FilterText: name,
            Documentation: PlainPreview(body),
            TextFormat: InsertTextFormat.Snippet,
            // LSP の候補（型・メンバー）より後ろに並ぶように。名前で一致すれば並びは絞り込みが決める。
            SortText: "~" + name,
            TextEdit: new LspTextEdit(range, body));

    /// <summary>説明欄に出す、snippet 記法を外した見た目（<c>${1:item}</c> → <c>item</c>）。</summary>
    internal static string PlainPreview(string body)
        => System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(body, @"\$\{\d+:([^}]*)\}", "$1"), @"\$\d+", "");

    /// <summary>2 行目以降へ現在行の字下げを付け、<c>\t</c> を字下げ 1 段に置き換える。</summary>
    internal static string Indent(string body, string indent, string unit)
        => body.Replace("\t", unit).Replace("\n", "\n" + indent);

    private static bool IsInCommentOrString(SyntaxNode root, int offset)
    {
        if (offset == 0) return false;
        var trivia = root.FindTrivia(offset - 1, findInsideTrivia: true);
        if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
            trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
            trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            return true;
        var token = root.FindToken(offset - 1, findInsideTrivia: true);
        if (!token.Span.Contains(offset - 1) && token.Span.End != offset) return false;
        return token.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken
            or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.SingleLineRawStringLiteralToken
            or SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.Utf8StringLiteralToken
            or SyntaxKind.XmlTextLiteralToken;
    }

    private static string LineIndent(string source, int offset)
    {
        var lineStart = offset;
        while (lineStart > 0 && source[lineStart - 1] != '\n') lineStart--;
        var end = lineStart;
        while (end < source.Length && source[end] is ' ' or '\t') end++;
        return source[lineStart..end];
    }

    private static int OffsetOf(string source, int line, int character)
    {
        var current = 0;
        var index = 0;
        while (current < line)
        {
            var newline = source.IndexOf('\n', index);
            if (newline < 0) return -1;
            index = newline + 1;
            current++;
        }
        var offset = index + character;
        return offset <= source.Length ? offset : -1;
    }

    private static LspRange RangeOf(string source, int start, int end)
        => new(PositionOf(source, start), PositionOf(source, end));

    private static LspPosition PositionOf(string source, int offset)
    {
        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
            if (source[i] == '\n') { line++; lineStart = i + 1; }
        return new LspPosition(line, offset - lineStart);
    }
}
