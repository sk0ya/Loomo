using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>
/// C#ファイルのトップレベルusingを安全に整理する。
/// 意味モデルなしで未使用判定はできないため、ここでは重複排除と安定した並べ替えだけを行う。
/// コメント・ディレクティブを含むusingは、コメントの移動や条件付きコンパイルの変更を避けて拒否する。
/// </summary>
public static class CSharpUsingOrganizer
{
    public static CSharpCodeGenerationResult Organize(
        string filePath, string sourceText, bool sortSystemDirectivesFirst = true)
        => OrganizeCore(filePath, sourceText, sortSystemDirectivesFirst, semanticCompilation: null);

    /// <summary>未使用usingまで意味解析する内部経路。Roslyn型をAppへ公開しない。</summary>
    internal static CSharpCodeGenerationResult Organize(
        string filePath, string sourceText, bool sortSystemDirectivesFirst,
        CSharpCompilation? semanticCompilation)
        => OrganizeCore(filePath, sourceText, sortSystemDirectivesFirst, semanticCompilation);

    private static CSharpCodeGenerationResult OrganizeCore(
        string filePath, string sourceText, bool sortSystemDirectivesFirst,
        CSharpCompilation? semanticCompilation)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("C# ファイルでのみusing整理を実行できます。");

        var source = SourceText.From(sourceText);
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var unused = semanticCompilation is null
            ? new HashSet<int>()
            : FindUnusedUsings(root, filePath, semanticCompilation);
        if (root.Usings.Count < 2 && unused.Count == 0)
            return Failed("整理できるトップレベルusingが2つ以上ありません。");

        if (root.Usings.Any(HasNonWhitespaceTrivia))
            return Failed("コメントまたはプリプロセッサを含むusingは安全のため整理しません。");

        var unique = root.Usings
            .Where(usingDirective => !unused.Contains(usingDirective.FullSpan.Start))
            .GroupBy(usingDirective => usingDirective.WithoutTrivia().ToFullString().Trim(),
                StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(usingDirective => SortKey(usingDirective, sortSystemDirectivesFirst),
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(usingDirective => usingDirective.WithoutTrivia().ToFullString(),
                StringComparer.Ordinal)
            .ToArray();

        if (unique.Length == root.Usings.Count && unique.Select(x => x.WithoutTrivia().ToFullString())
            .SequenceEqual(root.Usings.Select(x => x.WithoutTrivia().ToFullString()), StringComparer.Ordinal))
            return Failed("usingは既に整理されています。");

        var first = root.Usings[0];
        var last = root.Usings[^1];
        var span = TextSpan.FromBounds(first.FullSpan.Start, last.FullSpan.End);
        var replacement = string.Concat(unique.Select(usingDirective => usingDirective.ToFullString()));
        var uri = LspUri.FromPath(Path.GetFullPath(filePath));
        var edit = new LspWorkspaceEdit(new Dictionary<string, IReadOnlyList<LspTextEdit>>(
            StringComparer.OrdinalIgnoreCase)
        {
            [uri] = [new LspTextEdit(ToLspRange(source, span), replacement)],
        });
        return new CSharpCodeGenerationResult(edit,
            $"usingを整理（{root.Usings.Count}→{unique.Length}件）");
    }

    /// <summary>Roslyn compilerが発行するCS8019の位置をusing directiveへ戻す。
    /// global usingはファイル全体へ影響するため、cleanupで自動削除しない。</summary>
    private static HashSet<int> FindUnusedUsings(
        CompilationUnitSyntax root, string filePath, CSharpCompilation compilation)
    {
        var result = new HashSet<int>();
        var fullPath = Path.GetFullPath(filePath);
        var tree = compilation.SyntaxTrees.FirstOrDefault(candidate =>
            string.Equals(Path.GetFullPath(candidate.FilePath ?? ""), fullPath,
                StringComparison.OrdinalIgnoreCase));
        if (tree is null) return result;

        foreach (var diagnostic in compilation.GetDiagnostics()
                     .Where(diagnostic => string.Equals(diagnostic.Id, "CS8019", StringComparison.Ordinal)))
        {
            if (!diagnostic.Location.IsInSource ||
                !string.Equals(Path.GetFullPath(diagnostic.Location.SourceTree?.FilePath ?? ""), fullPath,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            var position = diagnostic.Location.SourceSpan.Start;
            var usingDirective = root.Usings.FirstOrDefault(candidate =>
                candidate.FullSpan.Contains(position) || candidate.Span.Contains(position));
            if (usingDirective is not null && !usingDirective.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
                result.Add(usingDirective.FullSpan.Start);
        }
        return result;
    }

    private static bool HasNonWhitespaceTrivia(UsingDirectiveSyntax usingDirective)
        => usingDirective.DescendantTrivia(descendIntoTrivia: true)
            .Any(trivia => !trivia.IsKind(SyntaxKind.WhitespaceTrivia) &&
                           !trivia.IsKind(SyntaxKind.EndOfLineTrivia));

    private static string SortKey(UsingDirectiveSyntax usingDirective, bool systemFirst)
    {
        var name = usingDirective.Name?.ToString() ?? "";
        var isSystem = name.Equals("System", StringComparison.Ordinal) ||
            name.StartsWith("System.", StringComparison.Ordinal);
        var systemGroup = systemFirst ? (isSystem ? "0" : "1") : "0";
        var globalGroup = usingDirective.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword) ? "0" : "1";
        var staticGroup = usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) ? "1" : "0";
        var alias = usingDirective.Alias?.Name.ToString() ?? "";
        return $"{systemGroup}{globalGroup}{staticGroup}{alias}\u0000{name}";
    }

    private static LspRange ToLspRange(SourceText source, TextSpan span)
    {
        var start = source.Lines.GetLinePosition(span.Start);
        var end = source.Lines.GetLinePosition(span.End);
        return new LspRange(new LspPosition(start.Line, start.Character),
            new LspPosition(end.Line, end.Character));
    }

    private static CSharpCodeGenerationResult Failed(string message)
        => new(null, "", message);
}
