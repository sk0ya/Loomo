using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>
/// C#意味ワークスペースを必要とする編集操作の公開ファサード。
/// AppはRoslynのCompilation／ParseOptionsを参照せず、未保存本文と編集結果だけを渡す。
/// </summary>
public static class CSharpSemanticOperations
{
    public static async Task<CSharpCodeGenerationResult> OrganizeUsingsAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        bool sortSystemDirectivesFirst = true,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateAsync(solution, filePath, sourceText,
            CSharpWorkspaceSourceScope.ProjectGraph, includeSemanticCompilation: true,
            openTexts: openTexts, cancellationToken: cancellationToken);
        return AttachExpectedTexts(
            CSharpUsingOrganizer.Organize(filePath, sourceText,
                sortSystemDirectivesFirst, context.SemanticCompilation),
            filePath, sourceText, context.Snapshot);
    }

    public static async Task<CSharpCleanupResult> CleanAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        CSharpCleanupOptions options,
        CSharpEditorConfigService? editorConfig = null,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateAsync(solution, filePath, sourceText,
            CSharpWorkspaceSourceScope.ProjectGraph, includeSemanticCompilation: true,
            editorConfig, openTexts, cancellationToken);
        return AttachExpectedTexts(
            CSharpCleanupService.Clean(
                filePath, sourceText, options, context.SemanticCompilation),
            filePath, sourceText, context.Snapshot);
    }

    public static async Task<CSharpCodeGenerationResult> ExtractClassAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        LspRange selection,
        string extractedClassName,
        string destinationFilePath,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateAsync(solution, filePath, sourceText,
            CSharpWorkspaceSourceScope.ProjectGraph, includeSemanticCompilation: true,
            openTexts: openTexts, cancellationToken: cancellationToken);
        return AttachExpectedTexts(
            CSharpExtractClassService.Extract(
                filePath, sourceText, selection, extractedClassName,
                destinationFilePath, context.SemanticCompilation),
            filePath, sourceText, context.Snapshot);
    }

    public static async Task<CSharpCodeGenerationResult> ExtractInterfaceAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        LspRange selection,
        string interfaceName,
        string destinationFilePath,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateAsync(solution, filePath, sourceText,
            CSharpWorkspaceSourceScope.ProjectGraph, includeSemanticCompilation: true,
            openTexts: openTexts, cancellationToken: cancellationToken);
        if (RequireCompleteSnapshot(context) is { } incomplete)
            return incomplete;
        var result = context.SemanticCompilation is { } compilation
            ? CSharpExtractInterfaceService.Extract(filePath, sourceText,
                selection, interfaceName, destinationFilePath, compilation)
            : CSharpExtractInterfaceService.Extract(filePath, sourceText,
                selection, interfaceName, destinationFilePath);
        return AttachExpectedTexts(result, filePath, sourceText, context.Snapshot);
    }

    public static async Task<CSharpCodeGenerationResult> MoveTypeToFileAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        LspRange selection,
        string destinationFilePath,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateAsync(solution, filePath, sourceText,
            CSharpWorkspaceSourceScope.ProjectGraph, includeSemanticCompilation: true,
            openTexts: openTexts, cancellationToken: cancellationToken);
        if (RequireCompleteSnapshot(context) is { } incomplete)
            return incomplete;
        var result = context.SemanticCompilation is { } compilation
            ? CSharpMoveTypeToFileService.Move(filePath, sourceText, selection,
                destinationFilePath, compilation)
            : CSharpMoveTypeToFileService.Move(filePath, sourceText, selection,
                destinationFilePath);
        return AttachExpectedTexts(result, filePath, sourceText, context.Snapshot);
    }

    public static async Task<CSharpCodeGenerationResult> ExtractFieldAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        LspRange selection,
        string fieldName,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateAsync(solution, filePath, sourceText,
            CSharpWorkspaceSourceScope.ProjectGraph, includeSemanticCompilation: true,
            openTexts: openTexts, cancellationToken: cancellationToken);
        var result = context.SemanticCompilation is { } compilation
            ? CSharpExtractFieldService.Extract(filePath, sourceText, selection,
                fieldName, compilation)
            : CSharpExtractFieldService.Extract(filePath, sourceText, selection, fieldName);
        return AttachExpectedTexts(result, filePath, sourceText, context.Snapshot);
    }

    public static async Task<CSharpCodeGenerationResult> IntroducePropertyAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        LspRange selection,
        string propertyName,
        string propertyType,
        string accessibility = "private",
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateAsync(solution, filePath, sourceText,
            CSharpWorkspaceSourceScope.ProjectGraph, includeSemanticCompilation: true,
            openTexts: openTexts, cancellationToken: cancellationToken);
        var result = context.SemanticCompilation is { } compilation
            ? CSharpIntroducePropertyService.Introduce(filePath, sourceText,
                selection, propertyName, propertyType, accessibility, compilation)
            : CSharpIntroducePropertyService.Introduce(filePath, sourceText,
                selection, propertyName, propertyType, accessibility);
        return AttachExpectedTexts(result, filePath, sourceText, context.Snapshot);
    }

    public static async Task<CSharpCodeGenerationResult> IntroduceVariableAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        LspRange selection,
        string variableName,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateAsync(solution, filePath, sourceText,
            CSharpWorkspaceSourceScope.ProjectGraph, includeSemanticCompilation: true,
            openTexts: openTexts, cancellationToken: cancellationToken);
        var result = context.SemanticCompilation is { } compilation
            ? CSharpIntroduceVariableService.Introduce(filePath, sourceText,
                selection, variableName, compilation)
            : CSharpIntroduceVariableService.Introduce(filePath, sourceText,
                selection, variableName);
        return AttachExpectedTexts(result, filePath, sourceText, context.Snapshot);
    }

    public static async Task<CSharpCodeGenerationResult> ExtractConstantAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        LspRange selection,
        string constantName,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateAsync(solution, filePath, sourceText,
            CSharpWorkspaceSourceScope.ProjectGraph, includeSemanticCompilation: true,
            openTexts: openTexts, cancellationToken: cancellationToken);
        var result = context.SemanticCompilation is { } compilation
            ? CSharpExtractConstantService.Extract(filePath, sourceText,
                selection, constantName, compilation)
            : CSharpExtractConstantService.Extract(filePath, sourceText,
                selection, constantName);
        return AttachExpectedTexts(result, filePath, sourceText, context.Snapshot);
    }

    public static async Task<CSharpCodeGenerationResult> EncapsulateFieldAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        LspRange selection,
        string propertyName,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateAsync(solution, filePath, sourceText,
            CSharpWorkspaceSourceScope.ProjectGraph, includeSemanticCompilation: true,
            openTexts: openTexts, cancellationToken: cancellationToken);
        var result = context.SemanticCompilation is { } compilation
            ? CSharpEncapsulateFieldService.Encapsulate(filePath, sourceText,
                selection, propertyName, compilation)
            : CSharpEncapsulateFieldService.Encapsulate(filePath, sourceText,
                selection, propertyName);
        return AttachExpectedTexts(result, filePath, sourceText, context.Snapshot);
    }

    public static async Task<CSharpCodeGenerationResult> ExtractMethodAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        LspRange selection,
        string methodName,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateAsync(solution, filePath, sourceText,
            CSharpWorkspaceSourceScope.ProjectGraph, includeSemanticCompilation: true,
            openTexts: openTexts, cancellationToken: cancellationToken);
        var result = context.SemanticCompilation is { } compilation
            ? CSharpExtractMethodService.Extract(
                filePath, sourceText, selection, methodName, compilation)
            : CSharpExtractMethodService.Extract(
                filePath, sourceText, selection, methodName);
        return AttachExpectedTexts(result, filePath, sourceText, context.Snapshot);
    }

    public static async Task<CSharpCodeGenerationResult> SafeDeleteAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        LspRange selection,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateAsync(solution, filePath, sourceText,
            CSharpWorkspaceSourceScope.Solution, includeSemanticCompilation: true,
            openTexts: openTexts,
            cancellationToken: cancellationToken);
        if (RequireCompleteSnapshot(context) is { } incomplete)
            return incomplete;
        var result = context.SemanticCompilation is { } compilation
            ? CSharpSafeDeleteService.Delete(
                filePath, sourceText, selection, context.Snapshot.Texts,
                context.Snapshot.ParseOptionsByPath, compilation)
            : CSharpSafeDeleteService.Delete(
                filePath, sourceText, selection, context.Snapshot.Texts,
                context.Snapshot.ParseOptionsByPath);
        return AttachExpectedTexts(result, filePath, sourceText, context.Snapshot);
    }

    public static async Task<CSharpCodeGenerationResult> PullUpAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        LspRange selection,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateAsync(solution, filePath, sourceText,
            CSharpWorkspaceSourceScope.Solution, includeSemanticCompilation: true,
            openTexts: openTexts,
            cancellationToken: cancellationToken);
        if (RequireCompleteSnapshot(context) is { } incomplete)
            return incomplete;
        var result = context.SemanticCompilation is { } compilation
            ? CSharpPullUpMemberService.PullUp(
                filePath, sourceText, selection, context.Snapshot.Texts,
                context.Snapshot.ParseOptionsByPath, compilation)
            : CSharpPullUpMemberService.PullUp(
                filePath, sourceText, selection, context.Snapshot.Texts,
                context.Snapshot.ParseOptionsByPath);
        return AttachExpectedTexts(result, filePath, sourceText, context.Snapshot);
    }

    public static async Task<CSharpCodeGenerationResult> PushDownAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        LspRange selection,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateAsync(solution, filePath, sourceText,
            CSharpWorkspaceSourceScope.Solution, includeSemanticCompilation: true,
            openTexts: openTexts,
            cancellationToken: cancellationToken);
        if (RequireCompleteSnapshot(context) is { } incomplete)
            return incomplete;
        var result = context.SemanticCompilation is { } compilation
            ? CSharpPushDownMemberService.PushDown(
                filePath, sourceText, selection, context.Snapshot.Texts,
                destinationPath: null,
                workspaceParseOptions: context.Snapshot.ParseOptionsByPath,
                semanticCompilation: compilation)
            : CSharpPushDownMemberService.PushDown(
                filePath, sourceText, selection, context.Snapshot.Texts,
                workspaceParseOptions: context.Snapshot.ParseOptionsByPath);
        return AttachExpectedTexts(result, filePath, sourceText, context.Snapshot);
    }

    public static async Task<CSharpCodeGenerationResult> InlineMethodAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        LspRange selection,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateAsync(solution, filePath, sourceText,
            CSharpWorkspaceSourceScope.ProjectGraph, includeSemanticCompilation: true,
            openTexts: openTexts, cancellationToken: cancellationToken);
        var result = context.SemanticCompilation is { } compilation
            ? CSharpInlineMethodService.Inline(filePath, sourceText, selection, compilation)
            : CSharpInlineMethodService.Inline(filePath, sourceText, selection);
        return AttachExpectedTexts(result, filePath, sourceText, context.Snapshot);
    }

    public static async Task<CSharpCodeGenerationResult> InlineVariableAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        LspRange selection,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateAsync(solution, filePath, sourceText,
            CSharpWorkspaceSourceScope.ProjectGraph, includeSemanticCompilation: true,
            openTexts: openTexts, cancellationToken: cancellationToken);
        var result = context.SemanticCompilation is { } compilation
            ? CSharpInlineVariableService.Inline(filePath, sourceText, selection, compilation)
            : CSharpInlineVariableService.Inline(filePath, sourceText, selection);
        return AttachExpectedTexts(result, filePath, sourceText, context.Snapshot);
    }

    public static async Task<CSharpCodeGenerationResult> IntroduceParameterAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        LspRange selection,
        string parameterName,
        string parameterType,
        string callSiteArgument,
        string? defaultValue = null,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateAsync(solution, filePath, sourceText,
            CSharpWorkspaceSourceScope.Solution, includeSemanticCompilation: true,
            openTexts: openTexts,
            cancellationToken: cancellationToken);
        if (RequireCompleteSnapshot(context) is { } incomplete)
            return incomplete;
        var result = context.SemanticCompilation is { } compilation
            ? CSharpIntroduceParameterService.Introduce(
                filePath, sourceText, selection, parameterName, parameterType,
                callSiteArgument, context.Snapshot.Texts, defaultValue,
                context.Snapshot.ParseOptionsByPath, compilation)
            : CSharpIntroduceParameterService.Introduce(
                filePath, sourceText, selection, parameterName, parameterType,
                callSiteArgument, context.Snapshot.Texts, defaultValue,
                context.Snapshot.ParseOptionsByPath);
        return AttachExpectedTexts(result, filePath, sourceText, context.Snapshot);
    }

    public static async Task<CSharpCodeGenerationResult> GenerateAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        int line,
        int character,
        CSharpCodeGenerationKind kind,
        CSharpEditorConfigService? editorConfig = null,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var generationOptions = CSharpGenerationOptionsFactory.Create(
            solution, filePath, editorConfig);
        CSharpWorkspaceOperationContext? context = null;
        if (NeedsSemanticCompilation(kind))
        {
            context = await CreateAsync(solution, filePath, sourceText,
                CSharpWorkspaceSourceScope.ProjectGraph, includeSemanticCompilation: true,
                editorConfig, openTexts, cancellationToken);
            generationOptions = generationOptions with
            {
                WorkspaceParseOptions = context.Snapshot.ParseOptionsByPath,
                SemanticCompilation = context.SemanticCompilation,
            };
        }

        var result = CSharpCodeGenerationService.Generate(
            filePath, sourceText, line, character, kind,
            context?.Snapshot.Texts, generationOptions);
        return AttachExpectedTexts(result, filePath, sourceText, context?.Snapshot);
    }

    public static async Task<CSharpCodeGenerationResult> GenerateNullGuardsAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        int line,
        int character,
        CSharpEditorConfigService? editorConfig = null,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateAsync(solution, filePath, sourceText,
            CSharpWorkspaceSourceScope.ProjectGraph, includeSemanticCompilation: true,
            editorConfig, openTexts, cancellationToken);
        var result = CSharpCodeGenerationService.GenerateNullGuards(
            filePath, sourceText, line, character, context.SemanticCompilation);
        return AttachExpectedTexts(result, filePath, sourceText, context.Snapshot);
    }

    public static CSharpCodeGenerationResult GenerateJsonTypes(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        int line,
        int character,
        string json,
        string rootName,
        CSharpEditorConfigService? editorConfig = null)
        => AttachExpectedTexts(CSharpCodeGenerationService.GenerateJsonTypes(
                filePath, sourceText, line, character, json, rootName,
                CSharpGenerationOptionsFactory.Create(solution, filePath, editorConfig)),
            filePath, sourceText, snapshot: null);

    private static bool NeedsSemanticCompilation(CSharpCodeGenerationKind kind)
        => kind is CSharpCodeGenerationKind.Constructor
            or CSharpCodeGenerationKind.PropertiesFromFields
            or CSharpCodeGenerationKind.EqualsAndGetHashCode
            or CSharpCodeGenerationKind.ToString
            or CSharpCodeGenerationKind.Deconstruct
            or CSharpCodeGenerationKind.ImplementInterface
            or CSharpCodeGenerationKind.OverrideMembers
            or CSharpCodeGenerationKind.DelegatingMembers
            or CSharpCodeGenerationKind.MethodFromUsage
            or CSharpCodeGenerationKind.DisposePattern
            or CSharpCodeGenerationKind.AsyncDisposePattern;

    private static CSharpCodeGenerationResult? RequireCompleteSnapshot(
        CSharpWorkspaceOperationContext context)
        => context.SourceSnapshotWarning is { } warning
            ? new CSharpCodeGenerationResult(null, "", warning +
                "参照全体が必要なため、この操作を安全に実行できません。")
            : null;

    private static Task<CSharpWorkspaceOperationContext> CreateAsync(
        SolutionModel? solution,
        string filePath,
        string sourceText,
        CSharpWorkspaceSourceScope scope,
        bool includeSemanticCompilation = false,
        CSharpEditorConfigService? editorConfig = null,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => CSharpWorkspaceOperationContext.Create(
            solution, filePath, sourceText, scope, includeSemanticCompilation,
            editorConfigService: editorConfig, openTexts: openTexts), cancellationToken);

    private static CSharpCodeGenerationResult AttachExpectedTexts(
        CSharpCodeGenerationResult result,
        string activePath,
        string activeText,
        CSharpWorkspaceSourceSnapshot? snapshot)
    {
        if (result.Edit is not { } edit) return result;

        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uri in edit.Changes.Keys)
        {
            if (LspUri.TryToLocalPath(uri) is not { } path) continue;
            var fullPath = Path.GetFullPath(path);
            if (string.Equals(fullPath, Path.GetFullPath(activePath), StringComparison.OrdinalIgnoreCase))
                expected[fullPath] = activeText;
            else if (snapshot?.Texts.TryGetValue(fullPath, out var text) == true)
                expected[fullPath] = text;
            else if (edit.FileOperations?.Any(operation =>
                         operation.Kind == LspFileOperationKind.Create &&
                         string.Equals(LspUri.TryToLocalPath(operation.Uri), fullPath,
                             StringComparison.OrdinalIgnoreCase)) == true)
                expected[fullPath] = "";
        }

        return expected.Count == 0
            ? result
            : result with
            {
#if LOOMO_EDITOR_EXPECTED_TEXTS
                Edit = edit with { ExpectedTexts = expected },
#else
                Edit = edit,
#endif
                ExpectedTexts = expected,
            };
    }

    private static CSharpCleanupResult AttachExpectedTexts(
        CSharpCleanupResult result,
        string activePath,
        string activeText,
        CSharpWorkspaceSourceSnapshot? snapshot)
    {
        if (result.Edit is not { } edit) return result;

        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uri in edit.Changes.Keys)
        {
            if (LspUri.TryToLocalPath(uri) is not { } path) continue;
            var fullPath = Path.GetFullPath(path);
            if (string.Equals(fullPath, Path.GetFullPath(activePath), StringComparison.OrdinalIgnoreCase))
                expected[fullPath] = activeText;
            else if (snapshot?.Texts.TryGetValue(fullPath, out var text) == true)
                expected[fullPath] = text;
        }

        return expected.Count == 0
            ? result
            : result with
            {
#if LOOMO_EDITOR_EXPECTED_TEXTS
                Edit = edit with { ExpectedTexts = expected },
#else
                Edit = edit,
#endif
                ExpectedTexts = expected,
            };
    }
}
