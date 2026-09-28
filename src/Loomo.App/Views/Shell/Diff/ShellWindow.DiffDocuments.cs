namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: Diff の右側で編集する作業ツリーのファイルの持ち主を Editor ペインにする（§24.16）。
/// 右側は Editor のタブの本文を映して一緒に編集するだけで、ファイルは持たない——同じファイルを
/// 別々のバッファで持つと、片方の保存がもう片方に届かず、Diff を閉じるたびに「保存しますか」と聞く羽目になる。
/// </summary>
public partial class ShellWindow {
    /// <summary>Editor のタブの文書に起きたことのうち、BufferChanged では届かないもの（読み込み・保存・閉じる）。</summary>
    private readonly EditorDocumentEvents _editorDocumentEvents = new();
    private IDiffWorkingDocuments? _diffWorkingDocuments;
    private IDiffWorkingDocuments DiffWorkingDocuments => _diffWorkingDocuments ??= new DiffWorkingDocumentsHost(this);

    private sealed class DiffWorkingDocumentsHost(ShellWindow shell) : IDiffWorkingDocuments {
        public EditorDocumentEvents Events => shell._editorDocumentEvents;

        public VimEditorControl? Find(string path) {
            // 復元待ち（未実体化）のタブも対象：未保存の本文を抱えていることがあるので、実体化して映す。
            var tab = shell._editorTabs.FirstOrDefault(t => FilePathRelations.AreEqual(t.PeekFilePath, path));
            return tab?.Control is { IsVirtualDocument: false } control ? control : null;
        }

        public VimEditorControl? Open(string path) {
            // 切替の途中はタブ集合がまだ前のワークスペースを指している（WorkspaceTransitionGate）。
            if (shell._workspaceTransition.IsSwitching || !File.Exists(path))
                return null;
            path = Path.GetFullPath(path);
            var tab = shell.CreateEditorTab();
            shell._editorTabs.Add(tab);
            shell._vm.Tabs.AddEditorTab(tab.Id, path, false, false);
            shell.LoadEditorFile(tab.Control, path);
            // 前面には出さない。ただ Editor ペインにタブが1つも無かったなら、これを選んでおく
            // （アクティブの無いペインにタブだけ増えると、帯に出ても中身が空になる）。
            if (shell._activeEditorTab is null)
                shell.ActivateEditorTab(tab.Id, focusView: false);
            shell.UpdateEditorTab(tab);
            shell.SaveActiveWorkspaceSnapshot();
            return tab.Control;
        }

        public async Task SaveAsync(VimEditorControl document) {
            await shell._editor.SaveFileAsync(document);
            if (shell._editorTabs.FirstOrDefault(t => t.IsRealized && ReferenceEquals(t.Control, document)) is { } tab)
                shell.UpdateEditorTab(tab);
        }

        public bool IsOpen(VimEditorControl document)
            => shell._editorTabs.Any(t => t.IsRealized && ReferenceEquals(t.Control, document));
    }
}
