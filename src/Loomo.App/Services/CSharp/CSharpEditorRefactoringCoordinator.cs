using Editor.Controls;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Editor;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.App.Services;

/// <summary>C# エディタ操作の意味処理をまとめ、UI依存部分だけを呼び出し元へ返す。</summary>
internal sealed class CSharpEditorRefactoringCoordinator
{
    private readonly Func<SolutionModel?> _currentSolution;
    private readonly CSharpEditorConfigService _editorConfig;
    private readonly Func<string, string, string, bool, string?> _prompt;
    private readonly Action<string> _showStatus;
    private readonly Func<IReadOnlyDictionary<string, string>> _findOpenEditorTexts;
    private readonly Func<LspWorkspaceEdit, IReadOnlyDictionary<string, string>?, bool, WorkspaceEditOutcome> _applyWorkspaceEdit;

    public CSharpEditorRefactoringCoordinator(
        Func<SolutionModel?> currentSolution,
        CSharpEditorConfigService editorConfig,
        Func<string, string, string, bool, string?> prompt,
        Action<string> showStatus,
        Func<IReadOnlyDictionary<string, string>> findOpenEditorTexts,
        Func<LspWorkspaceEdit, IReadOnlyDictionary<string, string>?, bool, WorkspaceEditOutcome> applyWorkspaceEdit)
    {
        _currentSolution = currentSolution;
        _editorConfig = editorConfig;
        _prompt = prompt;
        _showStatus = showStatus;
        _findOpenEditorTexts = findOpenEditorTexts;
        _applyWorkspaceEdit = applyWorkspaceEdit;
    }

    private string? Prompt(string title, string prompt, string initial = "", bool allowEmpty = false)
        => _prompt(title, prompt, initial, allowEmpty);

    public async Task RunCSharpOrganizeUsingsAsync(VimEditorControl control)
    {
        if (control.FilePath is not { Length: > 0 } path) return;
        var options = CSharpCleanupOptionsFactory.CreateForFile(
            path, editorConfigService: _editorConfig);
        var result = await CSharpSemanticOperations.OrganizeUsingsAsync(
            _currentSolution(), path, control.Text,
            options.SortSystemDirectivesFirst, _findOpenEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"using整理: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("using整理: 適用できる編集がありません。");
            return;
        }

        var outcome = _applyWorkspaceEdit(edit, result.ExpectedTexts, true);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public async Task RunCSharpCleanupAsync(
        VimEditorControl control,
        bool showPreview = true,
        bool suppressRoutineMessages = false)
    {
        if (control.FilePath is not { Length: > 0 } path) return;
        var text = control.Text;
        var result = await CSharpSemanticOperations.CleanAsync(
            _currentSolution(), path, text,
            CSharpCleanupOptionsFactory.CreateForFile(path, format: true,
                removeUnusedUsings: true, editorConfigService: _editorConfig),
            _editorConfig, _findOpenEditorTexts());
        if (result.IsGeneratedCode)
        {
            if (!suppressRoutineMessages)
                _showStatus(result.Summary);
            return;
        }
        if (result.Error is { Length: > 0 } error)
        {
            if (suppressRoutineMessages &&
                string.Equals(error, "cleanup対象の変更はありません。", StringComparison.Ordinal))
                return;
            _showStatus($"C# cleanup: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("C# cleanup: 適用できる編集がありません。");
            return;
        }

        // 保存時は確認なしで適用するため、cleanupが生成した単一文書編集だけを許可する。
        // 将来cleanupへ複数ファイル変更やファイル操作が加わっても、黙って適用範囲を広げない。
        if (!showPreview &&
            (edit.FileOperations is { Count: > 0 } ||
             edit.Changes.Keys.Any(uri => !string.Equals(
                 uri,
                 LspUri.FromPath(Path.GetFullPath(path)),
                 StringComparison.OrdinalIgnoreCase))))
        {
            _showStatus("C# cleanup: 保存時cleanupは現在のファイルだけに適用できます。");
            return;
        }

        var outcome = _applyWorkspaceEdit(edit, result.ExpectedTexts, showPreview);
        if (suppressRoutineMessages && outcome.Error is null && !outcome.Cancelled)
            return;
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public async Task RunCSharpExtractMethodAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            _showStatus("メソッド抽出には、同じブロック内の文を選択してください。");
            return;
        }

        var methodName = Prompt("メソッドを抽出", "新しいメソッド名を入力してください:", "ExtractedMethod");
        if (methodName is null) return;

        var result = await CSharpSemanticOperations.ExtractMethodAsync(
            _currentSolution(),
            control.FilePath!, control.Text, selection, methodName.Trim(),
            _findOpenEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"メソッド抽出: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("メソッド抽出: 適用できる編集がありません。");
            return;
        }

        var outcome = ApplyGeneratedEdit(control, edit, result.ExpectedTexts);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public async Task RunCSharpExtractInterfaceAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            _showStatus("interface抽出には、クラス名全体を選択してください。");
            return;
        }
        if (control.FilePath is not { Length: > 0 } sourcePath)
            return;

        var className = control.SelectedText.Trim();
        var interfaceName = Prompt("interfaceを抽出", "interface名を入力してください:",
            CSharpGenerationInputPolicy.DefaultInterfaceName(className));
        if (interfaceName is null) return;

        var defaultPath = CSharpGenerationInputPolicy.DefaultTypeFilePath(sourcePath, interfaceName.Trim());
        var destination = Prompt("interfaceを抽出", "interfaceの移動先ファイルパスを入力してください:", defaultPath);
        if (destination is null) return;

        var result = await CSharpSemanticOperations.ExtractInterfaceAsync(
            _currentSolution(), sourcePath, control.Text, selection,
            interfaceName.Trim(), CSharpGenerationInputPolicy.ResolveDestinationPath(sourcePath, destination),
            _findOpenEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"interface抽出: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("interface抽出: 適用できる編集がありません。");
            return;
        }

        var outcome = ApplyGeneratedEdit(control, edit, result.ExpectedTexts);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public async Task RunCSharpExtractClassAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            _showStatus("クラス抽出には、連続したメンバー全体を選択してください。");
            return;
        }
        if (control.FilePath is not { Length: > 0 } sourcePath)
            return;

        var className = Prompt("クラスを抽出", "抽出先クラス名を入力してください:", "ExtractedState");
        if (className is null) return;
        var defaultPath = CSharpGenerationInputPolicy.DefaultTypeFilePath(sourcePath, className.Trim());
        var destination = Prompt("クラスを抽出", "抽出先ファイルパスを入力してください:", defaultPath);
        if (destination is null) return;

        var sourceText = control.Text;
        var result = await CSharpSemanticOperations.ExtractClassAsync(
            _currentSolution(), sourcePath, sourceText, selection,
            className.Trim(), CSharpGenerationInputPolicy.ResolveDestinationPath(sourcePath, destination),
            _findOpenEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"クラス抽出: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("クラス抽出: 適用できる編集がありません。");
            return;
        }

        var outcome = ApplyGeneratedEdit(control, edit, result.ExpectedTexts);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public async Task RunCSharpIntroduceVariableAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            _showStatus("ローカル変数の導入には、式全体を選択してください。");
            return;
        }

        var variableName = Prompt("ローカル変数を導入", "新しい変数名を入力してください:", "value");
        if (variableName is null) return;

        var result = await CSharpSemanticOperations.IntroduceVariableAsync(
            _currentSolution(), control.FilePath!, control.Text, selection,
            variableName.Trim(), _findOpenEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"ローカル変数の導入: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("ローカル変数の導入: 適用できる編集がありません。");
            return;
        }

        var outcome = ApplyGeneratedEdit(control, edit, result.ExpectedTexts);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public async Task RunCSharpIntroducePropertyAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            _showStatus("プロパティの導入には、式全体を選択してください。");
            return;
        }

        var propertyName = Prompt("プロパティを導入", "新しいプロパティ名を入力してください:", "Value");
        if (propertyName is null) return;
        var propertyType = Prompt("プロパティを導入", "プロパティの型を入力してください:", "object");
        if (propertyType is null) return;
        var accessibility = Prompt("プロパティを導入", "アクセス修飾子（private/public等）:", "private");
        if (accessibility is null) return;

        var result = await CSharpSemanticOperations.IntroducePropertyAsync(
            _currentSolution(), control.FilePath!, control.Text, selection,
            propertyName, propertyType, accessibility, _findOpenEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"プロパティの導入: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("プロパティの導入: 適用できる編集がありません。");
            return;
        }

        var outcome = ApplyGeneratedEdit(control, edit, result.ExpectedTexts);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public async Task RunCSharpExtractConstantAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            _showStatus("定数抽出には、リテラル全体を選択してください。");
            return;
        }

        var constantName = Prompt("定数を抽出", "新しい定数名を入力してください:", "Value");
        if (constantName is null) return;

        var result = await CSharpSemanticOperations.ExtractConstantAsync(
            _currentSolution(), control.FilePath!, control.Text, selection,
            constantName.Trim(), _findOpenEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"定数抽出: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("定数抽出: 適用できる編集がありません。");
            return;
        }

        var outcome = ApplyGeneratedEdit(control, edit, result.ExpectedTexts);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public async Task RunCSharpInlineVariableAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            _showStatus("ローカル変数のインライン化には、変数名全体を選択してください。");
            return;
        }

        var result = await CSharpSemanticOperations.InlineVariableAsync(
            _currentSolution(), control.FilePath!, control.Text, selection,
            _findOpenEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"ローカル変数のインライン化: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("ローカル変数のインライン化: 適用できる編集がありません。");
            return;
        }

        var outcome = ApplyGeneratedEdit(control, edit, result.ExpectedTexts);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public async Task RunCSharpInlineMethodAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            _showStatus("メソッドのインライン化には、メソッド名全体を選択してください。");
            return;
        }

        var result = await CSharpSemanticOperations.InlineMethodAsync(
            _currentSolution(), control.FilePath!, control.Text, selection,
            _findOpenEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"メソッドのインライン化: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("メソッドのインライン化: 適用できる編集がありません。");
            return;
        }

        var outcome = ApplyGeneratedEdit(control, edit, result.ExpectedTexts);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public async Task RunCSharpEncapsulateFieldAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            _showStatus("フィールドのカプセル化には、フィールド名全体を選択してください。");
            return;
        }

        var selectedName = control.SelectedText.Trim();
        var defaultName = CSharpEncapsulateFieldService.DefaultPropertyName(selectedName);
        var propertyName = Prompt("フィールドをカプセル化", "生成するプロパティ名を入力してください:", defaultName);
        if (propertyName is null) return;

        var result = await CSharpSemanticOperations.EncapsulateFieldAsync(
            _currentSolution(), control.FilePath!, control.Text, selection,
            propertyName.Trim(), _findOpenEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"フィールドのカプセル化: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("フィールドのカプセル化: 適用できる編集がありません。");
            return;
        }

        var outcome = ApplyGeneratedEdit(control, edit, result.ExpectedTexts);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public async Task RunCSharpExtractFieldAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            _showStatus("フィールド抽出には、式全体を選択してください。");
            return;
        }

        var fieldName = Prompt("フィールドを抽出", "新しいフィールド名を入力してください:", "value");
        if (fieldName is null) return;

        var result = await CSharpSemanticOperations.ExtractFieldAsync(
            _currentSolution(), control.FilePath!, control.Text, selection,
            fieldName.Trim(), _findOpenEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"フィールド抽出: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("フィールド抽出: 適用できる編集がありません。");
            return;
        }

        var outcome = ApplyGeneratedEdit(control, edit, result.ExpectedTexts);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public async Task RunCSharpMoveTypeToFileAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            _showStatus("型の移動には、型名全体を選択してください。");
            return;
        }
        if (control.FilePath is not { Length: > 0 } sourcePath)
            return;

        var selectedName = control.SelectedText.Trim();
        var defaultPath = CSharpGenerationInputPolicy.DefaultMovedTypeFilePath(sourcePath, selectedName);
        var destination = Prompt("型を別ファイルへ移動", "移動先ファイルパスを入力してください:", defaultPath);
        if (destination is null) return;

        var result = await CSharpSemanticOperations.MoveTypeToFileAsync(
            _currentSolution(), sourcePath, control.Text, selection,
            CSharpGenerationInputPolicy.ResolveDestinationPath(sourcePath, destination), _findOpenEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"型の移動: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("型の移動: 適用できる編集がありません。");
            return;
        }

        var outcome = ApplyGeneratedEdit(control, edit, result.ExpectedTexts);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public async Task RunCSharpSafeDeleteAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            _showStatus("安全な削除には、型またはメンバー名全体を選択してください。");
            return;
        }
        if (control.FilePath is not { Length: > 0 } path)
            return;

        var result = await CSharpSemanticOperations.SafeDeleteAsync(
            _currentSolution(), path, control.Text, selection,
            _findOpenEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"安全な削除: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("安全な削除: 適用できる編集がありません。");
            return;
        }

        var outcome = ApplyGeneratedEdit(control, edit, result.ExpectedTexts);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public async Task RunCSharpPullUpAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            _showStatus("基底クラスへの移動には、メンバー全体を選択してください。");
            return;
        }
        if (control.FilePath is not { Length: > 0 } path)
            return;

        var result = await CSharpSemanticOperations.PullUpAsync(
            _currentSolution(), path, control.Text, selection,
            _findOpenEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"基底クラスへの移動: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("基底クラスへの移動: 適用できる編集がありません。");
            return;
        }

        var outcome = ApplyGeneratedEdit(control, edit, result.ExpectedTexts);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public async Task RunCSharpPushDownAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            _showStatus("派生クラスへの移動には、メンバー全体を選択してください。");
            return;
        }
        if (control.FilePath is not { Length: > 0 } path)
            return;

        var result = await CSharpSemanticOperations.PushDownAsync(
            _currentSolution(), path, control.Text, selection,
            _findOpenEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"派生クラスへの移動: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("派生クラスへの移動: 適用できる編集がありません。");
            return;
        }

        var outcome = ApplyGeneratedEdit(control, edit, result.ExpectedTexts);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public async Task RunCSharpIntroduceParameterAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            _showStatus("パラメーターの導入には、メソッド名全体を選択してください。");
            return;
        }
        if (control.FilePath is not { Length: > 0 } path)
            return;

        var parameterName = Prompt("パラメーターを導入",
            "新しいパラメーター名を入力してください:", "value");
        if (parameterName is null) return;
        var parameterType = Prompt("パラメーターを導入",
            "新しいパラメーターの型を入力してください:", "object");
        if (parameterType is null) return;
        var callSiteArgument = Prompt("パラメーターを導入",
            "呼び出し側へ追加する式を入力してください:", parameterName);
        if (callSiteArgument is null) return;
        var defaultValue = Prompt("パラメーターを導入",
            "ワークスペース外の呼び出し向け既定値（不要なら空欄）:", "", allowEmpty: true);
        if (defaultValue is null) return;

        var result = await CSharpSemanticOperations.IntroduceParameterAsync(
            _currentSolution(), path, control.Text, selection,
            parameterName, parameterType, callSiteArgument, defaultValue,
            _findOpenEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"パラメーター導入: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("パラメーター導入: 適用できる編集がありません。");
            return;
        }

        var outcome = ApplyGeneratedEdit(control, edit, result.ExpectedTexts);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public async Task RunCSharpNullGuardGenerationAsync(VimEditorControl control)
    {
        if (control.FilePath is not { Length: > 0 } path)
            return;

        var result = await CSharpSemanticOperations.GenerateNullGuardsAsync(
            _currentSolution(), path, control.Text,
            control.Caret.Line, control.Caret.Column, _editorConfig,
            _findOpenEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"コード生成: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("コード生成: 適用できる編集がありません。");
            return;
        }

        var outcome = ApplyGeneratedEdit(control, edit, result.ExpectedTexts);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public void RunCSharpJsonGeneration(VimEditorControl control)
    {
        if (control.FilePath is not { Length: > 0 } path)
            return;
        if (string.IsNullOrWhiteSpace(control.SelectedText))
        {
            _showStatus("JSONからの型生成には、JSONオブジェクトを先に選択してください。");
            return;
        }

        var rootName = Prompt("JSONからC#型を生成",
            "ルート型名を入力してください:", "Root");
        if (rootName is null) return;

        var result = CSharpSemanticOperations.GenerateJsonTypes(
            _currentSolution(),
            path, control.Text, control.Caret.Line, control.Caret.Column,
            control.SelectedText, rootName, _editorConfig);
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"コード生成: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("コード生成: 適用できる編集がありません。");
            return;
        }

        var outcome = ApplyGeneratedEdit(control, edit, result.ExpectedTexts);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    public async Task RunCSharpCodeGenerationAsync(
        VimEditorControl control, CSharpCodeGenerationKind kind)
    {
        if (control.FilePath is not { Length: > 0 } path)
            return;

        var text = control.Text;
        var line = control.Caret.Line;
        var column = control.Caret.Column;
        var result = await CSharpSemanticOperations.GenerateAsync(
            _currentSolution(), path, text, line, column, kind,
            _editorConfig, _findOpenEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            _showStatus($"コード生成: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            _showStatus("コード生成: 適用できる編集がありません。");
            return;
        }

        var outcome = ApplyGeneratedEdit(control, edit, result.ExpectedTexts);
        _showStatus(outcome.Describe(result.Summary) ?? $"「{result.Summary}」を適用しました。");
    }

    private WorkspaceEditOutcome ApplyGeneratedEdit(
        VimEditorControl control,
        LspWorkspaceEdit edit,
        IReadOnlyDictionary<string, string>? expectedTexts = null)
    {
        edit = CSharpEditorResultMapper.PrepareGeneratedEdit(
            edit, _findOpenEditorTexts(), control.FilePath, control.Text, _editorConfig);
        return _applyWorkspaceEdit(edit, expectedTexts, true);
    }
}
