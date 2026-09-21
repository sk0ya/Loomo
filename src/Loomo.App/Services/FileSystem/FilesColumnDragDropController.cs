using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>ファイルカラム間と外部とのドラッグ＆ドロップを調整する。</summary>
internal sealed class FilesColumnDragDropController
{
    private static bool _internalDrag;

    private readonly ListBox _entryList;
    private readonly Func<FilesColumnViewModel?> _getViewModel;
    private readonly Func<IReadOnlyList<FileEntryViewModel>> _getSelection;
    private readonly FilesColumnCommandController _commands;
    private Point _dragStart;
    private FileEntryViewModel? _dragCandidate;

    internal FilesColumnDragDropController(
        ListBox entryList,
        Func<FilesColumnViewModel?> getViewModel,
        Func<IReadOnlyList<FileEntryViewModel>> getSelection,
        FilesColumnCommandController commands)
    {
        _entryList = entryList;
        _getViewModel = getViewModel;
        _getSelection = getSelection;
        _commands = commands;
    }

    internal void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = EntryAt(e.OriginalSource);
    }

    internal void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not { } origin)
            return;

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragCandidate = null;
        var paths = _commands.PrepareDragPaths(
            origin, _entryList.SelectedItems.Contains(origin), _getSelection);
        if (paths.Count == 0)
            return;

        var data = new DataObject();
        FileDragDrop.SetPaths(data, paths);
        _internalDrag = true;
        try { DragDrop.DoDragDrop(_entryList, data, DragDropEffects.Copy | DragDropEffects.Move); }
        catch { /* ドラッグ中の例外は無視 */ }
        finally { _internalDrag = false; }
    }

    internal void OnDragOver(DragEventArgs e)
    {
        e.Effects = ResolveDropEffect(e, out _);
        e.Handled = true;
    }

    internal void OnDrop(DragEventArgs e)
    {
        var effect = ResolveDropEffect(e, out var targetDirectory);
        e.Handled = true;
        var sources = FileDragDrop.TryGetPaths(e.Data);
        if (effect == DragDropEffects.None || targetDirectory is null || _getViewModel() is not { } vm
            || sources.Count == 0)
            return;

        var move = (effect & DragDropEffects.Move) != 0;
        _commands.PasteDroppedEntries(vm, targetDirectory, sources, move);
    }

    internal static FileEntryViewModel? EntryAt(object? source)
        => WpfTreeTraversal.FindAncestor<ListBoxItem>(source as DependencyObject)?.DataContext as FileEntryViewModel;

    private DragDropEffects ResolveDropEffect(DragEventArgs e, out string? targetDirectory)
    {
        targetDirectory = null;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || _getViewModel() is not { } vm)
            return DragDropEffects.None;

        targetDirectory = vm.DropTargetFor(EntryAt(e.OriginalSource));
        if (targetDirectory is null)
            return DragDropEffects.None;

        var sources = FileDragDrop.TryGetPaths(e.Data);
        var effect = FileColumnDropEffectPolicy.Resolve(
            sources,
            targetDirectory,
            _internalDrag,
            controlPressed: (e.KeyStates & DragDropKeyStates.ControlKey) != 0,
            shiftPressed: (e.KeyStates & DragDropKeyStates.ShiftKey) != 0,
            isDirectory: Directory.Exists);
        return effect switch
        {
            FileColumnDropEffect.Copy => DragDropEffects.Copy,
            FileColumnDropEffect.Move => DragDropEffects.Move,
            _ => DragDropEffects.None,
        };
    }
}
