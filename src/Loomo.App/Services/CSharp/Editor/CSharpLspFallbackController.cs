using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.App.Services;

/// <summary>LSP応答が不足した場合のC#フォールバック要求と結果整形。</summary>
internal sealed class CSharpLspFallbackController(
    Func<SolutionModel?> getSolution,
    Func<IReadOnlyDictionary<string, string>> getOpenTexts,
    Action<string> showStatus)
{
    public static bool IsTarget(string path)
        => !string.IsNullOrWhiteSpace(path) &&
           string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);

    public async Task<CSharpFallbackResult<LspWorkspaceEdit?>> RenameAsync(
        string path, string source, int line, int character, string newName,
        CancellationToken cancellationToken)
    {
        if (!IsTarget(path)) return new(null, null);

        var result = await CSharpRenameService.RenameAsync(
            getSolution(), path, source, new LspPosition(line, character), newName,
            getOpenTexts(), cancellationToken);
        return new(result.Edit, ErrorStatus("C# rename", result.Error));
    }

    public async Task<LspRange?> PrepareRenameAsync(
        string path, string source, int line, int character, CancellationToken cancellationToken)
    {
        if (!IsTarget(path)) return null;

        return await CSharpRenameService.PrepareAsync(
            getSolution(), path, source, new LspPosition(line, character),
            getOpenTexts(), cancellationToken);
    }

    public async Task<CSharpFallbackResult<(string Uri, int Line, int Column)?>> DefinitionAsync(
        string path, string source, int line, int character, CancellationToken cancellationToken)
    {
        if (!IsTarget(path)) return new(null, null);

        var result = await CSharpNavigationService.FindDefinitionAsync(
            getSolution(), path, source, new LspPosition(line, character),
            getOpenTexts(), cancellationToken);
        return new(CSharpEditorResultMapper.DefinitionLocation(result.Location),
            ErrorStatus("C# 定義検索", result.Error));
    }

    public Task<CSharpFallbackResult<IReadOnlyList<LspLocation>>> ReferencesAsync(
        string path, string source, int line, int character, CancellationToken cancellationToken)
        => FindLocationsAsync(path, source, line, character, "C# 参照検索", cancellationToken,
            FindReferenceLocationsAsync);

    public Task<CSharpFallbackResult<IReadOnlyList<LspLocation>>> ImplementationsAsync(
        string path, string source, int line, int character, CancellationToken cancellationToken)
        => FindLocationsAsync(path, source, line, character, "C# 実装先検索", cancellationToken,
            CSharpNavigationService.FindImplementationsAsync);

    public Task<CSharpFallbackResult<IReadOnlyList<LspLocation>>> TypeDefinitionAsync(
        string path, string source, int line, int character, CancellationToken cancellationToken)
        => FindLocationsAsync(path, source, line, character, "C# 型定義検索", cancellationToken,
            CSharpNavigationService.FindTypeDefinitionAsync);

    public Task<CSharpFallbackResult<IReadOnlyList<LspLocation>>> DeclarationAsync(
        string path, string source, int line, int character, CancellationToken cancellationToken)
        => FindLocationsAsync(path, source, line, character, "C# 宣言検索", cancellationToken,
            CSharpNavigationService.FindDeclarationAsync);

    private async Task<CSharpFallbackResult<IReadOnlyList<LspLocation>>> FindLocationsAsync(
        string path, string source, int line, int character, string statusPrefix,
        CancellationToken cancellationToken,
        Func<SolutionModel?, string, string, LspPosition,
            IReadOnlyDictionary<string, string>?, CancellationToken, Task<CSharpLocationsResult>> find)
    {
        if (!IsTarget(path)) return new(Array.Empty<LspLocation>(), null);

        var result = await find(getSolution(), path, source, new LspPosition(line, character),
            getOpenTexts(), cancellationToken);
        return new(result.Locations, ErrorStatus(statusPrefix, result.Error));
    }

    private async Task<CSharpLocationsResult> FindReferenceLocationsAsync(
        SolutionModel? solution, string path, string source, LspPosition position,
        IReadOnlyDictionary<string, string>? openTexts, CancellationToken cancellationToken)
    {
        var result = await CSharpNavigationService.FindReferencesAsync(
            solution, path, source, position, openTexts, cancellationToken);
        return new CSharpLocationsResult(result.Locations, result.SymbolName, result.Error);
    }

    private static string? ErrorStatus(string prefix, string? error)
        => error is { Length: > 0 } ? $"{prefix}: {error}" : null;

    public async Task<T> ReportAsync<T>(Task<CSharpFallbackResult<T>> request)
    {
        var response = await request;
        if (response.StatusMessage is { } status)
            showStatus(status);
        return response.Value;
    }
}

internal sealed record CSharpFallbackResult<T>(T Value, string? StatusMessage);
