using sk0ya.Loomo.CSharp.Editor;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.App.Views;

public partial class ShellWindow
{
    /// <summary>非同期コマンドの例外を状態行へ渡し、ディスパッチャ上の未処理例外を防ぐ。</summary>
    private async void RunCSharpOperation(string commandId, Func<Task> operation)
        => await CSharpCommandOperationController.RunAsync(commandId, operation, ShowRefactorStatus);

    /// <summary>現在のC#エディタを返す。右クリックから呼ばれた場合は呼び出し元を優先する。</summary>
    private VimEditorControl? ActiveCSharpEditor(VimEditorControl? requested = null)
    {
        var control = requested ?? _activeEditorTab?.Control;
        return control?.FilePath is { Length: > 0 } path &&
               string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase)
            ? control
            : null;
    }

    /// <summary>
    /// C#固有操作の唯一のApp側結線。右クリック、コマンドパレット、キーボードは
    /// CSharpEditorCommandCatalogの同じIDからここへ入る。
    /// </summary>
    private void ExecuteCSharpEditorCommand(string id, VimEditorControl? requested = null)
    {
        if (ActiveCSharpEditor(requested) is not { } control)
            return;

        if (CSharpEditorCommandDispatchPolicy.TryGetCodeGenerationKind(id, out var generationKind))
        {
            RunCSharpCodeGeneration(control, generationKind);
            return;
        }
        if (CSharpEditorCommandDispatchPolicy.NativeEditorCommandFor(id) is { } nativeCommand)
        {
            control.ExecuteCommand(nativeCommand);
            return;
        }

        switch (id)
        {
            case CSharpEditorCommandCatalog.ChangeSignature:
                RunCSharpOperation(id, () => RunCSharpChangeSignatureCommandAsync(control));
                break;
            case CSharpEditorCommandCatalog.GoToDefinition:
                _ = control.GoToDefinitionAsync();
                break;
            case CSharpEditorCommandCatalog.PeekDefinition:
                _ = control.PeekDefinitionAsync();
                break;
            case CSharpEditorCommandCatalog.GoToImplementation:
                _ = control.GoToImplementationAsync();
                break;
            case CSharpEditorCommandCatalog.GoToTypeDefinition:
                _ = control.GoToTypeDefinitionAsync();
                break;
            case CSharpEditorCommandCatalog.GoToDeclaration:
                _ = control.GoToDeclarationAsync();
                break;
            case CSharpEditorCommandCatalog.FindReferences:
                _ = control.FindReferencesAsync();
                break;
            case CSharpEditorCommandCatalog.OrganizeUsings:
                RunCSharpOperation(id, () => RunCSharpOrganizeUsingsAsync(control));
                break;
            case CSharpEditorCommandCatalog.Cleanup:
                RunCSharpOperation(id, () => RunCSharpCleanupAsync(control));
                break;
            case CSharpEditorCommandCatalog.ExtractMethod:
                RunCSharpOperation(id, () => RunCSharpExtractMethodAsync(control));
                break;
            case CSharpEditorCommandCatalog.ExtractInterface:
                RunCSharpOperation(id, () => RunCSharpExtractInterfaceAsync(control));
                break;
            case CSharpEditorCommandCatalog.ExtractClass:
                RunCSharpOperation(id, () => RunCSharpExtractClassAsync(control));
                break;
            case CSharpEditorCommandCatalog.PullUp:
                RunCSharpOperation(id, () => RunCSharpPullUpAsync(control));
                break;
            case CSharpEditorCommandCatalog.PushDown:
                RunCSharpOperation(id, () => RunCSharpPushDownAsync(control));
                break;
            case CSharpEditorCommandCatalog.IntroduceParameter:
                RunCSharpOperation(id, () => RunCSharpIntroduceParameterAsync(control));
                break;
            case CSharpEditorCommandCatalog.IntroduceVariable:
                RunCSharpOperation(id, () => RunCSharpIntroduceVariableAsync(control));
                break;
            case CSharpEditorCommandCatalog.IntroduceProperty:
                RunCSharpOperation(id, () => RunCSharpIntroducePropertyAsync(control));
                break;
            case CSharpEditorCommandCatalog.ExtractConstant:
                RunCSharpOperation(id, () => RunCSharpExtractConstantAsync(control));
                break;
            case CSharpEditorCommandCatalog.InlineVariable:
                RunCSharpOperation(id, () => RunCSharpInlineVariableAsync(control));
                break;
            case CSharpEditorCommandCatalog.InlineMethod:
                RunCSharpOperation(id, () => RunCSharpInlineMethodAsync(control));
                break;
            case CSharpEditorCommandCatalog.SafeDelete:
                RunCSharpOperation(id, () => RunCSharpSafeDeleteAsync(control));
                break;
            case CSharpEditorCommandCatalog.EncapsulateField:
                RunCSharpOperation(id, () => RunCSharpEncapsulateFieldAsync(control));
                break;
            case CSharpEditorCommandCatalog.ExtractField:
                RunCSharpOperation(id, () => RunCSharpExtractFieldAsync(control));
                break;
            case CSharpEditorCommandCatalog.MoveTypeToFile:
                RunCSharpOperation(id, () => RunCSharpMoveTypeToFileAsync(control));
                break;
            case CSharpEditorCommandCatalog.GenerateNullGuards:
                RunCSharpOperation(id, () => RunCSharpNullGuardGenerationAsync(control));
                break;
            case CSharpEditorCommandCatalog.GenerateJsonTypes:
                RunCSharpJsonGeneration(control);
                break;
        }
    }

    private async Task RunCSharpChangeSignatureCommandAsync(VimEditorControl control)
    {
        if (control.FilePath is not { Length: > 0 } path)
            return;

        var signature = await RefactoringRequestController.FindChangeableSignatureAsync(
            path, control.Text, control.Caret.Line, control.Caret.Column);
        if (signature is null)
        {
            ShowRefactorStatus("シグネチャを変更できるメソッドまたはコンストラクターがありません。");
            return;
        }

        await ChangeSignatureAsync(signature);
    }

    private CSharpLspFallbackController? _csharpFallbackController;
    private CSharpLspFallbackController CSharpFallbackController
        => _csharpFallbackController ??= new(
            () => _solutionModel?.Current, FindOpenCSharpEditorTexts, ShowRefactorStatus);

    private Task<LspWorkspaceEdit?> RequestCSharpRenameFallbackAsync(
        string path,
        string source,
        int line,
        int character,
        string newName,
        CancellationToken cancellationToken)
        => CSharpFallbackController.ReportAsync(CSharpFallbackController.RenameAsync(
            path, source, line, character, newName, cancellationToken));

    private Task<LspRange?> RequestCSharpPrepareRenameFallbackAsync(
        string path,
        string source,
        int line,
        int character,
        CancellationToken cancellationToken)
        => CSharpFallbackController.PrepareRenameAsync(path, source, line, character, cancellationToken);

    private Task<(string Uri, int Line, int Column)?> RequestCSharpDefinitionFallbackAsync(
        string path, string source, int line, int character, CancellationToken cancellationToken)
        => CSharpFallbackController.ReportAsync(
            CSharpFallbackController.DefinitionAsync(path, source, line, character, cancellationToken));

    private Task<IReadOnlyList<LspLocation>> RequestCSharpReferencesFallbackAsync(
        string path, string source, int line, int character, CancellationToken cancellationToken)
        => CSharpFallbackController.ReportAsync(
            CSharpFallbackController.ReferencesAsync(path, source, line, character, cancellationToken));

    private Task<IReadOnlyList<LspLocation>> RequestCSharpImplementationsFallbackAsync(
        string path, string source, int line, int character, CancellationToken cancellationToken)
        => CSharpFallbackController.ReportAsync(
            CSharpFallbackController.ImplementationsAsync(path, source, line, character, cancellationToken));

    private Task<IReadOnlyList<LspLocation>> RequestCSharpTypeDefinitionFallbackAsync(
        string path, string source, int line, int character, CancellationToken cancellationToken)
        => CSharpFallbackController.ReportAsync(
            CSharpFallbackController.TypeDefinitionAsync(path, source, line, character, cancellationToken));

    private Task<IReadOnlyList<LspLocation>> RequestCSharpDeclarationFallbackAsync(
        string path, string source, int line, int character, CancellationToken cancellationToken)
        => CSharpFallbackController.ReportAsync(
            CSharpFallbackController.DeclarationAsync(path, source, line, character, cancellationToken));
}
