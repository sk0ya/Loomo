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
        => await FileDropController.OnEditorDropAsync(sender, e);

    private void OnTerminalFileDrop(object sender, DragEventArgs e)
        => FileDropController.OnTerminalDrop(sender, e);

    private void OnSearchFileDrop(object sender, DragEventArgs e)
        => FileDropController.OnSearchDrop(sender, e);

    private void OnDiffFileDrop(object sender, DragEventArgs e)
        => FileDropController.OnDiffDrop(sender, e);

    private async void OnGitFileDrop(object sender, DragEventArgs e)
        => await FileDropController.OnGitDropAsync(sender, e);
}
