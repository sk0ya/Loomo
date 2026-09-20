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

    // 前の投下を打ち切るのは Cancel だけにし、Dispose はその投下自身の finally に任せる。
    // ここで捨ててしまうと、await の途中で止まっている前のハンドラーが再開したときに
    // operation.Token（破棄済み CTS のプロパティ）を読んで ObjectDisposedException になる
    // ——async void なので、それはそのまま未処理例外になる。
    private CancellationTokenSource BeginFileDropOperation()
    {
        _fileDropCts?.Cancel();
        return _fileDropCts = new CancellationTokenSource();
    }

    private void EndFileDropOperation(CancellationTokenSource operation)
    {
        // 自分が最後の投下なら「進行中なし」に戻す。追い越されていても、自分の CTS は必ず捨てる。
        if (ReferenceEquals(_fileDropCts, operation))
            _fileDropCts = null;
        operation.Dispose();
    }

    private void CancelFileDropOperations()
    {
        _fileDropCts?.Cancel();
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
        // トークンは await をまたぐので、CTS からではなく最初に 1 度だけ取り出して使う。
        var token = operation.Token;
        try
        {
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                await OpenFileInNewEditorTabAsync(path);
            }
            if (!token.IsCancellationRequested)
                FocusPane(PaneKind.Editor);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { EndFileDropOperation(operation); }
    }

    private void OnTerminalFileDrop(object sender, DragEventArgs e)
    {
        var paths = FileDragDrop.TryGetPaths(e.Data);
        e.Handled = true;
        if (DropEffectFor(sender, paths) == DragDropEffects.None || _activeTerminalTab?.View is not { } view)
            return;
        var operation = BeginFileDropOperation();
        var token = operation.Token;
        try
        {
            var input = string.Join(" ", paths.Select(FileDragDrop.PowerShellQuote));
            if (input.Length == 0 || token.IsCancellationRequested) return;
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
        var token = operation.Token;
        try
        {
            var root = FileDragDrop.CommonDirectory(paths);
            if (root is null || token.IsCancellationRequested) return;
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
        var token = operation.Token;
        try
        {
            if (!token.IsCancellationRequested)
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
        // トークンは await をまたぐので、CTS からではなく最初に 1 度だけ取り出して使う。
        var token = operation.Token;
        try
        {
            var path = FileDragDrop.CommonDirectory(paths);
            if (path is null) return;
            await _vm.GitSession.ShowPathHistoryAsync(path);
            if (token.IsCancellationRequested) return;
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Git);
            FocusPane(PaneKind.Git);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { EndFileDropOperation(operation); }
    }
}
