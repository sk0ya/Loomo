using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>フォルダーツリーのファイル操作を既存の ViewModel／共有バッチ処理へ委譲する。</summary>
/// <summary>フォルダーツリーの選択から作ったクイックアクセスメニュー状態。</summary>
internal readonly record struct FolderTreeQuickAccessMenuState(
    bool HasTarget,
    QuickAccessMenuState? ReadyState);

internal static class FolderTreeFileCommandController
{
    /// <summary>実フォルダーが選択されているときだけ照会し、snapshot準備済みなら表示可否も返す。</summary>
    internal static FolderTreeQuickAccessMenuState GetQuickAccessMenuState(
        FolderTreeViewModel vm,
        IReadOnlyList<FileNodeViewModel> selection)
    {
        var hasTarget = vm.QuickAccess.IsAvailable &&
            selection.Any(item => item.IsDirectory && !item.IsShellItem);
        var ready = hasTarget && vm.QuickAccess.IsSnapshotReady;
        return new(hasTarget, ready ? GetQuickAccessCapabilities(vm, selection) : null);
    }

    private static QuickAccessMenuState GetQuickAccessCapabilities(
        FolderTreeViewModel vm,
        IReadOnlyList<FileNodeViewModel> selection)
        => new(vm.CanPinToQuickAccess(selection), vm.CanUnpinFromQuickAccess(selection));

    /// <summary>Explorerの照会に失敗した場合は、他のメニュー操作へ影響させず項目を隠す。</summary>
    internal static async Task<QuickAccessMenuState?> RefreshQuickAccessMenuStateAsync(
        FolderTreeViewModel vm,
        IReadOnlyList<FileNodeViewModel> selection)
    {
        try
        {
            if (!await vm.QuickAccess.RefreshAsync())
                return null;
            return GetQuickAccessCapabilities(vm, selection);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>選択したフォルダーをクイックアクセスへピン留め／解除する。</summary>
    internal static async Task<QuickAccessBatchResult?> TrySetQuickAccessPinnedAsync(
        FolderTreeViewModel vm,
        IReadOnlyList<FileNodeViewModel> selection,
        bool pin)
    {
        try
        {
            return pin
                ? await vm.PinToQuickAccessAsync(selection)
                : await vm.UnpinFromQuickAccessAsync(selection);
        }
        catch (Exception)
        {
            // Explorer に聞けない環境では、他のメニュー操作へ影響させず静かに終了する。
            return null;
        }
    }

    internal static string? CreateEntry(
        FolderTreeViewModel vm,
        FileNodeViewModel? contextNode,
        bool isDirectory,
        Func<bool, string?> promptName)
    {
        var parent = vm.GetTargetDirectory(contextNode);
        if (parent is null) return null;
        var name = promptName(isDirectory);
        if (name is null) return null;
        try { return vm.CreateEntry(parent, name, isDirectory); }
        catch (InvalidOperationException ex)
        {
            ToastService.Error(ex.Message);
            return null;
        }
    }

    internal static string? RenameEntry(
        FolderTreeViewModel? vm,
        FileNodeViewModel? node,
        Func<FileNodeViewModel, string?> promptName)
    {
        if (vm is null || node is null) return null;
        var newName = promptName(node);
        if (newName is null) return null;
        try { return vm.RenameEntry(node, newName); }
        catch (InvalidOperationException ex)
        {
            ToastService.Error(ex.Message);
            return null;
        }
    }

    internal static string? DuplicateEntries(
        FolderTreeViewModel? vm,
        IReadOnlyList<FileNodeViewModel> nodes)
        => vm is null || nodes.Count == 0
            ? null
            : FileEntryBatchExecutor.Execute(nodes, vm.BeginFileOperationBatch, vm.DuplicateEntry);

    internal static bool DeleteEntries(
        FolderTreeViewModel? vm,
        IReadOnlyList<FileNodeViewModel> nodes,
        Func<string, bool> confirm,
        Action onConfirmed)
    {
        if (vm is null || nodes.Count == 0)
            return false;

        var message = FileContextMenuPolicy.FormatDeleteConfirmation(
            nodes.Count,
            nodes.Count == 1 && nodes[0].IsDirectory,
            nodes.Count == 1 ? nodes[0].Name : null);
        if (!confirm(message))
            return false;

        onConfirmed();
        _ = FileEntryBatchExecutor.Execute(nodes, vm.BeginFileOperationBatch, node =>
        {
            vm.DeleteEntry(node);
            return null;
        });
        return true;
    }

    internal static void ExecuteShellAction(
        FolderTreeViewModel? vm,
        ShellFileAction action,
        IEnumerable<FileNodeViewModel> nodes,
        bool filesOnly = false)
    {
        if (vm is null) return;
        var paths = nodes
            .Where(node => !filesOnly || !node.IsDirectory)
            .Select(node => node.FullPath)
            .ToArray();
        if (paths.Length == 0) return;

        var result = vm.ShellOperations.Execute(action, paths);
        if (!result.IsCancelled && result.FailedPaths.Count > 0)
            ToastService.Error(result.ErrorMessage ?? "Shell 操作を実行できませんでした。");
    }

    internal static void CopyPaths(IEnumerable<FileNodeViewModel> nodes)
        => FileClipboard.CopyLines(nodes.Select(node => node.FullPath));

    internal static void CopyFiles(IEnumerable<FileNodeViewModel> nodes, bool move)
        => FileClipboard.SetFiles(nodes.Where(node => !node.IsShellItem).Select(node => node.FullPath), move);

    internal static void CopyRelativePaths(
        FolderTreeViewModel? vm, IEnumerable<FileNodeViewModel> nodes)
    {
        if (vm is not null)
            FileClipboard.CopyLines(nodes.Select(vm.RelativePathFor));
    }

    internal static void CopyNames(IEnumerable<FileNodeViewModel> nodes)
        => FileClipboard.CopyLines(nodes.Select(node => node.Name));

    private static FilePropertiesTarget[] CreatePropertiesTargets(IEnumerable<FileNodeViewModel> nodes)
        => nodes.Select(node => new FilePropertiesTarget(node.FullPath, node.IsDirectory)).ToArray();

    internal static Task<FilePropertiesResult> ReadPropertiesAsync(
        FolderTreeViewModel vm, IEnumerable<FileNodeViewModel> nodes, CancellationToken cancellationToken)
    {
        var targets = CreatePropertiesTargets(nodes);
        return Task.Run(() => vm.FileProperties.ReadMany(targets, cancellationToken), cancellationToken);
    }

    internal static async Task<string?> CompressEntriesAsync(
        FolderTreeViewModel vm, IReadOnlyList<FileNodeViewModel> nodes, CancellationToken cancellationToken)
    {
        if (nodes.Count == 0)
            return null;

        try { return await vm.CompressEntriesAsync(nodes, cancellationToken); }
        catch (InvalidOperationException ex)
        {
            ToastService.Error(ex.Message);
            return null;
        }
    }

    internal static async Task<FileOperationResult?> RunHistoryStepAsync(
        FolderTreeViewModel? vm, bool undo)
    {
        if (vm is null || (undo ? !vm.History.CanUndo : !vm.History.CanRedo))
            return null;

        try
        {
            var result = undo
                ? vm.UndoFileOperation()
                : await vm.RedoFileOperationAsync();
            ToastService.Info($"{(undo ? "元に戻しました" : "やり直しました")}: {result.Description}");
            return result;
        }
        catch (InvalidOperationException ex)
        {
            ToastService.Error(ex.Message);
            return null;
        }
    }

    internal static void AddToGitignore(FolderTreeViewModel? vm, FileNodeViewModel node)
    {
        if (vm is null)
            return;
        try { vm.AddToGitignore(node); }
        catch (InvalidOperationException ex) { ToastService.Error(ex.Message); }
    }

    internal static FilePasteBatchOutcome PasteFromClipboard(
        FolderTreeViewModel? vm,
        FileNodeViewModel? contextNode,
        Func<FileConflictContext, FileConflictDecision> resolveConflict)
    {
        if (vm is null || !FileClipboard.ContainsFiles())
            return default;
        var targetDirectory = vm.GetTargetDirectory(contextNode);
        if (targetDirectory is null)
            return default;

        var move = FileClipboard.PrefersMove();
        var outcome = FilePasteBatchExecutor.Execute(
            vm.BeginFileOperationBatch,
            FileClipboard.GetFiles(),
            move,
            (source, shouldMove, resolver) => vm.PasteEntry(targetDirectory, source, shouldMove, resolver),
            resolveConflict);
        if (move && outcome.Completed && !outcome.Cancelled)
            FileClipboard.Clear();
        return outcome;
    }
}

/// <summary>フォルダーツリーの長時間コマンドを単一実行に保ち、ビュー終了時に中止する。</summary>
internal sealed class FolderTreeFileOperationSession
{
    private CancellationTokenSource? _propertiesLoad;
    private CancellationTokenSource? _compression;
    private bool _propertiesBusy;

    public bool IsLoadingProperties => _propertiesBusy;
    public bool IsCompressing => _compression is not null;

    public async Task<FilePropertiesResult?> ReadPropertiesAsync(
        FolderTreeViewModel vm, IReadOnlyList<FileNodeViewModel> nodes)
    {
        if (_propertiesBusy)
            return null;
        _propertiesBusy = true;
        var operation = new CancellationTokenSource();
        _propertiesLoad = operation;
        try
        {
            return await FolderTreeFileCommandController.ReadPropertiesAsync(vm, nodes, operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (ReferenceEquals(_propertiesLoad, operation))
                _propertiesLoad = null;
            operation.Dispose();
        }
    }

    public void CompleteProperties() => _propertiesBusy = false;

    public async Task<string?> CompressEntriesAsync(
        FolderTreeViewModel vm, IReadOnlyList<FileNodeViewModel> nodes)
    {
        if (_compression is not null)
            return null;
        var operation = new CancellationTokenSource();
        _compression = operation;
        try
        {
            return await FolderTreeFileCommandController.CompressEntriesAsync(vm, nodes, operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (ReferenceEquals(_compression, operation))
                _compression = null;
            operation.Dispose();
        }
    }

    public void CancelPending()
    {
        _propertiesLoad?.Cancel();
        _compression?.Cancel();
    }
}
