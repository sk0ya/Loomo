using sk0ya.Loomo.CSharp.Editor;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.App.Views;

public partial class ShellWindow
{
    /// <summary>C# の構文だけで完結するコード生成を右クリックへ出す。
    /// 生成結果は通常の WorkspaceEdit と同じ preview／rollback／Undo を通る。</summary>
    private void AddCSharpCodeGenerationMenuItems(
        System.Windows.Controls.ContextMenu menu,
        VimEditorControl? control)
    {
        if (control?.FilePath is not { Length: > 0 } path ||
            !string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
            return;

        menu.Items.Add(new Separator());
        var root = new MenuItem { Header = "C# コード生成" };
        SetCSharpMenuAutomation(root, "CSharpCodeGeneration");
        var organizeUsings = new MenuItem
        {
            Header = "usingディレクティブを整理",
            Tag = CSharpEditorCommandCatalog.OrganizeUsings,
        };
        SetCSharpMenuAutomation(organizeUsings, CSharpEditorCommandCatalog.OrganizeUsings);
        organizeUsings.Click += (_, _) => ExecuteCSharpEditorCommand(
            CSharpEditorCommandCatalog.OrganizeUsings, control);
        root.Items.Add(organizeUsings);
        var cleanup = new MenuItem
        {
            Header = "C# cleanup profile（プレビュー）",
            Tag = CSharpEditorCommandCatalog.Cleanup,
        };
        SetCSharpMenuAutomation(cleanup, CSharpEditorCommandCatalog.Cleanup);
        cleanup.Click += (_, _) => ExecuteCSharpEditorCommand(
            CSharpEditorCommandCatalog.Cleanup, control);
        root.Items.Add(cleanup);
        var extract = new MenuItem
        {
            Header = "選択範囲からメソッドを抽出…",
            Tag = CSharpEditorCommandCatalog.ExtractMethod,
        };
        SetCSharpMenuAutomation(extract, CSharpEditorCommandCatalog.ExtractMethod);
        extract.Click += (_, _) => ExecuteCSharpEditorCommand(
            CSharpEditorCommandCatalog.ExtractMethod, control);
        root.Items.Add(extract);
        AddCSharpCommandItem(root, control, CSharpEditorCommandCatalog.ExtractInterface, "クラスからinterfaceを抽出…");
        AddCSharpCommandItem(root, control, CSharpEditorCommandCatalog.ExtractClass, "メンバーをクラスへ抽出…");
        AddCSharpCommandItem(root, control, CSharpEditorCommandCatalog.PeekDefinition, "定義をPeek表示");
        AddCSharpCommandItem(root, control, CSharpEditorCommandCatalog.PullUp, "メンバーを基底クラスへ移動");
        AddCSharpCommandItem(root, control, CSharpEditorCommandCatalog.PushDown, "メンバーを派生クラスへ移動");
        AddCSharpCommandItem(root, control, CSharpEditorCommandCatalog.IntroduceParameter, "パラメーターを導入…");
        AddCSharpCommandItem(root, control, CSharpEditorCommandCatalog.IntroduceVariable, "選択式をローカル変数に導入…");
        AddCSharpCommandItem(root, control, CSharpEditorCommandCatalog.IntroduceProperty, "選択式をプロパティに導入…");
        AddCSharpCommandItem(root, control, CSharpEditorCommandCatalog.ExtractConstant, "選択リテラルを定数に抽出…");
        AddCSharpCommandItem(root, control, CSharpEditorCommandCatalog.InlineVariable, "ローカル変数をインライン化");
        AddCSharpCommandItem(root, control, CSharpEditorCommandCatalog.InlineMethod, "メソッドをインライン化");
        AddCSharpCommandItem(root, control, CSharpEditorCommandCatalog.SafeDelete, "安全に削除");
        AddCSharpCommandItem(root, control, CSharpEditorCommandCatalog.EncapsulateField, "フィールドをカプセル化…");
        AddCSharpCommandItem(root, control, CSharpEditorCommandCatalog.ExtractField, "選択式をフィールドに抽出…");
        AddCSharpCommandItem(root, control, CSharpEditorCommandCatalog.MoveTypeToFile, "型を別ファイルへ移動…");
        AddCodeGenerationItem(root, control, CSharpCodeGenerationKind.Constructor, "コンストラクターを生成");
        AddCodeGenerationItem(root, control, CSharpCodeGenerationKind.FieldFromConstructorParameter, "コンストラクターパラメーターからフィールドを生成");
        AddCodeGenerationItem(root, control, CSharpCodeGenerationKind.PropertiesFromFields, "プロパティを生成");
        AddCodeGenerationItem(root, control, CSharpCodeGenerationKind.EqualsAndGetHashCode, "Equals／GetHashCodeを生成");
        AddCodeGenerationItem(root, control, CSharpCodeGenerationKind.ToString, "ToStringを生成");
        AddCodeGenerationItem(root, control, CSharpCodeGenerationKind.Deconstruct, "Deconstructを生成");
        AddCodeGenerationItem(root, control, CSharpCodeGenerationKind.MethodFromUsage, "使用箇所からメソッドを生成");
        AddCodeGenerationItem(root, control, CSharpCodeGenerationKind.ImplementInterface, "インターフェースを実装");
        AddCodeGenerationItem(root, control, CSharpCodeGenerationKind.OverrideMembers, "overrideメンバーを生成");
        AddCodeGenerationItem(root, control, CSharpCodeGenerationKind.DelegatingMembers, "委譲メンバーを生成");
        AddCodeGenerationItem(root, control, CSharpCodeGenerationKind.DisposePattern, "Disposeパターンを生成");
        AddCodeGenerationItem(root, control, CSharpCodeGenerationKind.AsyncDisposePattern, "非同期Disposeパターンを生成");
        AddCodeGenerationItem(root, control, CSharpCodeGenerationKind.NullGuards, "引数のnull guardを生成");
        var jsonItem = new MenuItem
        {
            Header = "JSONからC#型を生成",
            Tag = CSharpEditorCommandCatalog.GenerateJsonTypes,
        };
        SetCSharpMenuAutomation(jsonItem, CSharpEditorCommandCatalog.GenerateJsonTypes);
        jsonItem.Click += (_, _) => ExecuteCSharpEditorCommand(
            CSharpEditorCommandCatalog.GenerateJsonTypes, control);
        root.Items.Add(jsonItem);
        menu.Items.Add(root);
    }

    private void AddCSharpCommandItem(
        MenuItem root, VimEditorControl control, string commandId, string header)
    {
        var item = new MenuItem { Header = header, Tag = commandId };
        SetCSharpMenuAutomation(item, commandId);
        item.Click += (_, _) => ExecuteCSharpEditorCommand(commandId, control);
        root.Items.Add(item);
    }

    private static void SetCSharpMenuAutomation(MenuItem item, string id)
    {
        System.Windows.Automation.AutomationProperties.SetAutomationId(item, id);
        System.Windows.Automation.AutomationProperties.SetName(item, item.Header?.ToString() ?? id);
    }

    private async Task RunCSharpOrganizeUsingsAsync(VimEditorControl control)
    {
        if (control.FilePath is not { Length: > 0 } path) return;
        var options = CSharpCleanupOptionsFactory.CreateForFile(
            path, editorConfigService: _csharpEditorConfig);
        var result = await CSharpSemanticOperations.OrganizeUsingsAsync(
            _solutionModel?.Current, path, control.Text,
            options.SortSystemDirectivesFirst, FindOpenCSharpEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"using整理: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("using整理: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyLspWorkspaceEdit(edit.Changes, edit.DocumentVersions, edit.FileOperations,
            expectedTexts: result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private async Task RunCSharpCleanupAsync(VimEditorControl control)
    {
        if (control.FilePath is not { Length: > 0 } path) return;
        var text = control.Text;
        var result = await CSharpSemanticOperations.CleanAsync(
            _solutionModel?.Current, path, text,
            CSharpCleanupOptionsFactory.CreateForFile(path, format: true,
                removeUnusedUsings: true, editorConfigService: _csharpEditorConfig),
            _csharpEditorConfig, FindOpenCSharpEditorTexts());
        if (result.IsGeneratedCode)
        {
            ShowRefactorStatus(result.Summary);
            return;
        }
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"C# cleanup: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("C# cleanup: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyLspWorkspaceEdit(edit.Changes, edit.DocumentVersions, edit.FileOperations,
            expectedTexts: result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private async void RunCSharpExtractMethod(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            ShowRefactorStatus("メソッド抽出には、同じブロック内の文を選択してください。");
            return;
        }

        var methodName = InputDialog.Prompt(
            this, "メソッドを抽出", "新しいメソッド名を入力してください:", "ExtractedMethod");
        if (methodName is null) return;

        var result = await CSharpSemanticOperations.ExtractMethodAsync(
            _solutionModel?.Current,
            control.FilePath!, control.Text, selection, methodName.Trim(),
            FindOpenCSharpEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"メソッド抽出: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("メソッド抽出: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyCSharpGeneratedEdit(control, edit, result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private async void RunCSharpExtractInterface(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            ShowRefactorStatus("interface抽出には、クラス名全体を選択してください。");
            return;
        }
        if (control.FilePath is not { Length: > 0 } sourcePath)
            return;

        var className = control.SelectedText.Trim();
        var interfaceName = InputDialog.Prompt(
            this, "interfaceを抽出", "interface名を入力してください:",
            className.Length == 0 ? "IContract" : "I" + className);
        if (interfaceName is null) return;

        var defaultPath = Path.Combine(
            Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory,
            interfaceName.Trim() + ".cs");
        var destination = InputDialog.Prompt(
            this, "interfaceを抽出", "interfaceの移動先ファイルパスを入力してください:", defaultPath);
        if (destination is null) return;
        if (!Path.IsPathRooted(destination))
            destination = Path.Combine(Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory, destination);

        var result = await CSharpSemanticOperations.ExtractInterfaceAsync(
            _solutionModel?.Current, sourcePath, control.Text, selection,
            interfaceName.Trim(), Path.GetFullPath(destination.Trim()),
            FindOpenCSharpEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"interface抽出: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("interface抽出: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyCSharpGeneratedEdit(control, edit, result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private async Task RunCSharpExtractClassAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            ShowRefactorStatus("クラス抽出には、連続したメンバー全体を選択してください。");
            return;
        }
        if (control.FilePath is not { Length: > 0 } sourcePath)
            return;

        var className = InputDialog.Prompt(
            this, "クラスを抽出", "抽出先クラス名を入力してください:", "ExtractedState");
        if (className is null) return;
        var defaultPath = Path.Combine(
            Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory,
            className.Trim() + ".cs");
        var destination = InputDialog.Prompt(
            this, "クラスを抽出", "抽出先ファイルパスを入力してください:", defaultPath);
        if (destination is null) return;
        if (!Path.IsPathRooted(destination))
            destination = Path.Combine(Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory, destination);

        var sourceText = control.Text;
        var result = await CSharpSemanticOperations.ExtractClassAsync(
            _solutionModel?.Current, sourcePath, sourceText, selection,
            className.Trim(), Path.GetFullPath(destination.Trim()),
            FindOpenCSharpEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"クラス抽出: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("クラス抽出: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyCSharpGeneratedEdit(control, edit, result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private async void RunCSharpIntroduceVariable(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            ShowRefactorStatus("ローカル変数の導入には、式全体を選択してください。");
            return;
        }

        var variableName = InputDialog.Prompt(
            this, "ローカル変数を導入", "新しい変数名を入力してください:", "value");
        if (variableName is null) return;

        var result = await CSharpSemanticOperations.IntroduceVariableAsync(
            _solutionModel?.Current, control.FilePath!, control.Text, selection,
            variableName.Trim(), FindOpenCSharpEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"ローカル変数の導入: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("ローカル変数の導入: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyCSharpGeneratedEdit(control, edit, result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private async void RunCSharpIntroduceProperty(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            ShowRefactorStatus("プロパティの導入には、式全体を選択してください。");
            return;
        }

        var propertyName = InputDialog.Prompt(
            this, "プロパティを導入", "新しいプロパティ名を入力してください:", "Value");
        if (propertyName is null) return;
        var propertyType = InputDialog.Prompt(
            this, "プロパティを導入", "プロパティの型を入力してください:", "object");
        if (propertyType is null) return;
        var accessibility = InputDialog.Prompt(
            this, "プロパティを導入", "アクセス修飾子（private/public等）:", "private");
        if (accessibility is null) return;

        var result = await CSharpSemanticOperations.IntroducePropertyAsync(
            _solutionModel?.Current, control.FilePath!, control.Text, selection,
            propertyName, propertyType, accessibility, FindOpenCSharpEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"プロパティの導入: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("プロパティの導入: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyCSharpGeneratedEdit(control, edit, result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private async void RunCSharpExtractConstant(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            ShowRefactorStatus("定数抽出には、リテラル全体を選択してください。");
            return;
        }

        var constantName = InputDialog.Prompt(
            this, "定数を抽出", "新しい定数名を入力してください:", "Value");
        if (constantName is null) return;

        var result = await CSharpSemanticOperations.ExtractConstantAsync(
            _solutionModel?.Current, control.FilePath!, control.Text, selection,
            constantName.Trim(), FindOpenCSharpEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"定数抽出: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("定数抽出: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyCSharpGeneratedEdit(control, edit, result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private async void RunCSharpInlineVariable(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            ShowRefactorStatus("ローカル変数のインライン化には、変数名全体を選択してください。");
            return;
        }

        var result = await CSharpSemanticOperations.InlineVariableAsync(
            _solutionModel?.Current, control.FilePath!, control.Text, selection,
            FindOpenCSharpEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"ローカル変数のインライン化: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("ローカル変数のインライン化: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyCSharpGeneratedEdit(control, edit, result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private async void RunCSharpInlineMethod(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            ShowRefactorStatus("メソッドのインライン化には、メソッド名全体を選択してください。");
            return;
        }

        var result = await CSharpSemanticOperations.InlineMethodAsync(
            _solutionModel?.Current, control.FilePath!, control.Text, selection,
            FindOpenCSharpEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"メソッドのインライン化: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("メソッドのインライン化: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyCSharpGeneratedEdit(control, edit, result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private async void RunCSharpEncapsulateField(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            ShowRefactorStatus("フィールドのカプセル化には、フィールド名全体を選択してください。");
            return;
        }

        var selectedName = control.SelectedText.Trim();
        var defaultName = CSharpEncapsulateFieldService.DefaultPropertyName(selectedName);
        var propertyName = InputDialog.Prompt(
            this, "フィールドをカプセル化", "生成するプロパティ名を入力してください:", defaultName);
        if (propertyName is null) return;

        var result = await CSharpSemanticOperations.EncapsulateFieldAsync(
            _solutionModel?.Current, control.FilePath!, control.Text, selection,
            propertyName.Trim(), FindOpenCSharpEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"フィールドのカプセル化: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("フィールドのカプセル化: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyCSharpGeneratedEdit(control, edit, result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private async void RunCSharpExtractField(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            ShowRefactorStatus("フィールド抽出には、式全体を選択してください。");
            return;
        }

        var fieldName = InputDialog.Prompt(
            this, "フィールドを抽出", "新しいフィールド名を入力してください:", "value");
        if (fieldName is null) return;

        var result = await CSharpSemanticOperations.ExtractFieldAsync(
            _solutionModel?.Current, control.FilePath!, control.Text, selection,
            fieldName.Trim(), FindOpenCSharpEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"フィールド抽出: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("フィールド抽出: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyCSharpGeneratedEdit(control, edit, result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private async void RunCSharpMoveTypeToFile(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            ShowRefactorStatus("型の移動には、型名全体を選択してください。");
            return;
        }
        if (control.FilePath is not { Length: > 0 } sourcePath)
            return;

        var selectedName = control.SelectedText.Trim();
        var defaultPath = Path.Combine(
            Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory,
            (selectedName.Length == 0 ? "MovedType" : selectedName) + ".cs");
        var destination = InputDialog.Prompt(
            this, "型を別ファイルへ移動", "移動先ファイルパスを入力してください:", defaultPath);
        if (destination is null) return;
        if (!Path.IsPathRooted(destination))
            destination = Path.Combine(Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory, destination);

        var result = await CSharpSemanticOperations.MoveTypeToFileAsync(
            _solutionModel?.Current, sourcePath, control.Text, selection,
            Path.GetFullPath(destination.Trim()), FindOpenCSharpEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"型の移動: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("型の移動: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyCSharpGeneratedEdit(control, edit, result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private async Task RunCSharpSafeDeleteAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            ShowRefactorStatus("安全な削除には、型またはメンバー名全体を選択してください。");
            return;
        }
        if (control.FilePath is not { Length: > 0 } path)
            return;

        var result = await CSharpSemanticOperations.SafeDeleteAsync(
            _solutionModel?.Current, path, control.Text, selection,
            FindOpenCSharpEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"安全な削除: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("安全な削除: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyCSharpGeneratedEdit(control, edit, result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private async Task RunCSharpPullUpAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            ShowRefactorStatus("基底クラスへの移動には、メンバー全体を選択してください。");
            return;
        }
        if (control.FilePath is not { Length: > 0 } path)
            return;

        var result = await CSharpSemanticOperations.PullUpAsync(
            _solutionModel?.Current, path, control.Text, selection,
            FindOpenCSharpEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"基底クラスへの移動: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("基底クラスへの移動: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyCSharpGeneratedEdit(control, edit, result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private async Task RunCSharpPushDownAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            ShowRefactorStatus("派生クラスへの移動には、メンバー全体を選択してください。");
            return;
        }
        if (control.FilePath is not { Length: > 0 } path)
            return;

        var result = await CSharpSemanticOperations.PushDownAsync(
            _solutionModel?.Current, path, control.Text, selection,
            FindOpenCSharpEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"派生クラスへの移動: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("派生クラスへの移動: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyCSharpGeneratedEdit(control, edit, result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private async Task RunCSharpIntroduceParameterAsync(VimEditorControl control)
    {
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } selection)
        {
            ShowRefactorStatus("パラメーターの導入には、メソッド名全体を選択してください。");
            return;
        }
        if (control.FilePath is not { Length: > 0 } path)
            return;

        var parameterName = InputDialog.Prompt(this, "パラメーターを導入",
            "新しいパラメーター名を入力してください:", "value");
        if (parameterName is null) return;
        var parameterType = InputDialog.Prompt(this, "パラメーターを導入",
            "新しいパラメーターの型を入力してください:", "object");
        if (parameterType is null) return;
        var callSiteArgument = InputDialog.Prompt(this, "パラメーターを導入",
            "呼び出し側へ追加する式を入力してください:", parameterName);
        if (callSiteArgument is null) return;
        var defaultValue = InputDialog.Prompt(this, "パラメーターを導入",
            "ワークスペース外の呼び出し向け既定値（不要なら空欄）:", "", allowEmpty: true);
        if (defaultValue is null) return;

        var result = await CSharpSemanticOperations.IntroduceParameterAsync(
            _solutionModel?.Current, path, control.Text, selection,
            parameterName, parameterType, callSiteArgument, defaultValue,
            FindOpenCSharpEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"パラメーター導入: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("パラメーター導入: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyCSharpGeneratedEdit(control, edit, result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private void AddCodeGenerationItem(
        MenuItem root,
        VimEditorControl control,
        CSharpCodeGenerationKind kind,
        string header)
    {
        var commandId = CSharpCommandIdFor(kind);
        var item = new MenuItem { Header = header, Tag = commandId };
        SetCSharpMenuAutomation(item, commandId);
        item.Click += (_, _) => ExecuteCSharpEditorCommand(commandId, control);
        root.Items.Add(item);
    }

    private async Task RunCSharpNullGuardGenerationAsync(VimEditorControl control)
    {
        if (control.FilePath is not { Length: > 0 } path)
            return;

        var result = await CSharpSemanticOperations.GenerateNullGuardsAsync(
            _solutionModel?.Current, path, control.Text,
            control.Caret.Line, control.Caret.Column, _csharpEditorConfig,
            FindOpenCSharpEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"コード生成: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("コード生成: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyCSharpGeneratedEdit(control, edit, result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private void RunCSharpJsonGeneration(VimEditorControl control)
    {
        if (control.FilePath is not { Length: > 0 } path)
            return;
        if (string.IsNullOrWhiteSpace(control.SelectedText))
        {
            ShowRefactorStatus("JSONからの型生成には、JSONオブジェクトを先に選択してください。");
            return;
        }

        var rootName = InputDialog.Prompt(this, "JSONからC#型を生成",
            "ルート型名を入力してください:", "Root");
        if (rootName is null) return;

        var result = CSharpSemanticOperations.GenerateJsonTypes(
            _solutionModel?.Current,
            path, control.Text, control.Caret.Line, control.Caret.Column,
            control.SelectedText, rootName, _csharpEditorConfig);
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"コード生成: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("コード生成: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyCSharpGeneratedEdit(control, edit, result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private void RunCSharpCodeGeneration(VimEditorControl control, CSharpCodeGenerationKind kind)
        => _ = RunCSharpCodeGenerationAsync(control, kind);

    private async Task RunCSharpCodeGenerationAsync(
        VimEditorControl control, CSharpCodeGenerationKind kind)
    {
        if (control.FilePath is not { Length: > 0 } path)
            return;

        var text = control.Text;
        var line = control.Caret.Line;
        var column = control.Caret.Column;
        var result = await CSharpSemanticOperations.GenerateAsync(
            _solutionModel?.Current, path, text, line, column, kind,
            _csharpEditorConfig, FindOpenCSharpEditorTexts());
        if (result.Error is { Length: > 0 } error)
        {
            ShowRefactorStatus($"コード生成: {error}");
            return;
        }
        if (result.Edit is not { } edit)
        {
            ShowRefactorStatus("コード生成: 適用できる編集がありません。");
            return;
        }

        var applyError = ApplyCSharpGeneratedEdit(control, edit, result.ExpectedTexts);
        ShowRefactorStatus(applyError is null
            ? $"「{result.Summary}」を適用しました。"
            : $"「{result.Summary}」を適用できませんでした: {applyError}");
    }

    private string? ApplyCSharpGeneratedEdit(
        VimEditorControl control,
        Editor.Core.Lsp.LspWorkspaceEdit edit,
        IReadOnlyDictionary<string, string>? expectedTexts = null)
    {
        var originalTexts = FindOpenCSharpEditorTexts().ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (control.FilePath is { Length: > 0 } activePath)
            originalTexts[Path.GetFullPath(activePath)] = control.Text;

        // 新規作成URIは空本文を基準に整形する。既存の非表示文書はディスク本文を
        // 読み、WorkspaceEdit全体を同じpreviewへ渡す。
        foreach (var uri in edit.Changes.Keys)
        {
            if (LspUri.TryToLocalPath(uri) is not { } rawPath) continue;
            var path = Path.GetFullPath(rawPath);
            if (originalTexts.ContainsKey(path)) continue;
            if (File.Exists(path))
            {
                try { originalTexts[path] = File.ReadAllText(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            else if (edit.FileOperations?.Any(operation =>
                         operation.Kind == Editor.Core.Lsp.LspFileOperationKind.Create &&
                         LspUri.SamePath(operation.Uri, uri)) == true)
                originalTexts[path] = "";
        }

        edit = CSharpGeneratedEditFormatter.FormatWorkspace(edit, originalTexts,
            path => CSharpCleanupOptionsFactory.CreateForFile(path, format: true,
                insertFinalNewlineWhenUnset: null, excludeGeneratedCode: false,
                editorConfigService: _csharpEditorConfig));
        return ApplyLspWorkspaceEdit(edit.Changes, edit.DocumentVersions, edit.FileOperations,
            expectedTexts: expectedTexts);
    }

}
