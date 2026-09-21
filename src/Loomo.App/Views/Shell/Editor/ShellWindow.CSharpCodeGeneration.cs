using sk0ya.Loomo.CSharp.Editor;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.App.Views;

public partial class ShellWindow
{
    /// <summary>C# 固有の操作を「C#」サブメニュー1つに畳んで右クリックへ出す。
    /// <para>並び（何を表に出し、何を入れ子へ落とすか）の正本は <see cref="CSharpEditorMenu"/>——
    /// WPF に触らない純関数なので、「開いてすぐ見える段が短いか」「選択が無いときに押せない項目が
    /// 出ていないか」をテストで押さえられる。</para>
    /// <para><b>一覧表にしない。</b>C# の操作は 40 種あるが、右クリックはその目録ではない。
    /// 開いてすぐ見えるのは毎日使うもの（using 整理・抽出／導入／インライン化の代表・よく使う生成）
    /// だけにして、残りは「書き換え」「生成」「まとめて整える」の 3 つの入れ子へ落とす。
    /// 落とした操作も同じ Command ID なので、コマンドパレットとキーバインドからは 1 手で届く。</para>
    /// <para>見出しとキー表記も <see cref="CSharpEditorCommandCatalog"/> から引くので、
    /// コマンドパレット・キーバインドとの表記ゆれが構造的に起きない。</para></summary>
    private void AddCSharpMenuItems(
        System.Windows.Controls.ContextMenu menu,
        VimEditorControl? control)
        => CSharpEditorContextMenuBuilder.AddMenuItems(
            menu, control, GestureFor, ExecuteCSharpEditorCommand, AddCSharpFixAllMenuItems);

    /// <summary>コントロール側の「移動」サブメニューへ、C# の「定義をPeek表示」を足す。
    /// 移動の入口は 1 つ——定義へ移動と Peek がメニューの別々の場所に出ていると、
    /// 「どこかへ行く」操作を探すのに 2 か所見ることになる。</summary>
    private void AddCSharpPeekMenuItem(MenuItem? navigateMenu, VimEditorControl? control)
        => CSharpEditorContextMenuBuilder.AddPeekMenuItem(
            navigateMenu, control, GestureFor, ExecuteCSharpEditorCommand);

    private CSharpEditorRefactoringCoordinator? _csharpEditorRefactoringCoordinator;

    private CSharpEditorRefactoringCoordinator CSharpRefactoring => _csharpEditorRefactoringCoordinator ??= new(
        () => _solutionModel?.Current,
        _csharpEditorConfig,
        (title, prompt, initial, allowEmpty) => InputDialog.Prompt(
            this, title, prompt, initial, allowEmpty: allowEmpty),
        ShowRefactorStatus,
        FindOpenCSharpEditorTexts,
        (edit, expectedTexts) => ApplyLspWorkspaceEdit(
            edit.Changes, edit.DocumentVersions, edit.FileOperations, expectedTexts: expectedTexts));

    private Task RunCSharpOrganizeUsingsAsync(VimEditorControl control)
        => CSharpRefactoring.RunCSharpOrganizeUsingsAsync(control);

    private Task RunCSharpCleanupAsync(VimEditorControl control)
        => CSharpRefactoring.RunCSharpCleanupAsync(control);

    private Task RunCSharpExtractMethodAsync(VimEditorControl control)
        => CSharpRefactoring.RunCSharpExtractMethodAsync(control);

    private Task RunCSharpExtractInterfaceAsync(VimEditorControl control)
        => CSharpRefactoring.RunCSharpExtractInterfaceAsync(control);

    private Task RunCSharpExtractClassAsync(VimEditorControl control)
        => CSharpRefactoring.RunCSharpExtractClassAsync(control);

    private Task RunCSharpIntroduceVariableAsync(VimEditorControl control)
        => CSharpRefactoring.RunCSharpIntroduceVariableAsync(control);

    private Task RunCSharpIntroducePropertyAsync(VimEditorControl control)
        => CSharpRefactoring.RunCSharpIntroducePropertyAsync(control);

    private Task RunCSharpExtractConstantAsync(VimEditorControl control)
        => CSharpRefactoring.RunCSharpExtractConstantAsync(control);

    private Task RunCSharpInlineVariableAsync(VimEditorControl control)
        => CSharpRefactoring.RunCSharpInlineVariableAsync(control);

    private Task RunCSharpInlineMethodAsync(VimEditorControl control)
        => CSharpRefactoring.RunCSharpInlineMethodAsync(control);

    private Task RunCSharpEncapsulateFieldAsync(VimEditorControl control)
        => CSharpRefactoring.RunCSharpEncapsulateFieldAsync(control);

    private Task RunCSharpExtractFieldAsync(VimEditorControl control)
        => CSharpRefactoring.RunCSharpExtractFieldAsync(control);

    private Task RunCSharpMoveTypeToFileAsync(VimEditorControl control)
        => CSharpRefactoring.RunCSharpMoveTypeToFileAsync(control);

    private Task RunCSharpSafeDeleteAsync(VimEditorControl control)
        => CSharpRefactoring.RunCSharpSafeDeleteAsync(control);

    private Task RunCSharpPullUpAsync(VimEditorControl control)
        => CSharpRefactoring.RunCSharpPullUpAsync(control);

    private Task RunCSharpPushDownAsync(VimEditorControl control)
        => CSharpRefactoring.RunCSharpPushDownAsync(control);

    private Task RunCSharpIntroduceParameterAsync(VimEditorControl control)
        => CSharpRefactoring.RunCSharpIntroduceParameterAsync(control);

    private Task RunCSharpNullGuardGenerationAsync(VimEditorControl control)
        => CSharpRefactoring.RunCSharpNullGuardGenerationAsync(control);

    private void RunCSharpJsonGeneration(VimEditorControl control)
        => CSharpRefactoring.RunCSharpJsonGeneration(control);

    private void RunCSharpCodeGeneration(VimEditorControl control, CSharpCodeGenerationKind kind)
        => RunCSharpOperation(CSharpEditorResultMapper.CommandIdFor(kind), () => RunCSharpCodeGenerationAsync(control, kind));

    private Task RunCSharpCodeGenerationAsync(
        VimEditorControl control, CSharpCodeGenerationKind kind)
        => CSharpRefactoring.RunCSharpCodeGenerationAsync(control, kind);



}
