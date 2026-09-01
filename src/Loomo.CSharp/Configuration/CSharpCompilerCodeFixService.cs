using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Configuration;

/// <summary>
/// LSPがcompiler Code Actionを返さない場合の、限定的で検証可能なC# Quick Fix。
/// 修正後のCompilationでも対象診断が消える候補だけを返し、推測で本文を変更しない。
/// </summary>
public static class CSharpCompilerCodeFixService
{
    private static readonly HashSet<string> UsingFixDiagnosticIds =
        ["CS0103", "CS0246"];

    public static async Task<IReadOnlyList<LspCodeAction>> GetAsync(
        SolutionModel? solution,
        string filePath,
        string source,
        LspRange range,
        IReadOnlyList<string>? only = null,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase) ||
            !AllowsQuickFix(only))
            return [];

        return await Task.Run(() => Get(
            solution, filePath, source, range, openTexts, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// LSPのsource.fixAllがcompiler診断を返さない場合に使う、プロジェクト／solution範囲の
    /// 限定Fix All。各ファイルを修正するたびにCompilationを作り直して診断範囲を更新し、
    /// 最終的な全文置換だけを返す。途中結果を直接ファイルへ書かないため、Appの通常の
    /// WorkspaceEdit preview／atomic apply／Undo経路へ安全に渡せる。
    /// </summary>
    public static async Task<CSharpCompilerCodeFixBatchResult> ApplyAllAsync(
        SolutionModel solution,
        IReadOnlyList<string> filePaths,
        IReadOnlyDictionary<string, string>? currentTexts = null,
        IReadOnlyDictionary<string, string>? baselineTexts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in filePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(rawPath);
            if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".cs",
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (TryGetText(currentTexts, path, out var current))
                texts[path] = current!;
            else
                texts[path] = await File.ReadAllTextAsync(path, cancellationToken);
        }

        var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>(
            StringComparer.OrdinalIgnoreCase);
        var actionsFound = 0;
        foreach (var (path, initialText) in texts.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = initialText;
            for (var pass = 0; pass < 100; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                texts[path] = text;
                var actions = await GetAsync(
                    solution, path, text, FullDocumentRange(text),
                    openTexts: texts, cancellationToken: cancellationToken);
                // 未使用usingの削除を先に行うと、同じ本文にある不足型の候補探索を
                // 乱しやすい。Fix Allではコンパイルエラーを解消するusing追加を先に選ぶ。
                var action = actions
                    .Where(candidate => candidate.Edit?.Changes.TryGetValue(
                        LspUri.FromPath(path), out var edits) == true && edits is { Count: > 0 })
                    .OrderByDescending(candidate => candidate.Title.EndsWith("を追加",
                        StringComparison.Ordinal))
                    .ThenByDescending(candidate => candidate.IsPreferred)
                    .FirstOrDefault();
                if (action?.Edit?.Changes.TryGetValue(LspUri.FromPath(path), out var fileEdits) != true ||
                    fileEdits is null)
                    break;

                var updated = ApplyTextEdits(text, fileEdits);
                if (string.Equals(updated, text, StringComparison.Ordinal)) break;
                text = updated;
                actionsFound++;
            }

            if (!string.Equals(text, initialText, StringComparison.Ordinal))
            {
                var baseline = TryGetText(baselineTexts, path, out var configuredBaseline)
                    ? configuredBaseline!
                    : initialText;
                changes[LspUri.FromPath(path)] =
                    [new LspTextEdit(FullDocumentRange(baseline), text)];
            }
        }

        return new CSharpCompilerCodeFixBatchResult(
            changes.Count == 0 ? null : new LspWorkspaceEdit(changes),
            texts.Count, actionsFound);
    }

    private static bool TryGetText(
        IReadOnlyDictionary<string, string>? texts, string path, out string? value)
    {
        if (texts is not null)
        {
            foreach (var (candidatePath, candidateText) in texts)
            {
                if (string.Equals(Path.GetFullPath(candidatePath), path,
                        StringComparison.OrdinalIgnoreCase))
                {
                    value = candidateText;
                    return true;
                }
            }
        }
        value = null;
        return false;
    }

    private static IReadOnlyList<LspCodeAction> Get(
        SolutionModel? solution,
        string filePath,
        string source,
        LspRange range,
        IReadOnlyDictionary<string, string>? openTexts,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(filePath);
        var project = solution?.ProjectForFile(fullPath);
        if (project is null || project.State != ProjectLoadState.Ready)
            return [];

        var target = project.SelectedTargetFrameworkModel;
        var editorConfig = new CSharpEditorConfigService().Resolve(fullPath);
        var context = CSharpWorkspaceOperationContext.Create(
            solution, fullPath, source,
            // compilerの文書Quick Fixはアクティブ文書の診断と参照型の探索だけを行う。
            // solution全体を積む必要はなく、Fix Allも対象ファイルごとにこのグラフを使う。
            CSharpWorkspaceSourceScope.ProjectGraph,
            includeSemanticCompilation: true,
            compilationOptions: CSharpProjectCompilationOptions.Compilation(target, editorConfig),
            assemblyName: project.Name,
            openTexts: openTexts);
        if (context.SemanticCompilation is not { } compilation || !context.IsSourceSnapshotComplete)
            return [];

        var tree = compilation.SyntaxTrees.FirstOrDefault(candidate =>
            string.Equals(Path.GetFullPath(candidate.FilePath ?? ""), fullPath,
                StringComparison.OrdinalIgnoreCase));
        if (tree is null) return [];

        var text = tree.GetText(cancellationToken);
        var root = tree.GetCompilationUnitRoot(cancellationToken);
        var diagnostics = compilation.GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Location.IsInSource &&
                ReferenceEquals(diagnostic.Location.SourceTree, tree) &&
                IsInRange(ToLspRange(text, diagnostic.Location.SourceSpan), range))
            .OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start)
            .ToArray();
        var actions = new List<LspCodeAction>();
        foreach (var diagnostic in diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (diagnostic.Id == "CS8019")
            {
                if (TryCreateUnusedUsingAction(fullPath, text, root, diagnostic) is { } action)
                    actions.Add(action);
                continue;
            }

            if (diagnostic.Id == "CS0168")
            {
                if (TryCreateUnusedLocalAction(fullPath, text, root, diagnostic) is { } action)
                    actions.Add(action);
                continue;
            }

            if (UsingFixDiagnosticIds.Contains(diagnostic.Id) &&
                TryGetIdentifier(root, diagnostic, out var identifier))
            {
                actions.AddRange(CreateUsingActions(
                    fullPath, text, root, compilation, tree, diagnostic, identifier,
                    cancellationToken));
            }
        }

        return actions
            .GroupBy(action => (action.Title, action.Edit?.Changes.Values
                .SelectMany(edits => edits).FirstOrDefault()?.NewText))
            .Select(group => group.First())
            .Take(12)
            .ToArray();
    }

    private static IReadOnlyList<LspCodeAction> CreateUsingActions(
        string filePath,
        SourceText text,
        CompilationUnitSyntax root,
        CSharpCompilation compilation,
        SyntaxTree tree,
        Diagnostic diagnostic,
        string identifier,
        CancellationToken cancellationToken)
    {
        var existing = root.Usings
            .Select(usingDirective => usingDirective.Name?.ToString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
        var containingNamespaces = FindNamedTypes(compilation, identifier, cancellationToken)
            .Where(symbol => compilation.IsSymbolAccessibleWithin(symbol, compilation.Assembly))
            .Select(symbol => symbol.ContainingNamespace)
            .Where(namespaceSymbol => namespaceSymbol is not null && !namespaceSymbol.IsGlobalNamespace)
            .Select(namespaceSymbol => namespaceSymbol!.ToDisplayString())
            .Where(name => name.Length > 0 && !existing.Contains(name))
            .Distinct(StringComparer.Ordinal)
            // 一般的な型名は複数アセンブリに現れ得る。標準ライブラリのnamespaceを
            // 先に置き、参照DLLのInternal等を先に選ばないようにする。
            .OrderBy(GetUsingNamespacePriority)
            .ThenBy(name => name, StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        if (containingNamespaces.Length == 0) return [];

        var insertAt = FindUsingInsertionPosition(root);
        var lineBreak = text.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var actions = new List<LspCodeAction>();
        foreach (var @namespace in containingNamespaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var usingText = $"using {@namespace};{lineBreak}";
            var updated = text.WithChanges(new TextChange(new TextSpan(insertAt, 0), usingText));
            var updatedTree = CSharpSyntaxTree.ParseText(updated,
                tree.Options as CSharpParseOptions ?? CSharpParseOptions.Default, filePath);
            var updatedCompilation = compilation.ReplaceSyntaxTree(tree, updatedTree);
            var shiftedStart = diagnostic.Location.SourceSpan.Start >= insertAt
                ? diagnostic.Location.SourceSpan.Start + usingText.Length
                : diagnostic.Location.SourceSpan.Start;
            var remains = updatedCompilation.GetDiagnostics(cancellationToken).Any(candidate =>
                candidate.Id == diagnostic.Id && candidate.Location.IsInSource &&
                ReferenceEquals(candidate.Location.SourceTree, updatedTree) &&
                candidate.Location.SourceSpan.Start <= shiftedStart &&
                shiftedStart <= candidate.Location.SourceSpan.End);
            if (remains) continue;

            var uri = LspUri.FromPath(Path.GetFullPath(filePath));
            var edit = new LspWorkspaceEdit(
                new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
                {
                    [uri] = [new LspTextEdit(ToLspRange(text,
                        new TextSpan(insertAt, 0)), usingText)],
                });
            actions.Add(new LspCodeAction(
                $"using {@namespace} を追加",
                LspCodeActionKinds.QuickFix,
                edit,
                IsPreferred: containingNamespaces.Length == 1));
        }
        return actions;
    }

    private static int GetUsingNamespacePriority(string @namespace)
        => string.Equals(@namespace, "System", StringComparison.Ordinal) ? 0
            : @namespace.StartsWith("System.", StringComparison.Ordinal) ? 1
            : 2;

    private static LspCodeAction? TryCreateUnusedLocalAction(
        string filePath,
        SourceText text,
        CompilationUnitSyntax root,
        Diagnostic diagnostic)
    {
        var token = root.FindToken(diagnostic.Location.SourceSpan.Start);
        var declarator = token.Parent?.AncestorsAndSelf()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.Span.Contains(
                diagnostic.Location.SourceSpan.Start));
        var statement = declarator?.AncestorsAndSelf()
            .OfType<LocalDeclarationStatementSyntax>()
            .FirstOrDefault();
        if (declarator is null || statement is null ||
            statement.Declaration.Variables.Count != 1 ||
            statement.UsingKeyword != default || statement.AwaitKeyword != default)
            return null;

        var span = statement.Span;
        var uri = LspUri.FromPath(Path.GetFullPath(filePath));
        var edit = new LspWorkspaceEdit(
            new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
            {
                [uri] = [new LspTextEdit(ToLspRange(text, span), "")],
            });
        return new LspCodeAction(
            "未使用のローカル変数を削除", LspCodeActionKinds.QuickFix, edit, IsPreferred: true);
    }

    private static IEnumerable<INamedTypeSymbol> FindNamedTypes(
        CSharpCompilation compilation, string identifier, CancellationToken cancellationToken)
    {
        // GetSymbolsWithNameはCompilation／Roslynホストの組み合わせによってsource symbol
        // を中心に返すことがある。参照DLLの型もusing追加候補になるため、global namespaceを
        // 明示的に辿ってSystem.Console等を取りこぼさない。
        foreach (var symbol in compilation.GetSymbolsWithName(identifier, SymbolFilter.Type, cancellationToken)
                     .OfType<INamedTypeSymbol>())
            yield return symbol;

        foreach (var symbol in FindNamedTypes(compilation.GlobalNamespace, identifier,
                     cancellationToken))
            yield return symbol;
    }

    private static IEnumerable<INamedTypeSymbol> FindNamedTypes(
        INamespaceSymbol @namespace, string identifier, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var symbol in @namespace.GetTypeMembers(identifier))
            yield return symbol;
        foreach (var child in @namespace.GetNamespaceMembers())
            foreach (var symbol in FindNamedTypes(child, identifier, cancellationToken))
                yield return symbol;
    }

    private static LspCodeAction? TryCreateUnusedUsingAction(
        string filePath,
        SourceText text,
        CompilationUnitSyntax root,
        Diagnostic diagnostic)
    {
        var usingDirective = root.Usings.FirstOrDefault(candidate =>
            candidate.FullSpan.Contains(diagnostic.Location.SourceSpan.Start) ||
            candidate.Span.Contains(diagnostic.Location.SourceSpan.Start));
        if (usingDirective is null || usingDirective.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
            return null;

        var startLine = text.Lines.GetLineFromPosition(usingDirective.SpanStart);
        var endPosition = Math.Max(usingDirective.SpanStart, usingDirective.Span.End - 1);
        var endLine = text.Lines.GetLineFromPosition(Math.Min(endPosition, text.Length));
        var span = TextSpan.FromBounds(startLine.Start, endLine.EndIncludingLineBreak);
        var uri = LspUri.FromPath(Path.GetFullPath(filePath));
        var edit = new LspWorkspaceEdit(
            new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
            {
                [uri] = [new LspTextEdit(ToLspRange(text, span), "")],
            });
        return new LspCodeAction(
            "未使用のusingを削除", LspCodeActionKinds.QuickFix, edit, IsPreferred: true);
    }

    private static bool TryGetIdentifier(
        CompilationUnitSyntax root, Diagnostic diagnostic, out string identifier)
    {
        var token = root.FindToken(diagnostic.Location.SourceSpan.Start);
        if (!token.IsKind(SyntaxKind.IdentifierToken))
        {
            identifier = "";
            return false;
        }
        identifier = token.ValueText;
        return identifier.Length > 0;
    }

    private static int FindUsingInsertionPosition(CompilationUnitSyntax root)
        => root.Usings.Count > 0
            ? root.Usings[0].SpanStart
            : root.Members.Count > 0
                ? root.Members[0].SpanStart
                : root.EndOfFileToken.SpanStart;

    private static bool AllowsQuickFix(IReadOnlyList<string>? only)
        => only is null || only.Count == 0 || only.Any(kind =>
            LspCodeActionKinds.Matches(kind, LspCodeActionKinds.QuickFix) ||
            LspCodeActionKinds.Matches(LspCodeActionKinds.QuickFix, kind));

    private static bool IsInRange(LspRange diagnostic, LspRange requested)
    {
        static int Compare(LspPosition left, LspPosition right)
            => left.Line != right.Line
                ? left.Line.CompareTo(right.Line)
                : left.Character.CompareTo(right.Character);
        var point = Compare(requested.Start, requested.End) == 0;
        if (point)
            return Compare(diagnostic.Start, requested.Start) <= 0 &&
                   Compare(requested.Start, diagnostic.End) <= 0;
        return Compare(diagnostic.Start, requested.End) < 0 &&
               Compare(requested.Start, diagnostic.End) < 0;
    }

    private static LspRange ToLspRange(SourceText text, TextSpan span)
    {
        var start = text.Lines.GetLinePosition(span.Start);
        var end = text.Lines.GetLinePosition(span.End);
        return new(new(start.Line, start.Character), new(end.Line, end.Character));
    }

    private static string ApplyTextEdits(string source, IReadOnlyList<LspTextEdit> edits)
    {
        var text = SourceText.From(source);
        var ranges = edits.Select(edit =>
                (edit, start: ToOffset(text, edit.Range.Start), end: ToOffset(text, edit.Range.End)))
            .OrderByDescending(item => item.start)
            .ToArray();
        var result = source;
        var lastStart = int.MaxValue;
        foreach (var item in ranges)
        {
            if (item.start > item.end || item.end > result.Length || item.end > lastStart)
                throw new InvalidOperationException("compiler Code Fixの編集範囲が競合しています。");
            result = result[..item.start] + item.edit.NewText + result[item.end..];
            lastStart = item.start;
        }
        return result;
    }

    private static int ToOffset(SourceText text, LspPosition position)
    {
        var line = Math.Clamp(position.Line, 0, Math.Max(0, text.Lines.Count - 1));
        var textLine = text.Lines[line];
        return Math.Clamp(textLine.Start + position.Character, textLine.Start, textLine.End);
    }

    private static LspRange FullDocumentRange(string source)
    {
        var text = SourceText.From(source);
        var end = text.Lines[^1];
        return new(new(0, 0), new(text.Lines.Count - 1, end.Span.Length));
    }
}

public sealed record CSharpCompilerCodeFixBatchResult(
    LspWorkspaceEdit? Edit, int DocumentsScanned, int ActionsFound, string? Error = null);
