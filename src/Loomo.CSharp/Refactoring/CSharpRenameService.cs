using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>Roslynの意味モデルだけでC#シンボルrenameを作るフォールバック。
/// LSPが未接続、または空のWorkspaceEditを返した場合でも、単純な文字列置換ではなく
/// declaration／referenceのsymbol identityに基づいて変更範囲を決める。</summary>
public static class CSharpRenameService
{
    /// <summary>Appが利用するrename対象範囲のRoslyn非公開入力境界。</summary>
    public static async Task<LspRange?> PrepareAsync(
        SolutionModel? solution,
        string filePath,
        string source,
        LspPosition position,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await Task.Run(() => CSharpWorkspaceOperationContext.Create(
            solution, filePath, source,
            scope: CSharpWorkspaceSourceScope.Solution,
            includeSemanticCompilation: true,
            openTexts: openTexts), cancellationToken);
        return context.SemanticCompilation is { } compilation
            ? await PrepareAsync(filePath, position, compilation, cancellationToken)
            : null;
    }

    /// <summary>既に作成済みCompilationを使う内部／テスト経路。</summary>
    internal static async Task<LspRange?> PrepareAsync(
        string filePath,
        LspPosition position,
        CSharpCompilation compilation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(compilation);

        var activePath = Path.GetFullPath(filePath);
        var tree = compilation.SyntaxTrees.FirstOrDefault(candidate =>
            string.Equals(Path.GetFullPath(candidate.FilePath ?? ""), activePath,
                StringComparison.OrdinalIgnoreCase));
        if (tree is null) return null;

        var text = await tree.GetTextAsync(cancellationToken);
        if (!CSharpSemanticSymbolResolver.TryGetOffset(text, position, out var offset))
            return null;
        var root = await tree.GetRootAsync(cancellationToken);
        var token = FindIdentifierToken(root, offset);
        if (token is null) return null;

        var symbol = CSharpSemanticSymbolResolver.FindSymbol(
            compilation.GetSemanticModel(tree), root, offset, cancellationToken);
        return symbol is null || symbol.Locations.All(location => !location.IsInSource)
            ? null
            : ToLspRange(text, token.Value.Span);
    }

    public static async Task<CSharpRenameResult> RenameAsync(
        SolutionModel? solution,
        string filePath,
        string source,
        LspPosition position,
        string newName,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await Task.Run(() => CSharpWorkspaceOperationContext.Create(
            solution, filePath, source,
            scope: CSharpWorkspaceSourceScope.Solution,
            includeSemanticCompilation: true,
            openTexts: openTexts), cancellationToken);
        return context.SemanticCompilation is { } compilation
            ? await RenameAsync(filePath, source, position, newName, compilation, cancellationToken,
                context.Snapshot.Texts.Keys)
            : Failed("C# renameのCompilationを作成できませんでした。");
    }

    internal static async Task<CSharpRenameResult> RenameAsync(
        string filePath,
        string source,
        LspPosition position,
        string newName,
        CSharpCompilation compilation,
        CancellationToken cancellationToken = default,
        IEnumerable<string>? sourceDocumentPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(compilation);
        if (!SyntaxFacts.IsValidIdentifier(newName) || SyntaxFacts.IsKeyword(newName))
            return Failed("新しい名前がC#の識別子として正しくありません。");

        var activePath = Path.GetFullPath(filePath);
        var activeTree = compilation.SyntaxTrees.FirstOrDefault(tree =>
            string.Equals(Path.GetFullPath(tree.FilePath ?? ""), activePath,
                StringComparison.OrdinalIgnoreCase));
        if (activeTree is null)
            return Failed("rename対象のC#文書がCompilationにありません。");

        var activeText = await activeTree.GetTextAsync(cancellationToken);
        if (!CSharpSemanticSymbolResolver.TryGetOffset(activeText, position, out var offset))
            return Failed("rename位置が文書の範囲外です。");
        var model = compilation.GetSemanticModel(activeTree);
        var root = await activeTree.GetRootAsync(cancellationToken);
        var symbol = CSharpSemanticSymbolResolver.FindSymbol(model, root, offset, cancellationToken);
        if (symbol is null)
            return Failed("位置のC#シンボルを解決できません。");
        if (symbol.Locations.All(location => !location.IsInSource))
            return Failed("外部アセンブリのシンボルはrenameできません。");

        try
        {
            var allowedSourcePaths = sourceDocumentPaths is null
                ? null
                : sourceDocumentPaths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(Path.GetFullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            using var workspace = CSharpSemanticWorkspace.Create(compilation, allowedSourcePaths);
            if (!workspace.DocumentIds.TryGetValue(activePath, out var activeDocumentId))
                return Failed("rename対象のC#文書をWorkspaceへ追加できません。");
            var document = workspace.Solution.GetDocument(activeDocumentId);
            var workspaceModel = document is null
                ? null
                : await document.GetSemanticModelAsync(cancellationToken);
            var workspaceRoot = document is null
                ? null
                : await document.GetSyntaxRootAsync(cancellationToken);
            if (workspaceModel is null || workspaceRoot is null)
                return Failed("rename用のRoslyn意味モデルを作成できません。");

            var workspaceSymbol = CSharpSemanticSymbolResolver.FindSymbol(
                workspaceModel, workspaceRoot, offset, cancellationToken);
            if (workspaceSymbol is null)
                return Failed("Workspace内のC#シンボルを解決できません。");

            var solution = workspace.Solution;
            var referenced = await SymbolFinder.FindReferencesAsync(
                workspaceSymbol, solution, cancellationToken: cancellationToken);
            var locations = new List<(DocumentId Document, TextSpan Span)>();
            foreach (var referencedSymbol in referenced)
            {
                foreach (var location in referencedSymbol.Locations)
                    locations.Add((location.Document.Id, location.Location.SourceSpan));
                foreach (var location in referencedSymbol.Definition.Locations)
                    if (location.IsInSource && location.SourceTree?.FilePath is not null &&
                        workspace.DocumentIds.ContainsKey(Path.GetFullPath(location.SourceTree.FilePath)))
                        locations.Add((workspace.DocumentIds[Path.GetFullPath(location.SourceTree.FilePath)],
                            location.SourceSpan));
            }

            // FindReferencesAsyncの結果は定義を含まない実装もあるため、対象symbolの宣言を補う。
            foreach (var location in workspaceSymbol.Locations)
                if (location.IsInSource && location.SourceTree?.FilePath is not null &&
                    workspace.DocumentIds.TryGetValue(Path.GetFullPath(location.SourceTree.FilePath), out var id))
                    locations.Add((id, location.SourceSpan));

            var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>(
                StringComparer.OrdinalIgnoreCase);
            var expectedTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in locations
                .Where(item => item.Span.Length > 0)
                .Distinct()
                .GroupBy(item => item.Document))
            {
                var changedDocument = solution.GetDocument(group.Key);
                if (changedDocument?.FilePath is not { } changedPath) continue;
                // Source Generatorの出力もCompilation／Workspaceには載るが、ユーザーが編集する
                // 文書ではない。生成物やWorkspace外の仮想パスをWorkspaceEditへ出すと、ホストの
                // ルート検証で正しく拒否されるだけでなく、生成元を誤って書き換える危険がある。
                if (allowedSourcePaths is not null &&
                    !allowedSourcePaths.Contains(Path.GetFullPath(changedPath)))
                    continue;
                var changedText = await changedDocument.GetTextAsync(cancellationToken);
                var edits = group
                    .Where(item => IsIdentifierSpan(changedText, item.Span))
                    .DistinctBy(item => item.Span)
                    .OrderBy(item => item.Span.Start)
                    .Select(item => new LspTextEdit(ToLspRange(changedText, item.Span), newName))
                    .ToArray();
                if (edits.Length > 0)
                {
                    changes[LspUri.FromPath(changedPath)] = edits;
                    expectedTexts[Path.GetFullPath(changedPath)] = changedText.ToString();
                }
            }

            var workspaceEdit = CreateWorkspaceEdit(changes, expectedTexts);
            return changes.Count == 0
                ? Failed("rename対象の参照が見つかりません。")
                : new(workspaceEdit, symbol.Name, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Failed($"C# renameに失敗しました: {ex.Message}");
        }
    }

    private static LspWorkspaceEdit CreateWorkspaceEdit(
        IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> changes,
        IReadOnlyDictionary<string, string> expectedTexts)
    {
#if LOOMO_EDITOR_EXPECTED_TEXTS
        return new LspWorkspaceEdit(changes, ExpectedTexts: expectedTexts);
#else
        return new LspWorkspaceEdit(changes);
#endif
    }

    private static bool IsIdentifierSpan(SourceText text, TextSpan span)
    {
        if (span.Start < 0 || span.End > text.Length || span.Length == 0) return false;
        var value = text.ToString(span);
        return value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_') &&
            value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');
    }

    private static SyntaxToken? FindIdentifierToken(SyntaxNode root, int offset)
    {
        if (root.FullSpan.Length == 0) return null;
        var bounded = Math.Clamp(offset, root.FullSpan.Start, root.FullSpan.End - 1);
        var token = root.FindToken(bounded);
        if (!token.IsKind(SyntaxKind.IdentifierToken)) return null;
        return token;
    }

    private static LspRange ToLspRange(SourceText text, TextSpan span)
    {
        var start = text.Lines.GetLinePosition(span.Start);
        var end = text.Lines.GetLinePosition(span.End);
        return new(new(start.Line, start.Character), new(end.Line, end.Character));
    }

    private static CSharpRenameResult Failed(string error) => new(null, null, error);

    private static class SyntaxFacts
    {
        public static bool IsValidIdentifier(string value)
            => !string.IsNullOrWhiteSpace(value) &&
                (char.IsLetter(value[0]) || value[0] == '_') &&
                value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');

        public static bool IsKeyword(string value)
            => Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(value) !=
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.None;
    }
}

public sealed record CSharpRenameResult(
    LspWorkspaceEdit? Edit,
    string? SymbolName,
    string? Error);
