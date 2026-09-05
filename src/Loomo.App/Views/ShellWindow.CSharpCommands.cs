using sk0ya.Loomo.CSharp.Editor;
using sk0ya.Loomo.CSharp.Refactoring;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.App.Views;

public partial class ShellWindow
{
    private static string CSharpCommandIdFor(CSharpCodeGenerationKind kind)
        => kind switch
        {
            CSharpCodeGenerationKind.Constructor => CSharpEditorCommandCatalog.GenerateConstructor,
            CSharpCodeGenerationKind.FieldFromConstructorParameter => CSharpEditorCommandCatalog.GenerateField,
            CSharpCodeGenerationKind.PropertiesFromFields => CSharpEditorCommandCatalog.GenerateProperties,
            CSharpCodeGenerationKind.EqualsAndGetHashCode => CSharpEditorCommandCatalog.GenerateEquality,
            CSharpCodeGenerationKind.ToString => CSharpEditorCommandCatalog.GenerateToString,
            CSharpCodeGenerationKind.Deconstruct => CSharpEditorCommandCatalog.GenerateDeconstruct,
            CSharpCodeGenerationKind.MethodFromUsage => CSharpEditorCommandCatalog.GenerateMethodFromUsage,
            CSharpCodeGenerationKind.ImplementInterface => CSharpEditorCommandCatalog.ImplementInterface,
            CSharpCodeGenerationKind.OverrideMembers => CSharpEditorCommandCatalog.GenerateOverride,
            CSharpCodeGenerationKind.DelegatingMembers => CSharpEditorCommandCatalog.GenerateDelegatingMembers,
            CSharpCodeGenerationKind.DisposePattern => CSharpEditorCommandCatalog.GenerateDisposePattern,
            CSharpCodeGenerationKind.AsyncDisposePattern => CSharpEditorCommandCatalog.GenerateAsyncDisposePattern,
            CSharpCodeGenerationKind.NullGuards => CSharpEditorCommandCatalog.GenerateNullGuards,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未登録のC#コード生成種別です。"),
        };

    /// <summary>C#操作の唯一の起動口。<b>例外はここで受けて状態行に出す</b>——任意のユーザーコードを
    /// Roslynに食わせ、ダイアログで受け取ったパスも扱うので想定外の例外は必ず起こり得る。
    /// これらは元々<c>async void</c>で、投げるとディスパッチャ上で再送出されて部屋ごと落ちていた
    /// （<c>_ =</c>で捨てていた側も、黙って何も起きないので原因が追えなかった）。</summary>
    private async void RunCSharpOperation(string commandId, Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            var title = CSharpEditorCommandCatalog.All
                .FirstOrDefault(command => string.Equals(command.Id, commandId, StringComparison.Ordinal))
                ?.Title ?? commandId;
            System.Diagnostics.Debug.WriteLine($"[C#] {title} に失敗: {ex}");
            ShowRefactorStatus($"「{title}」を実行できませんでした: {ex.Message}");
        }
    }

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

        switch (id)
        {
            case CSharpEditorCommandCatalog.Rename:
                control.ExecuteCommand("Rename");
                break;
            case CSharpEditorCommandCatalog.ChangeSignature:
                RunCSharpOperation(id, () => RunCSharpChangeSignatureCommandAsync(control));
                break;
            case CSharpEditorCommandCatalog.GoToDefinition:
#if LOOMO_EDITOR_HOST_API
                _ = control.GoToDefinitionAsync();
#else
                control.ExecuteCommand("GoToDefinition");
#endif
                break;
            case CSharpEditorCommandCatalog.PeekDefinition:
#if LOOMO_EDITOR_HOST_API
                _ = control.PeekDefinitionAsync();
#else
                control.ExecuteCommand("PeekDefinition");
#endif
                break;
            case CSharpEditorCommandCatalog.GoToImplementation:
#if LOOMO_EDITOR_HOST_API
                _ = control.GoToImplementationAsync();
#else
                control.ExecuteCommand("GoToImplementation");
#endif
                break;
            case CSharpEditorCommandCatalog.GoToTypeDefinition:
#if LOOMO_EDITOR_HOST_API
                _ = control.GoToTypeDefinitionAsync();
#else
                control.ExecuteCommand("GoToTypeDefinition");
#endif
                break;
            case CSharpEditorCommandCatalog.GoToDeclaration:
#if LOOMO_EDITOR_HOST_API
                _ = control.GoToDeclarationAsync();
#else
                control.ExecuteCommand("GoToDeclaration");
#endif
                break;
            case CSharpEditorCommandCatalog.FindReferences:
#if LOOMO_EDITOR_HOST_API
                _ = control.FindReferencesAsync();
#else
                control.ExecuteCommand("FindReferences");
#endif
                break;
            case CSharpEditorCommandCatalog.Format:
                control.ExecuteCommand("Format");
                break;
            case CSharpEditorCommandCatalog.QuickFix:
                control.ExecuteCommand("QuickFix");
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
            case CSharpEditorCommandCatalog.GenerateConstructor:
                RunCSharpCodeGeneration(control, CSharpCodeGenerationKind.Constructor);
                break;
            case CSharpEditorCommandCatalog.GenerateField:
                RunCSharpCodeGeneration(control, CSharpCodeGenerationKind.FieldFromConstructorParameter);
                break;
            case CSharpEditorCommandCatalog.GenerateProperties:
                RunCSharpCodeGeneration(control, CSharpCodeGenerationKind.PropertiesFromFields);
                break;
            case CSharpEditorCommandCatalog.GenerateEquality:
                RunCSharpCodeGeneration(control, CSharpCodeGenerationKind.EqualsAndGetHashCode);
                break;
            case CSharpEditorCommandCatalog.GenerateToString:
                RunCSharpCodeGeneration(control, CSharpCodeGenerationKind.ToString);
                break;
            case CSharpEditorCommandCatalog.GenerateDeconstruct:
                RunCSharpCodeGeneration(control, CSharpCodeGenerationKind.Deconstruct);
                break;
            case CSharpEditorCommandCatalog.GenerateMethodFromUsage:
                RunCSharpCodeGeneration(control, CSharpCodeGenerationKind.MethodFromUsage);
                break;
            case CSharpEditorCommandCatalog.ImplementInterface:
                RunCSharpCodeGeneration(control, CSharpCodeGenerationKind.ImplementInterface);
                break;
            case CSharpEditorCommandCatalog.GenerateOverride:
                RunCSharpCodeGeneration(control, CSharpCodeGenerationKind.OverrideMembers);
                break;
            case CSharpEditorCommandCatalog.GenerateDelegatingMembers:
                RunCSharpCodeGeneration(control, CSharpCodeGenerationKind.DelegatingMembers);
                break;
            case CSharpEditorCommandCatalog.GenerateDisposePattern:
                RunCSharpCodeGeneration(control, CSharpCodeGenerationKind.DisposePattern);
                break;
            case CSharpEditorCommandCatalog.GenerateAsyncDisposePattern:
                RunCSharpCodeGeneration(control, CSharpCodeGenerationKind.AsyncDisposePattern);
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

        var signature = await FindChangeableSignatureAsync(
            path, control.Text, control.Caret.Line, control.Caret.Column);
        if (signature is null)
        {
            ShowRefactorStatus("シグネチャを変更できるメソッドまたはコンストラクターがありません。");
            return;
        }

        await ChangeSignatureAsync(signature);
    }

    /// <summary>C#専用フォールバックを走らせてよいファイルか。これらのproviderは全エディタへ
    /// 結線されているので、.cs以外では必ず素通しする——さもないと他言語のLSP結果を
    /// C#のCompilation失敗メッセージで上書きしてしまう（例: .ts で F12 → 「C# 定義検索: …」）。</summary>
    private static bool IsCSharpFallbackTarget(string path)
        => !string.IsNullOrWhiteSpace(path) &&
           string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);

    private async Task<LspWorkspaceEdit?> RequestCSharpRenameFallbackAsync(
        string path,
        string source,
        int line,
        int character,
        string newName,
        CancellationToken cancellationToken)
    {
        if (!IsCSharpFallbackTarget(path)) return null;

        var result = await CSharpRenameService.RenameAsync(
            _solutionModel?.Current, path, source,
            new LspPosition(line, character), newName,
            FindOpenCSharpEditorTexts(), cancellationToken);
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"C# rename: {error}");
            return null;
        }
        return result.Edit;
    }

    private async Task<LspRange?> RequestCSharpPrepareRenameFallbackAsync(
        string path,
        string source,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        if (!IsCSharpFallbackTarget(path)) return null;

        return await CSharpRenameService.PrepareAsync(
            _solutionModel?.Current, path, source,
            new LspPosition(line, character),
            FindOpenCSharpEditorTexts(), cancellationToken);
    }

    private async Task<(string Uri, int Line, int Column)?> RequestCSharpDefinitionFallbackAsync(
        string path, string source, int line, int character, CancellationToken cancellationToken)
    {
        if (!IsCSharpFallbackTarget(path)) return null;

        var result = await CSharpNavigationService.FindDefinitionAsync(
            _solutionModel?.Current, path, source,
            new LspPosition(line, character), FindOpenCSharpEditorTexts(), cancellationToken);
        if (result.Error is { Length: > 0 } error)
            ShowRefactorStatus($"C# 定義検索: {error}");
        return result.Location is { } location
            ? (location.Uri, location.Range.Start.Line, location.Range.Start.Character)
            : null;
    }

    private async Task<IReadOnlyList<LspLocation>> RequestCSharpReferencesFallbackAsync(
        string path, string source, int line, int character, CancellationToken cancellationToken)
    {
        if (!IsCSharpFallbackTarget(path)) return [];

        var result = await CSharpNavigationService.FindReferencesAsync(
            _solutionModel?.Current, path, source,
            new LspPosition(line, character), FindOpenCSharpEditorTexts(), cancellationToken);
        if (result.Error is { Length: > 0 } error)
            ShowRefactorStatus($"C# 参照検索: {error}");
        return result.Locations;
    }

    private async Task<IReadOnlyList<LspLocation>> RequestCSharpImplementationsFallbackAsync(
        string path, string source, int line, int character, CancellationToken cancellationToken)
    {
        if (!IsCSharpFallbackTarget(path)) return [];

        var result = await CSharpNavigationService.FindImplementationsAsync(
            _solutionModel?.Current, path, source,
            new LspPosition(line, character), FindOpenCSharpEditorTexts(), cancellationToken);
        if (result.Error is { Length: > 0 } error) ShowRefactorStatus($"C# 実装先検索: {error}");
        return result.Locations;
    }

    private async Task<IReadOnlyList<LspLocation>> RequestCSharpTypeDefinitionFallbackAsync(
        string path, string source, int line, int character, CancellationToken cancellationToken)
    {
        if (!IsCSharpFallbackTarget(path)) return [];

        var result = await CSharpNavigationService.FindTypeDefinitionAsync(
            _solutionModel?.Current, path, source,
            new LspPosition(line, character), FindOpenCSharpEditorTexts(), cancellationToken);
        if (result.Error is { Length: > 0 } error) ShowRefactorStatus($"C# 型定義検索: {error}");
        return result.Locations;
    }

    private async Task<IReadOnlyList<LspLocation>> RequestCSharpDeclarationFallbackAsync(
        string path, string source, int line, int character, CancellationToken cancellationToken)
    {
        if (!IsCSharpFallbackTarget(path)) return [];

        var result = await CSharpNavigationService.FindDeclarationAsync(
            _solutionModel?.Current, path, source,
            new LspPosition(line, character), FindOpenCSharpEditorTexts(), cancellationToken);
        if (result.Error is { Length: > 0 } error) ShowRefactorStatus($"C# 宣言検索: {error}");
        return result.Locations;
    }
}
