using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>LSPが未接続・空応答のときに使うC#定義／参照検索。
/// 文字列一致ではなくRoslynのsymbol identityを使い、同名の別型・別ローカルを混ぜない。</summary>
public static class CSharpNavigationService
{
    public static async Task<CSharpDefinitionResult> FindDefinitionAsync(
        SolutionModel? solution,
        string filePath,
        string source,
        LspPosition position,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var compilation = await CreateCompilationAsync(
            solution, filePath, source, openTexts, cancellationToken);
        return compilation is null
            ? FailedDefinition("C#定義検索のCompilationを作成できませんでした。")
            : await FindDefinitionAsync(filePath, source, position, compilation, cancellationToken);
    }

    public static async Task<CSharpReferencesResult> FindReferencesAsync(
        SolutionModel? solution,
        string filePath,
        string source,
        LspPosition position,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var compilation = await CreateCompilationAsync(
            solution, filePath, source, openTexts, cancellationToken);
        return compilation is null
            ? FailedReferences("C#参照検索のCompilationを作成できませんでした。")
            : await FindReferencesAsync(filePath, source, position, compilation, cancellationToken);
    }

    public static async Task<CSharpLocationsResult> FindImplementationsAsync(
        SolutionModel? solution,
        string filePath,
        string source,
        LspPosition position,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var compilation = await CreateCompilationAsync(
            solution, filePath, source, openTexts, cancellationToken);
        return compilation is null
            ? FailedLocations("C#実装先検索のCompilationを作成できませんでした。")
            : await FindImplementationsAsync(filePath, source, position, compilation, cancellationToken);
    }

    public static async Task<CSharpLocationsResult> FindTypeDefinitionAsync(
        SolutionModel? solution,
        string filePath,
        string source,
        LspPosition position,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var compilation = await CreateCompilationAsync(
            solution, filePath, source, openTexts, cancellationToken);
        return compilation is null
            ? FailedLocations("C#型定義検索のCompilationを作成できませんでした。")
            : await FindTypeDefinitionAsync(filePath, source, position, compilation, cancellationToken);
    }

    public static async Task<CSharpLocationsResult> FindDeclarationAsync(
        SolutionModel? solution,
        string filePath,
        string source,
        LspPosition position,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var compilation = await CreateCompilationAsync(
            solution, filePath, source, openTexts, cancellationToken);
        return compilation is null
            ? FailedLocations("C#宣言検索のCompilationを作成できませんでした。")
            : await FindDeclarationAsync(filePath, source, position, compilation, cancellationToken);
    }

    public static async Task<CSharpDefinitionResult> FindDefinitionAsync(
        string filePath,
        string source,
        LspPosition position,
        CSharpCompilation compilation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(compilation);

        var activePath = Path.GetFullPath(filePath);
        var tree = FindTree(compilation, activePath);
        if (tree is null) return FailedDefinition("定義検索対象のC#文書がCompilationにありません。");
        var text = await tree.GetTextAsync(cancellationToken);
        if (!CSharpSemanticSymbolResolver.TryGetOffset(text, position, out var offset))
            return FailedDefinition("定義検索位置が文書の範囲外です。");

        var root = await tree.GetRootAsync(cancellationToken);
        var symbol = CSharpSemanticSymbolResolver.FindSymbol(
            compilation.GetSemanticModel(tree), root, offset, cancellationToken);
        if (symbol is null) return FailedDefinition("位置のC#シンボルを解決できません。");

        var locations = symbol.Locations
            .Where(location => location.IsInSource && location.SourceTree?.FilePath is not null)
            .Select(location => (location, path: Path.GetFullPath(location.SourceTree!.FilePath!)))
            .OrderBy(item => string.Equals(item.path, activePath,
                StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToArray();
        if (locations.Length == 0)
            return FailedDefinition("外部アセンブリのシンボルにはソース定義がありません。");

        var target = locations[0];
        var targetText = target.location.SourceTree!.GetText(cancellationToken);
        return new(ToLspLocation(target.path, targetText, target.location.SourceSpan),
            symbol.Name, null);
    }

    public static async Task<CSharpReferencesResult> FindReferencesAsync(
        string filePath,
        string source,
        LspPosition position,
        CSharpCompilation compilation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(compilation);

        var activePath = Path.GetFullPath(filePath);
        var tree = FindTree(compilation, activePath);
        if (tree is null) return FailedReferences("参照検索対象のC#文書がCompilationにありません。");
        var text = await tree.GetTextAsync(cancellationToken);
        if (!CSharpSemanticSymbolResolver.TryGetOffset(text, position, out var offset))
            return FailedReferences("参照検索位置が文書の範囲外です。");
        var root = await tree.GetRootAsync(cancellationToken);
        var symbol = CSharpSemanticSymbolResolver.FindSymbol(
            compilation.GetSemanticModel(tree), root, offset, cancellationToken);
        if (symbol is null) return FailedReferences("位置のC#シンボルを解決できません。");

        try
        {
            using var workspace = CSharpSemanticWorkspace.Create(compilation);
            if (!workspace.DocumentIds.TryGetValue(activePath, out var activeDocumentId))
                return FailedReferences("参照検索対象のC#文書をWorkspaceへ追加できません。");
            var activeDocument = workspace.Solution.GetDocument(activeDocumentId);
            var workspaceModel = activeDocument is null
                ? null
                : await activeDocument.GetSemanticModelAsync(cancellationToken);
            var workspaceRoot = activeDocument is null
                ? null
                : await activeDocument.GetSyntaxRootAsync(cancellationToken);
            if (workspaceModel is null || workspaceRoot is null)
                return FailedReferences("参照検索用のRoslyn意味モデルを作成できません。");

            var workspaceSymbol = CSharpSemanticSymbolResolver.FindSymbol(
                workspaceModel, workspaceRoot, offset, cancellationToken);
            if (workspaceSymbol is null)
                return FailedReferences("Workspace内のC#シンボルを解決できません。");

            var locations = new List<(DocumentId? DocumentId, Location Location)>();
            var referenced = await SymbolFinder.FindReferencesAsync(
                workspaceSymbol, workspace.Solution, cancellationToken: cancellationToken);
            foreach (var referencedSymbol in referenced)
            {
                locations.AddRange(referencedSymbol.Locations.Select(location =>
                    (DocumentId: (DocumentId?)location.Document.Id, Location: location.Location)));
                locations.AddRange(referencedSymbol.Definition.Locations
                    .Where(location => location.IsInSource)
                    .Select(location => (
                        DocumentId: workspace.DocumentIds.TryGetValue(
                            Path.GetFullPath(location.SourceTree?.FilePath ?? ""), out var id)
                            ? id : null,
                        Location: location)));
            }

            var result = new List<LspLocation>();
            foreach (var location in locations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (location.DocumentId is null || location.Location.SourceSpan.Length == 0)
                    continue;
                var document = workspace.Solution.GetDocument(location.DocumentId);
                if (document?.FilePath is not { } path) continue;
                var documentText = await document.GetTextAsync(cancellationToken);
                result.Add(ToLspLocation(path, documentText, location.Location.SourceSpan));
            }

            var distinct = result
                .GroupBy(location => (location.Uri,
                    location.Range.Start.Line, location.Range.Start.Character,
                    location.Range.End.Line, location.Range.End.Character))
                .Select(group => group.First())
                .OrderBy(location => location.Uri, StringComparer.OrdinalIgnoreCase)
                .ThenBy(location => location.Range.Start.Line)
                .ThenBy(location => location.Range.Start.Character)
                .ToArray();
            return new(distinct, workspaceSymbol.Name, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return FailedReferences($"C#参照検索に失敗しました: {ex.Message}");
        }
    }

    /// <summary>インターフェースまたは基底メンバーの実装先をRoslynのsymbol identityで検索する。</summary>
    public static async Task<CSharpLocationsResult> FindImplementationsAsync(
        string filePath,
        string source,
        LspPosition position,
        CSharpCompilation compilation,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveWorkspaceSymbolAsync(
            filePath, source, position, compilation, cancellationToken);
        if (context is null)
            return FailedLocations("実装先検索対象のC#シンボルをWorkspaceへ追加できません。");

        using (context.Workspace)
        {
            try
            {
                var implementations = await SymbolFinder.FindImplementationsAsync(
                    context.Symbol, context.Workspace.Solution,
                    cancellationToken: cancellationToken);
                var locations = ToSourceLocations(
                    implementations, context.Workspace, cancellationToken);
                return new(locations, context.Symbol.Name, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                return FailedLocations($"C#実装先検索に失敗しました: {ex.Message}");
            }
        }
    }

    /// <summary>シンボルが表す型のソース定義を検索する。</summary>
    public static async Task<CSharpLocationsResult> FindTypeDefinitionAsync(
        string filePath,
        string source,
        LspPosition position,
        CSharpCompilation compilation,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveWorkspaceSymbolAsync(
            filePath, source, position, compilation, cancellationToken);
        if (context is null)
            return FailedLocations("型定義検索対象のC#シンボルをWorkspaceへ追加できません。");

        var type = GetTypeDefinition(context.Symbol);
        if (type is null)
            return FailedLocations("位置のC#シンボルから型定義を解決できません。");

        using (context.Workspace)
        {
            var locations = ToSourceLocations(
                [type], context.Workspace, cancellationToken);
            return new(locations, type.Name, null);
        }
    }

    /// <summary>シンボルの宣言位置をソース上で検索する。</summary>
    public static async Task<CSharpLocationsResult> FindDeclarationAsync(
        string filePath,
        string source,
        LspPosition position,
        CSharpCompilation compilation,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveWorkspaceSymbolAsync(
            filePath, source, position, compilation, cancellationToken);
        if (context is null)
            return FailedLocations("宣言検索対象のC#シンボルをWorkspaceへ追加できません。");

        using (context.Workspace)
        {
            var declaration = context.Symbol.OriginalDefinition;
            var locations = ToSourceLocations(
                [declaration], context.Workspace, cancellationToken);
            return new(locations, declaration.Name, null);
        }
    }

    private static async Task<WorkspaceSymbolContext?> ResolveWorkspaceSymbolAsync(
        string filePath,
        string source,
        LspPosition position,
        CSharpCompilation compilation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(compilation);

        var activePath = Path.GetFullPath(filePath);
        var tree = FindTree(compilation, activePath);
        if (tree is null) return null;
        var text = await tree.GetTextAsync(cancellationToken);
        if (!CSharpSemanticSymbolResolver.TryGetOffset(text, position, out var offset))
            return null;

        var root = await tree.GetRootAsync(cancellationToken);
        var symbol = CSharpSemanticSymbolResolver.FindSymbol(
            compilation.GetSemanticModel(tree), root, offset, cancellationToken);
        if (symbol is null) return null;

        var workspace = CSharpSemanticWorkspace.Create(compilation);
        if (!workspace.DocumentIds.TryGetValue(activePath, out var documentId))
        {
            workspace.Dispose();
            return null;
        }

        var document = workspace.Solution.GetDocument(documentId);
        var workspaceModel = document is null
            ? null
            : await document.GetSemanticModelAsync(cancellationToken);
        var workspaceRoot = document is null
            ? null
            : await document.GetSyntaxRootAsync(cancellationToken);
        if (workspaceModel is null || workspaceRoot is null)
        {
            workspace.Dispose();
            return null;
        }

        var workspaceSymbol = CSharpSemanticSymbolResolver.FindSymbol(
            workspaceModel, workspaceRoot, offset, cancellationToken);
        if (workspaceSymbol is null)
        {
            workspace.Dispose();
            return null;
        }

        return new(workspace, workspaceSymbol);
    }

    private static IReadOnlyList<LspLocation> ToSourceLocations(
        IEnumerable<ISymbol> symbols,
        CSharpSemanticWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var result = new List<LspLocation>();
        foreach (var location in symbols.SelectMany(symbol => symbol.Locations)
                     .Where(location => location.IsInSource && location.SourceTree?.FilePath is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(location.SourceTree!.FilePath!);
            if (!workspace.DocumentIds.ContainsKey(path) || location.SourceSpan.Length == 0)
                continue;
            var text = location.SourceTree.GetText(cancellationToken);
            result.Add(ToLspLocation(path, text, location.SourceSpan));
        }

        return result
            .GroupBy(location => (location.Uri,
                location.Range.Start.Line, location.Range.Start.Character,
                location.Range.End.Line, location.Range.End.Character))
            .Select(group => group.First())
            .OrderBy(location => location.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(location => location.Range.Start.Line)
            .ThenBy(location => location.Range.Start.Character)
            .ToArray();
    }

    private static INamedTypeSymbol? GetTypeDefinition(ISymbol symbol)
    {
        var type = symbol switch
        {
            ITypeSymbol typeSymbol => typeSymbol,
            IMethodSymbol method => method.ContainingType,
            IPropertySymbol property => property.Type,
            IFieldSymbol field => field.Type,
            IEventSymbol @event => @event.Type,
            IParameterSymbol parameter => parameter.Type,
            ILocalSymbol local => local.Type,
            _ => null,
        };
        return type as INamedTypeSymbol;
    }

    private static SyntaxTree? FindTree(CSharpCompilation compilation, string path)
        => compilation.SyntaxTrees.FirstOrDefault(tree =>
            string.Equals(Path.GetFullPath(tree.FilePath ?? ""), path,
                StringComparison.OrdinalIgnoreCase));

    private static async Task<CSharpCompilation?> CreateCompilationAsync(
        SolutionModel? solution,
        string filePath,
        string source,
        IReadOnlyDictionary<string, string>? openTexts,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath) ||
            !string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return null;

        var context = await Task.Run(() => CSharpWorkspaceOperationContext.Create(
            solution, filePath, source,
            scope: CSharpWorkspaceSourceScope.Solution,
            includeSemanticCompilation: true,
            openTexts: openTexts), cancellationToken);
        return context.SemanticCompilation;
    }

    private static LspLocation ToLspLocation(string path, SourceText text, TextSpan span)
    {
        var start = text.Lines.GetLinePosition(span.Start);
        var end = text.Lines.GetLinePosition(span.End);
        return new(LspUri.FromPath(path),
            new(new(start.Line, start.Character), new(end.Line, end.Character)));
    }

    private static CSharpDefinitionResult FailedDefinition(string error) => new(null, null, error);
    private static CSharpReferencesResult FailedReferences(string error) => new([], null, error);
    private static CSharpLocationsResult FailedLocations(string error) => new([], null, error);

    private sealed record WorkspaceSymbolContext(
        CSharpSemanticWorkspace Workspace,
        ISymbol Symbol);

}

public sealed record CSharpDefinitionResult(
    LspLocation? Location,
    string? SymbolName,
    string? Error);

public sealed record CSharpReferencesResult(
    IReadOnlyList<LspLocation> Locations,
    string? SymbolName,
    string? Error);

public sealed record CSharpLocationsResult(
    IReadOnlyList<LspLocation> Locations,
    string? SymbolName,
    string? Error);
