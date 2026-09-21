using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

internal readonly record struct QuickAccessMenuState(bool CanPin, bool CanUnpin);

/// <summary>ファイル一覧のファイル操作コマンドを既存 ViewModel へ委譲する。</summary>
internal sealed class FilesColumnCommandController
{
    private readonly Func<FileConflictContext, FileConflictDecision> _resolveConflict;

    public FilesColumnCommandController(
        Func<FileConflictContext, FileConflictDecision> resolveConflict)
        => _resolveConflict = resolveConflict;

    public void CreateEntry(
        FilesColumnViewModel? vm,
        bool isDirectory,
        Func<bool, string?> promptName)
    {
        if (vm?.TargetDirectory is null)
            return;
        var name = promptName(isDirectory);
        if (name is null)
            return;

        try
        {
            var created = vm.CreateEntry(name, isDirectory);
            if (!isDirectory)
                vm.OpenEntry(vm.Entries.FirstOrDefault(
                    entry => string.Equals(entry.FullPath, created, StringComparison.OrdinalIgnoreCase)));
        }
        catch (InvalidOperationException ex)
        {
            ToastService.Error(ex.Message);
        }
    }

    public void RenameEntry(
        FilesColumnViewModel? vm,
        FileEntryViewModel? entry,
        Func<FileEntryViewModel, string?> promptName)
    {
        if (entry is null || vm is null)
            return;
        var newName = promptName(entry);
        if (newName is null)
            return;

        try { vm.RenameEntry(entry, newName); }
        catch (InvalidOperationException ex) { ToastService.Error(ex.Message); }
    }

    internal string? PinTarget(FilesColumnViewModel? vm, FileEntryViewModel? selected)
        => selected is { IsDirectory: true } entry ? entry.FullPath : vm?.CurrentFolder;

    public void CompareSelectedFiles(
        FilesColumnViewModel? vm, IReadOnlyList<FileEntryViewModel> selection)
    {
        var files = selection.Where(entry => !entry.IsDirectory).ToList();
        if (vm is not null && FileContextMenuPolicy.HasExactlyTwoFiles(files.Count))
            vm.RequestCompare(files[0].FullPath, files[1].FullPath);
    }

    internal FileContextMenuState CreateContextMenuState(
        FilesColumnViewModel vm,
        IReadOnlyList<FileEntryViewModel> selection)
    {
        var single = selection.Count == 1 ? selection[0] : null;
        var singleIsDirectory = single is { IsDirectory: true };
        var quickAccessReady = vm.QuickAccess.IsSnapshotReady;
        var pinTarget = PinTarget(vm, single);
        return new FileContextMenuState(
            selection.Count,
            selection.Count(entry => !entry.IsDirectory),
            singleIsDirectory,
            single is { IsHtml: true },
            singleIsDirectory && vm.CanSearchIn(single!.FullPath),
            vm.CanPin(pinTarget),
            vm.IsPinned(pinTarget),
            quickAccessReady,
            quickAccessReady && vm.CanPinToQuickAccess(selection),
            quickAccessReady && vm.CanUnpinFromQuickAccess(selection),
            vm.CanGitFor(single),
            vm.CanAddToGitignoreFor(single),
            vm.CanRunFileAi);
    }

    internal async Task<QuickAccessMenuState?> RefreshQuickAccessMenuStateAsync(
        FilesColumnViewModel vm,
        IReadOnlyList<FileEntryViewModel> selection)
    {
        try
        {
            if (!await vm.QuickAccess.RefreshAsync())
                return null;
            return new(vm.CanPinToQuickAccess(selection), vm.CanUnpinFromQuickAccess(selection));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void DeleteEntries(
        FilesColumnViewModel? vm,
        IReadOnlyList<FileEntryViewModel> entries,
        Func<string, bool> confirm)
    {
        if (entries.Count == 0 || vm is null)
            return;

        var message = FileContextMenuPolicy.FormatDeleteConfirmation(
            entries.Count,
            entries.Count == 1 && entries[0].IsDirectory,
            entries.Count == 1 ? entries[0].Name : null);
        if (!confirm(message))
            return;

        _ = FileEntryBatchExecutor.Execute(entries, vm.BeginFileOperationBatch, entry =>
        {
            vm.DeleteEntry(entry);
            return null;
        });
    }

    public void DuplicateEntries(FilesColumnViewModel? vm, IReadOnlyList<FileEntryViewModel> entries)
    {
        if (vm is null)
            return;
        _ = FileEntryBatchExecutor.Execute(entries, vm.BeginFileOperationBatch, vm.DuplicateEntry);
    }

    public void CopyFiles(IEnumerable<FileEntryViewModel> entries, bool move)
        => FileClipboard.SetFiles(entries.Select(entry => entry.FullPath), move);

    public IReadOnlyList<string> PrepareDragPaths(
        FileEntryViewModel origin, bool originIsSelected,
        Func<IReadOnlyList<FileEntryViewModel>> getSelection)
    {
        IEnumerable<FileEntryViewModel> sources = originIsSelected ? getSelection() : new[] { origin };
        return FileDragDrop.ExistingPaths(sources.Select(entry => entry.FullPath));
    }

    public void PasteFromClipboard(FilesColumnViewModel? vm)
    {
        if (vm is null || vm.TargetDirectory is not { } target || !FileClipboard.ContainsFiles())
            return;

        var move = FileClipboard.PrefersMove();
        var outcome = FilePasteBatchExecutor.Execute(
            vm.BeginFileOperationBatch,
            FileClipboard.GetFiles(),
            move,
            (source, shouldMove, resolver) => vm.PasteEntry(target, source, shouldMove, resolver),
            _resolveConflict);

        // 切り取りは全件がキャンセルされずに終わったときだけクリップボードを空にする。
        if (move && outcome.Completed && !outcome.Cancelled)
            FileClipboard.Clear();
    }

    public void PasteDroppedEntries(
        FilesColumnViewModel? vm,
        string targetDirectory,
        IEnumerable<string> sources,
        bool move)
    {
        if (vm is null)
            return;

        _ = FilePasteBatchExecutor.Execute(
            vm.BeginFileOperationBatch,
            sources,
            move,
            (source, shouldMove, resolver) => vm.PasteEntry(targetDirectory, source, shouldMove, resolver),
            _resolveConflict);
    }

    public async Task RunHistoryStepAsync(FilesColumnViewModel? vm, bool undo)
    {
        if (vm is null || (undo ? !vm.History.CanUndo : !vm.History.CanRedo))
            return;

        try
        {
            var result = undo
                ? vm.UndoFileOperation()
                : await vm.RedoFileOperationAsync();
            ToastService.Info($"{(undo ? "元に戻しました" : "やり直しました")}: {result.Description}");
        }
        catch (InvalidOperationException ex)
        {
            ToastService.Error(ex.Message);
        }
    }

    public void CopyPaths(IEnumerable<FileEntryViewModel> entries)
        => FileClipboard.CopyLines(entries.Select(entry => entry.FullPath));

    public void CopyRelativePaths(FilesColumnViewModel vm, IEnumerable<FileEntryViewModel> entries)
        => FileClipboard.CopyLines(entries.Select(vm.RelativePathFor));

    public void CopyNames(IEnumerable<FileEntryViewModel> entries)
        => FileClipboard.CopyLines(entries.Select(entry => entry.Name));

    public void AddToGitignore(FilesColumnViewModel? vm, FileEntryViewModel? entry)
    {
        if (entry is null || vm is null)
            return;
        try { vm.AddToGitignore(entry); }
        catch (InvalidOperationException ex) { ToastService.Error(ex.Message); }
    }

    public bool HandleKeyDown(
        FilesColumnViewModel vm,
        KeyEventArgs e,
        Func<IReadOnlyList<FileEntryViewModel>> getSelection,
        Func<FileEntryViewModel?> getSingleSelection,
        Func<FileEntryViewModel?> getSelectedItem,
        Action showProperties,
        Action openFilter,
        Action<int> moveSelection,
        Action<FileEntryViewModel?> renameEntry,
        Action<IReadOnlyList<FileEntryViewModel>> deleteEntries)
    {
        var resolution = FilesColumnKeyboardPolicy.Resolve(
            e.Key,
            e.SystemKey,
            e.KeyboardDevice.Modifiers,
            vm.IsFilterBarOpen,
            () => vm.GoBackCommand.CanExecute(null),
            () => vm.GoForwardCommand.CanExecute(null));

        switch (resolution.Action)
        {
            case FilesColumnKeyAction.GoBack:
                vm.GoBackCommand.Execute(null);
                break;
            case FilesColumnKeyAction.GoForward:
                vm.GoForwardCommand.Execute(null);
                break;
            case FilesColumnKeyAction.ShowProperties:
                showProperties();
                break;
            case FilesColumnKeyAction.Copy:
                CopyFiles(getSelection(), move: false);
                break;
            case FilesColumnKeyAction.Cut:
                CopyFiles(getSelection(), move: true);
                break;
            case FilesColumnKeyAction.Paste:
                PasteFromClipboard(vm);
                break;
            case FilesColumnKeyAction.Duplicate:
                DuplicateEntries(vm, getSelection());
                break;
            case FilesColumnKeyAction.Undo:
                _ = RunHistoryStepAsync(vm, undo: true);
                break;
            case FilesColumnKeyAction.Redo:
                _ = RunHistoryStepAsync(vm, undo: false);
                break;
            case FilesColumnKeyAction.OpenFilter:
                openFilter();
                break;
            case FilesColumnKeyAction.CloseFilter:
                vm.CloseFilter();
                break;
            case FilesColumnKeyAction.OpenSelected:
                vm.OpenEntry(getSelectedItem());
                break;
            case FilesColumnKeyAction.GoUp:
                if (vm.GoUpCommand.CanExecute(null))
                    vm.GoUpCommand.Execute(null);
                break;
            case FilesColumnKeyAction.Rename:
                renameEntry(getSingleSelection());
                break;
            case FilesColumnKeyAction.Delete:
                deleteEntries(getSelection());
                break;
            case FilesColumnKeyAction.Refresh:
                vm.RefreshCommand.Execute(null);
                break;
            case FilesColumnKeyAction.MoveNext:
                moveSelection(1);
                break;
            case FilesColumnKeyAction.MovePrevious:
                moveSelection(-1);
                break;
        }

        return resolution.MarkHandled;
    }
}
