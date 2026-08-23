using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class ShellWindow
{
    private CancellationTokenSource? _fileDropCts;

    private CancellationTokenSource BeginFileDropOperation()
    {
        _fileDropCts?.Cancel();
        _fileDropCts?.Dispose();
        return _fileDropCts = new CancellationTokenSource();
    }

    private void EndFileDropOperation(CancellationTokenSource operation)
    {
        if (!ReferenceEquals(_fileDropCts, operation)) return;
        _fileDropCts = null;
        operation.Dispose();
    }

    private void CancelFileDropOperations()
    {
        _fileDropCts?.Cancel();
        _fileDropCts?.Dispose();
        _fileDropCts = null;
    }

    private void OnFileDropDragOver(object sender, DragEventArgs e)
    {
        var paths = FileDragDrop.TryGetPaths(e.Data);
        e.Effects = DropEffectFor(sender, paths);
        e.Handled = true;
    }

    private DragDropEffects DropEffectFor(object sender, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return DragDropEffects.None;
        if (ReferenceEquals(sender, EditorPane))
            return paths.Any(File.Exists) ? DragDropEffects.Copy : DragDropEffects.None;
        if (ReferenceEquals(sender, DiffPane))
            return paths.Count is 1 or 2 && paths.All(File.Exists) ? DragDropEffects.Copy : DragDropEffects.None;
        if (ReferenceEquals(sender, SearchPane) || ReferenceEquals(sender, GitPane)
            || ReferenceEquals(sender, TerminalPane))
            return DragDropEffects.Copy;
        return DragDropEffects.None;
    }

    private async void OnEditorFileDrop(object sender, DragEventArgs e)
    {
        var paths = FileDragDrop.TryGetPaths(e.Data).Where(File.Exists).ToArray();
        e.Handled = true;
        if (DropEffectFor(sender, paths) == DragDropEffects.None) return;
        var operation = BeginFileDropOperation();
        try
        {
            foreach (var path in paths)
            {
                operation.Token.ThrowIfCancellationRequested();
                await OpenFileInNewEditorTabAsync(path);
            }
            if (!operation.Token.IsCancellationRequested)
                FocusPane(PaneKind.Editor);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested) { }
        finally { EndFileDropOperation(operation); }
    }

    private void OnTerminalFileDrop(object sender, DragEventArgs e)
    {
        var paths = FileDragDrop.TryGetPaths(e.Data);
        e.Handled = true;
        if (DropEffectFor(sender, paths) == DragDropEffects.None || _activeTerminalTab?.View is not { } view)
            return;
        var operation = BeginFileDropOperation();
        try
        {
            var input = string.Join(" ", paths.Select(FileDragDrop.PowerShellQuote));
            if (input.Length == 0 || operation.Token.IsCancellationRequested) return;
            view.SendTerminalInput(input);
            FocusPane(PaneKind.Terminal);
        }
        finally { EndFileDropOperation(operation); }
    }

    private void OnSearchFileDrop(object sender, DragEventArgs e)
    {
        var paths = FileDragDrop.TryGetPaths(e.Data);
        e.Handled = true;
        if (DropEffectFor(sender, paths) == DragDropEffects.None) return;
        var operation = BeginFileDropOperation();
        try
        {
            var root = FileDragDrop.CommonDirectory(paths);
            if (root is null || operation.Token.IsCancellationRequested) return;
            _vm.SearchPanel.SetSearchRoot(root);
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Search);
            FocusPane(PaneKind.Search);
        }
        finally { EndFileDropOperation(operation); }
    }

    private void OnDiffFileDrop(object sender, DragEventArgs e)
    {
        var paths = FileDragDrop.TryGetPaths(e.Data).Where(File.Exists).ToArray();
        e.Handled = true;
        if (DropEffectFor(sender, paths) == DragDropEffects.None) return;
        var operation = BeginFileDropOperation();
        try
        {
            if (!operation.Token.IsCancellationRequested)
                CompareFilesInDiff(new FileCompareRequest(paths[0], paths.Length == 2 ? paths[1] : null));
        }
        finally { EndFileDropOperation(operation); }
    }

    private async void OnGitFileDrop(object sender, DragEventArgs e)
    {
        var paths = FileDragDrop.TryGetPaths(e.Data);
        e.Handled = true;
        if (DropEffectFor(sender, paths) == DragDropEffects.None) return;
        var operation = BeginFileDropOperation();
        try
        {
            var path = FileDragDrop.CommonDirectory(paths);
            if (path is null) return;
            await _vm.GitSession.ShowPathHistoryAsync(path);
            if (operation.Token.IsCancellationRequested) return;
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Git);
            FocusPane(PaneKind.Git);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested) { }
        finally { EndFileDropOperation(operation); }
    }
}
