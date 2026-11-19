using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

public partial class ShellWindow
{
    private FileDropInteractionController? _fileDropController;
    private FileDropInteractionController FileDropController => _fileDropController ??= new(
        EditorPane, DiffPane, SearchPane, GitPane, TerminalPane,
        OpenFileInNewEditorTabAsync,
        GetTerminalFileDropSender,
        _vm.SearchPanel.SetSearchRoot,
        path => _vm.GitSession.ShowPathHistoryAsync(path),
        CompareFilesInDiff,
        EnsurePaneVisibleOrSwapTopLeft,
        FocusPane,
        (path, line, column) => OpenPathInEditorAsync(path, line, column));

    /// <summary>ターミナルへ落としたファイルの入れ方。シェルには引用付きのパス、Claude Code が動いているタブには
    /// <c>@参照</c>（§24.3.1）——シェルの引用は Claude には意味が無く、@ なら Claude がファイルとして読む。</summary>
    private Action<IReadOnlyList<string>>? GetTerminalFileDropSender()
    {
        if (_activeTerminalTab is not { } tab)
            return null;
        var toClaude = _paneActivity.AgentTabFor(tab.Id) == tab.Id;
        return paths => tab.View.SendTerminalInput(toClaude
            ? string.Concat(paths.Select(path => ClaudeCodeReference.Format(path, tab.View.WorkingDirectory, null, null)))
            : string.Join(" ", paths.Select(FileDragDrop.PowerShellQuote)));
    }

    private void CancelFileDropOperations() => _fileDropController?.CancelOperations();

    private void OnFileDropDragOver(object sender, DragEventArgs e)
        => FileDropController.OnDragOver(sender, e);

    private async void OnEditorFileDrop(object sender, DragEventArgs e)
    {
        if (TryInsertMarkdownDropLinks(e))
            return;
        await FileDropController.OnEditorDropAsync(sender, e);
    }

    /// <summary>Markdown のタブの本文へファイルを落としたら、落とした所へリンクを入れる（画像なら画像の参照・§24.110 の 3）。
    /// Shift を押しながら落としたとき・無題のタブ・本文の外（ガター等）に落としたときは false（いままでどおりタブで開く）。
    /// 入れた文字は取り消し 1 回で戻る（<c>TryApplyLspTextEdits</c>）。</summary>
    private bool TryInsertMarkdownDropLinks(DragEventArgs e)
    {
        if (e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey))
            return false;
        if (WpfTreeTraversal.FindAncestor<VimEditorControl>(e.OriginalSource as DependencyObject) is not { } editor
            || !MarkdownDropLinks.IsMarkdownDocument(editor.FilePath))
            return false;
        var paths = FileDragDrop.TryGetPaths(e.Data).Where(File.Exists).ToArray();
        if (paths.Length == 0
            || !editor.TryGetInsertionPositionFromPoint(e.GetPosition(editor), out var line, out var column))
            return false;
        var sourceLine = paths.Length == 1 && FileDragDrop.TryGetSourceLocation(e.Data, out var l, out _) ? l : 0;
        var text = MarkdownDropLinks.Build(editor.FilePath!, paths, sourceLine);
        var at = new Editor.Core.Lsp.LspPosition(line, column);
        if (!editor.TryApplyLspTextEdits([new Editor.Core.Lsp.LspTextEdit(new Editor.Core.Lsp.LspRange(at, at), text)], expectedVersion: null, out var error)) {
            ToastService.Error($"リンクを入れられませんでした: {error}");
            e.Handled = true;
            return true;
        }
        e.Handled = true;
        editor.Focus();
        return true;
    }

    private void OnTerminalFileDrop(object sender, DragEventArgs e)
        => FileDropController.OnTerminalDrop(sender, e);

    private void OnSearchFileDrop(object sender, DragEventArgs e)
        => FileDropController.OnSearchDrop(sender, e);

    private void OnDiffFileDrop(object sender, DragEventArgs e)
        => FileDropController.OnDiffDrop(sender, e);

    private async void OnGitFileDrop(object sender, DragEventArgs e)
        => await FileDropController.OnGitDropAsync(sender, e);
}
