using sk0ya.Loomo.Services.Lsp;
using sk0ya.Loomo.CSharp.Editor;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;
using sk0ya.Loomo.Services.Refactoring;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: エディタの右クリック「リファクタリング」（設計書 §32）。
///
/// <para>候補は言語サーバーの <c>textDocument/codeAction</c>（<c>only: ["refactor"]</c>）から取る。
/// **選択があれば範囲で、無ければキャレット位置で**問い合わせる——「メソッドの抽出」のように
/// 範囲そのものが対象のリファクタリングは、1点の要求では候補に出ない。</para>
///
/// <para>メニューは同期的に組み立てる必要がある一方、候補の取得も「シグネチャの変更が使えるか」の
/// 判定（＝構文解析）も即答できないので、**サブメニューを開いた時点で中身を作る**
/// （<see cref="MenuItem.SubmenuOpened"/>）。開くまで LSP を叩かないので、右クリックしただけで
/// 言語サーバーへ問い合わせが飛ぶこともない。**使えない項目は出さない**——押しても
/// 「できません」としか返らない項目を並べない。</para>
///
/// <para>適用は3経路ある。(1) edit を持つアクションはそのまま適用、(2) <c>data</c> だけの未解決
/// アクションは <c>codeAction/resolve</c> してから適用（Roslyn はこちら）、(3) command 型は
/// <c>workspace/executeCommand</c> で実行し、編集はサーバー起点の <c>workspace/applyEdit</c> で
/// 返ってくる（tsserver 系がこちら）。</para></summary>
public partial class ShellWindow
{
    private RefactoringMenuPresenter? _refactoringMenuPresenter;
    private RefactoringMenuPresenter RefactoringActionsMenu
        => _refactoringMenuPresenter ??= new RefactoringMenuPresenter(
            _lspManagement, () => _lspWorkspace.ServerStatuses, GestureFor,
            (command, control) => ExecuteCSharpEditorCommand(command, control),
            control => {
                if (ActiveCSharpEditor(control) is not null)
                    ExecuteCSharpEditorCommand(CSharpEditorCommandCatalog.Rename, control);
                else
                    control.ExecuteCommand("Rename");
            }, ApplyRefactoringAsync);
    private LspApplyEditPresenter? _lspApplyEditPresenter;
    private LspApplyEditPresenter LspApplyEdit
        => _lspApplyEditPresenter ??= new LspApplyEditPresenter(
            Dispatcher,
            edit => ApplyLspWorkspaceEdit(
                edit.Changes, edit.DocumentVersions, edit.FileOperations, expectedTexts: edit.ExpectedTexts),
            ShowRefactorStatus);

    private void InitializeRefactoringWiring()
        => _lspWorkspace.ApplyEditRequested += OnLspServerApplyEditRequested;

    private void AddRefactorMenuItems(ContextMenu menu, VimEditorControl? control)
    {
        if (RefactoringActionsMenu.BuildMenuItem(control) is { } item)
            menu.Items.Add(item);
    }

    /// <summary>選ばれたリファクタリングを適用する。未解決なら解決し、コマンド型なら実行する。</summary>
    private Task ApplyRefactoringAsync(VimEditorControl control, RefactoringItem item)
        => RefactoringActionCoordinator.ApplyAsync(
            control.LspDocument,
            item,
            (refactoring, changes) => ExtractedSymbolRenamePresenter.Rename(this, refactoring, changes),
            edit => ApplyLspWorkspaceEdit(edit.Changes, edit.DocumentVersions, edit.FileOperations,
                expectedTexts: edit.ExpectedTexts),
            ShowRefactorStatus);

    /// <summary>サーバー起点の <c>workspace/applyEdit</c>。<b>LSP の読み取りスレッドで発火し、
    /// ここが戻るまでサーバーは応答を待って止まっている</b>ので、UI スレッドへは同期的に入る
    /// （呼び出し元の UI は LSP 呼び出しを await 中＝ブロックしていないので、これで詰まらない）。</summary>
    private void OnLspServerApplyEditRequested(object? sender, LspApplyEditEventArgs e)
        => LspApplyEdit.Handle(sender, e);

    private void ShowRefactorStatus(string message)
        => EditorSharedStatusBar?.UpdateStatus(message);

    /// <summary>C# のシグネチャ変更（LSP には無いので自前・§32.5）。
    /// メニューを開いた時点で読み取った宣言をそのまま使う（読み直すとキャレットが動いている）。</summary>
    private async Task ChangeSignatureAsync(MethodSignature signature)
    {
        var folders = _workspace.Folders;
        if (folders.Count == 0)
        {
            ShowRefactorStatus("ワークスペースが開かれていません。");
            return;
        }

        var dialog = new ChangeSignatureDialog(signature) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var currentText = FindOpenEditorText(signature.FilePath);
        await CSharpSignatureChangeCoordinator.ApplyAsync(
            signature, dialog.Result!, _lspWorkspace, folders, FindOpenEditorText, currentText,
            _solutionModel?.Current, FindOpenCSharpEditorTexts(), _csharpEditorConfig,
            (changes, expectedTexts) => ApplyLspWorkspaceEdit(
                changes, documentVersions: null, fileOperations: null, expectedTexts: expectedTexts),
            ShowRefactorStatus);
    }

    /// <summary>開いているタブが持つ最新テキスト（未保存を含む）。開いていなければ null。</summary>
    private string? FindOpenEditorText(string path)
        => _editorTabs.FirstOrDefault(tab =>
                tab.IsRealized && EditorPathMatches(tab.Control, path))
            ?.Control.Text;

    /// <summary>意味モデルを作るときに、開いているC#タブの未保存本文をすべて渡す。
    /// 対象ファイルだけを上書きすると、別ファイルの未保存method group／呼び出しを
    /// 古いディスク内容で安全確認してしまう。</summary>
    private IReadOnlyDictionary<string, string> FindOpenCSharpEditorTexts()
        => _editorTabs
            .Where(tab => tab.IsRealized &&
                string.Equals(Path.GetExtension(tab.Control.FilePath ?? ""), ".cs",
                    StringComparison.OrdinalIgnoreCase) &&
                tab.Control.FilePath is { Length: > 0 })
            .GroupBy(tab => Path.GetFullPath(tab.Control.FilePath!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Control.Text,
                StringComparer.OrdinalIgnoreCase);
    /// <summary>コマンドの実効キー表記（利用者の割当を優先し、無ければカタログの既定、
    /// どちらも無ければ空文字＝キー表記の欄を出さない）。</summary>
    private string GestureFor(string commandId)
        => DescribeBinding(commandId) is { Length: > 0 } effective
            ? effective
            : CSharpEditorMenu.GestureFor(commandId) ?? "";
}
