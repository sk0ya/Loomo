using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>FolderTree の内部ドラッグ、ドロップ効果、貼付後の表示を一体で管理する。</summary>
internal sealed class FolderTreeDragDropController
{
    private readonly TreeView _tree;
    private readonly Func<FolderTreeViewModel?> _getViewModel;
    private readonly Func<FileNodeViewModel, IReadOnlyList<FileNodeViewModel>> _getDragSelection;
    private readonly Action<FileNodeViewModel> _applySelectionModifiers;
    private readonly Action<string> _revealPath;
    private Point _dragStart;
    private FileNodeViewModel? _dragCandidate;
    private bool _internalDrag;

    internal FolderTreeDragDropController(
        TreeView tree,
        Func<FolderTreeViewModel?> getViewModel,
        Func<FileNodeViewModel, IReadOnlyList<FileNodeViewModel>> getDragSelection,
        Action<FileNodeViewModel> applySelectionModifiers,
        Action<string> revealPath)
    {
        _tree = tree;
        _getViewModel = getViewModel;
        _getDragSelection = getDragSelection;
        _applySelectionModifiers = applySelectionModifiers;
        _revealPath = revealPath;
    }

    internal void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = NodeAt(e.OriginalSource);

        // Ctrl/Shift 修飾時は複数選択だけ更新し、TreeView の通常選択は通す。
        if (_dragCandidate is { } node)
            _applySelectionModifiers(node);
    }

    internal void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not { } node)
            return;

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragCandidate = null;
        // 選択集合の一員を掴んだときだけ集合全体を運ぶ。
        var selected = _getDragSelection(node);
        var sources = FileDragDrop.ExistingPaths(
            selected.Where(item => !item.IsShellItem).Select(item => item.FullPath));
        if (sources.Count == 0)
            return;

        var data = new DataObject();
        FileDragDrop.SetPaths(data, sources);
        _internalDrag = true;
        try { DragDrop.DoDragDrop(_tree, data, DragDropEffects.Copy | DragDropEffects.Move); }
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
        if (effect == DragDropEffects.None || targetDirectory is null
            || _getViewModel() is not { } vm || sources.Count == 0)
            return;

        var move = (effect & DragDropEffects.Move) != 0;
        var outcome = FilePasteBatchExecutor.Execute(
            vm.BeginFileOperationBatch,
            sources,
            move,
            (source, shouldMove, resolver) => vm.PasteEntry(targetDirectory, source, shouldMove, resolver),
            context => FileConflictDialog.Show(Window.GetWindow(_tree), context));

        if (outcome.Completed && outcome.LastDestinationPath is { } lastPasted)
            _tree.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => _revealPath(lastPasted)));
    }

    private DragDropEffects ResolveDropEffect(DragEventArgs e, out string? targetDirectory)
    {
        targetDirectory = null;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || _getViewModel() is not { } vm)
            return DragDropEffects.None;

        targetDirectory = vm.GetTargetDirectory(NodeAt(e.OriginalSource));
        if (targetDirectory is null)
            return DragDropEffects.None;

        var sources = FileDragDrop.TryGetPaths(e.Data);
        var effect = FileColumnDropEffectPolicy.Resolve(
            sources,
            targetDirectory,
            _internalDrag,
            controlPressed: (e.KeyStates & DragDropKeyStates.ControlKey) != 0,
            shiftPressed: (e.KeyStates & DragDropKeyStates.ShiftKey) != 0,
            isDirectory: Directory.Exists,
            preventSameDirectoryMove: false);
        return effect switch
        {
            FileColumnDropEffect.Copy => DragDropEffects.Copy,
            FileColumnDropEffect.Move => DragDropEffects.Move,
            _ => DragDropEffects.None,
        };
    }

    private static FileNodeViewModel? NodeAt(object? source)
        => WpfTreeTraversal.FindAncestor<TreeViewItem>(source as DependencyObject)?.DataContext as FileNodeViewModel;
}
