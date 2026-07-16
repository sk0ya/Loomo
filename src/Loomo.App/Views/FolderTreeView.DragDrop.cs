using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class FolderTreeView
{
    // ===== ドラッグ＆ドロップ =====
    // ・ツリー内のドラッグ … フォルダへドロップで移動（既定）。
    // ・エクスプローラー等からのドロップ … ワークスペース配下へコピー（既定）。
    // ・ツリーからエクスプローラーへのドラッグ … Windows のファイルドロップリストで受け渡し。
    // 修飾キー: Ctrl=コピー強制 / Shift=移動強制。
    // 実体のコピー／移動は ViewModel.PasteEntry（ルート限定・一意化・タブ追従）に委譲する。

    private Point _dragStart;
    private FileNodeViewModel? _dragCandidate;
    // Loomo 自身が発生源のドラッグ中フラグ（DoDragDrop の modal ループ中だけ true）。
    // ツリー内ドラッグは移動・外部（Explorer）からのドロップはコピーを既定にするための判定。
    private bool _internalDrag;

    private void OnTreePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = e.OriginalSource is DependencyObject src
            && FindAncestorTreeViewItem(src)?.DataContext is FileNodeViewModel node
            ? node
            : null;
    }

    private void OnTreePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null)
            return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var node = _dragCandidate;
        _dragCandidate = null;

        if (!File.Exists(node.FullPath) && !Directory.Exists(node.FullPath))
            return;

        var data = new DataObject();
        data.SetFileDropList(new StringCollection { node.FullPath });

        _internalDrag = true;
        try
        {
            DragDrop.DoDragDrop(FileTree, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch { /* ドラッグ中の例外は無視 */ }
        finally
        {
            _internalDrag = false;
        }
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ResolveDropEffect(e, out _);
        e.Handled = true;
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        var effect = ResolveDropEffect(e, out var targetDir);
        e.Handled = true;

        if (effect == DragDropEffects.None || targetDir is null
            || DataContext is not FolderTreeViewModel vm
            || e.Data.GetData(DataFormats.FileDrop) is not string[] sources)
            return;

        var move = (effect & DragDropEffects.Move) != 0;
        string? lastPasted = null;

        try
        {
            foreach (var source in sources)
                if (!string.IsNullOrEmpty(source))
                    lastPasted = vm.PasteEntry(targetDir, source, move);
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
            return;
        }

        if (lastPasted is not null)
        {
            var reveal = lastPasted;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => RevealPath(reveal)));
        }
    }

    // ドロップ先ディレクトリと効果（コピー/移動/不可）を決める。
    private DragDropEffects ResolveDropEffect(DragEventArgs e, out string? targetDir)
    {
        targetDir = null;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || DataContext is not FolderTreeViewModel vm)
            return DragDropEffects.None;

        var node = e.OriginalSource is DependencyObject src
            ? FindAncestorTreeViewItem(src)?.DataContext as FileNodeViewModel
            : null;
        targetDir = vm.GetTargetDirectory(node);
        if (targetDir is null)
            return DragDropEffects.None;

        var sources = e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        // フォルダを自身／配下へは不可（無限再帰）。ドラッグ中にカーソルで示す。
        foreach (var s in sources)
            if (Directory.Exists(s) && (PathEquals(s, targetDir) || IsAncestor(s, targetDir)))
                return DragDropEffects.None;

        // 修飾キー優先。無ければ内部ドラッグは移動、外部からはコピーを既定にする。
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0)
            return DragDropEffects.Copy;
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0)
            return DragDropEffects.Move;
        return _internalDrag ? DragDropEffects.Move : DragDropEffects.Copy;
    }
}
