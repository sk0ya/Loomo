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
        FocusPane);

    private Action<string>? GetTerminalFileDropSender()
    {
        if (_activeTerminalTab?.View is not { } view)
            return null;
        return input => { view.SendTerminalInput(input); };
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
