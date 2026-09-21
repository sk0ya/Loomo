using System.IO;
using System.Windows;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>各ペインへのファイルドロップ判定と処理をまとめる。</summary>
internal sealed class FileDropInteractionController
{
    private readonly object _editorPane;
    private readonly object _diffPane;
    private readonly object _searchPane;
    private readonly object _gitPane;
    private readonly object _terminalPane;
    private readonly Func<string, Task> _openEditorFile;
    private readonly Func<Action<string>?> _getTerminalInputSender;
    private readonly Action<string> _setSearchRoot;
    private readonly Func<string, Task> _showPathHistory;
    private readonly Action<FileCompareRequest> _compareFiles;
    private readonly Action<PaneKind> _ensurePaneVisible;
    private readonly Action<PaneKind> _focusPane;
    private CancellationTokenSource? _dropCts;

    internal FileDropInteractionController(
        object editorPane,
        object diffPane,
        object searchPane,
        object gitPane,
        object terminalPane,
        Func<string, Task> openEditorFile,
        Func<Action<string>?> getTerminalInputSender,
        Action<string> setSearchRoot,
        Func<string, Task> showPathHistory,
        Action<FileCompareRequest> compareFiles,
        Action<PaneKind> ensurePaneVisible,
        Action<PaneKind> focusPane)
    {
        _editorPane = editorPane;
        _diffPane = diffPane;
        _searchPane = searchPane;
        _gitPane = gitPane;
        _terminalPane = terminalPane;
        _openEditorFile = openEditorFile;
        _getTerminalInputSender = getTerminalInputSender;
        _setSearchRoot = setSearchRoot;
        _showPathHistory = showPathHistory;
        _compareFiles = compareFiles;
        _ensurePaneVisible = ensurePaneVisible;
        _focusPane = focusPane;
    }

    internal void CancelOperations()
    {
        _dropCts?.Cancel();
        _dropCts = null;
    }

    internal void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DropEffectFor(sender, FileDragDrop.TryGetPaths(e.Data));
        e.Handled = true;
    }

    internal async Task OnEditorDropAsync(object sender, DragEventArgs e)
    {
        var paths = FileDragDrop.TryGetPaths(e.Data).Where(File.Exists).ToArray();
        e.Handled = true;
        if (DropEffectFor(sender, paths) == DragDropEffects.None) return;
        var operation = BeginOperation();
        var token = operation.Token;
        try
        {
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                await _openEditorFile(path);
            }
            if (!token.IsCancellationRequested)
                _focusPane(PaneKind.Editor);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { EndOperation(operation); }
    }

    internal void OnTerminalDrop(object sender, DragEventArgs e)
    {
        var paths = FileDragDrop.TryGetPaths(e.Data);
        e.Handled = true;
        if (DropEffectFor(sender, paths) == DragDropEffects.None) return;
        var sendTerminalInput = _getTerminalInputSender();
        if (sendTerminalInput is null) return;
        var operation = BeginOperation();
        var token = operation.Token;
        try
        {
            var input = string.Join(" ", paths.Select(FileDragDrop.PowerShellQuote));
            if (input.Length == 0 || token.IsCancellationRequested) return;
            sendTerminalInput(input);
            _focusPane(PaneKind.Terminal);
        }
        finally { EndOperation(operation); }
    }

    internal void OnSearchDrop(object sender, DragEventArgs e)
    {
        var paths = FileDragDrop.TryGetPaths(e.Data);
        e.Handled = true;
        if (DropEffectFor(sender, paths) == DragDropEffects.None) return;
        var operation = BeginOperation();
        var token = operation.Token;
        try
        {
            var root = FileDragDrop.CommonDirectory(paths);
            if (root is null || token.IsCancellationRequested) return;
            _setSearchRoot(root);
            _ensurePaneVisible(PaneKind.Search);
            _focusPane(PaneKind.Search);
        }
        finally { EndOperation(operation); }
    }

    internal void OnDiffDrop(object sender, DragEventArgs e)
    {
        var paths = FileDragDrop.TryGetPaths(e.Data).Where(File.Exists).ToArray();
        e.Handled = true;
        if (DropEffectFor(sender, paths) == DragDropEffects.None) return;
        var operation = BeginOperation();
        var token = operation.Token;
        try
        {
            if (!token.IsCancellationRequested)
                _compareFiles(new FileCompareRequest(paths[0], paths.Length == 2 ? paths[1] : null));
        }
        finally { EndOperation(operation); }
    }

    internal async Task OnGitDropAsync(object sender, DragEventArgs e)
    {
        var paths = FileDragDrop.TryGetPaths(e.Data);
        e.Handled = true;
        if (DropEffectFor(sender, paths) == DragDropEffects.None) return;
        var operation = BeginOperation();
        var token = operation.Token;
        try
        {
            var path = FileDragDrop.CommonDirectory(paths);
            if (path is null) return;
            await _showPathHistory(path);
            if (token.IsCancellationRequested) return;
            _ensurePaneVisible(PaneKind.Git);
            _focusPane(PaneKind.Git);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { EndOperation(operation); }
    }

    private DragDropEffects DropEffectFor(object sender, IReadOnlyList<string> paths)
    {
        var surface = ReferenceEquals(sender, _editorPane) ? FileDropSurface.Editor
            : ReferenceEquals(sender, _diffPane) ? FileDropSurface.Diff
            : ReferenceEquals(sender, _searchPane) ? FileDropSurface.Search
            : ReferenceEquals(sender, _gitPane) ? FileDropSurface.Git
            : ReferenceEquals(sender, _terminalPane) ? FileDropSurface.Terminal
            : FileDropSurface.Unsupported;
        return FileDropPolicy.CanAccept(surface, paths) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private CancellationTokenSource BeginOperation()
    {
        _dropCts?.Cancel();
        return _dropCts = new CancellationTokenSource();
    }

    private void EndOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_dropCts, operation))
            _dropCts = null;
        operation.Dispose();
    }
}
