using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class FolderTreeView
{
    // ===== 複数選択（Ctrl/Shift+クリック） =====
    // ネイティブ TreeView は単一選択しか持たない（IsSelected を複数項目で true にしても内部で
    // 一つに畳まれる）ため、選択集合はここ（View 層）で別に持つ。ネイティブの IsSelected／
    // SelectedItem は「キーボード移動の現在地・シングルクリックの通常選択」のまま触らず、
    // 見た目のハイライトだけを担う FileNodeViewModel.IsMultiSelected を追加で操作する。
    // 一括削除・コピー/切り取りはこの集合（無ければ単一選択1件）を対象にする。

    private readonly List<FileNodeViewModel> _multiSelected = new();
    private FileNodeViewModel? _rangeAnchor;

    private void ClearMultiSelection()
    {
        foreach (var n in _multiSelected) n.IsMultiSelected = false;
        _multiSelected.Clear();
    }

    private void AddToMultiSelection(FileNodeViewModel node)
    {
        if (_multiSelected.Contains(node)) return;
        _multiSelected.Add(node);
        node.IsMultiSelected = true;
    }

    private void ToggleMultiSelection(FileNodeViewModel node)
    {
        if (_multiSelected.Remove(node)) { node.IsMultiSelected = false; return; }
        AddToMultiSelection(node);
    }

    /// <summary>操作対象の集合。複数選択中ならその集合、そうでなければ <paramref name="fallback"/>
    /// （省略時はネイティブの現在選択）の1件（どちらも無ければ空）。Delete・コピー/切り取りが使う。</summary>
    private IReadOnlyList<FileNodeViewModel> CurrentSelection(FileNodeViewModel? fallback = null)
    {
        if (_multiSelected.Count > 0) return _multiSelected;
        var single = fallback ?? FileTree.SelectedItem as FileNodeViewModel;
        return single is null ? Array.Empty<FileNodeViewModel>() : new[] { single };
    }

    /// <summary>クリックされたノードに Ctrl/Shift 修飾を適用する。呼び出し元
    /// （<see cref="OnTreePreviewMouseLeftButtonDown"/>）はこの後もネイティブの単一選択処理へ
    /// そのまま渡す（e.Handled はしない）——ネイティブの「現在地」移動と、ここでの複数選択集合の
    /// 更新は独立に共存できるため。</summary>
    private void ApplySelectionModifiers(FileNodeViewModel node)
    {
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (shift)
        {
            var anchor = _rangeAnchor ?? (FileTree.SelectedItem as FileNodeViewModel) ?? node;
            if (DataContext is FolderTreeViewModel vm)
            {
                var visible = VisibleNodes(vm.Nodes).ToList();
                var ai = visible.IndexOf(anchor);
                var ni = visible.IndexOf(node);
                if (ai >= 0 && ni >= 0)
                {
                    ClearMultiSelection();
                    var (from, to) = ai <= ni ? (ai, ni) : (ni, ai);
                    for (var i = from; i <= to; i++) AddToMultiSelection(visible[i]);
                }
            }
            return;
        }

        if (ctrl)
        {
            // 何も複数選択が無い状態からの初回 Ctrl+クリック：今のネイティブ選択も集合に含めて
            // 1件→2件に広げる（Explorer 等と同じ体感）。
            if (_multiSelected.Count == 0 && FileTree.SelectedItem is FileNodeViewModel current && current != node)
                AddToMultiSelection(current);
            ToggleMultiSelection(node);
            _rangeAnchor = node;
            return;
        }

        // 修飾キー無しの通常クリック：複数選択があれば解除して単一選択へ戻す。
        if (_multiSelected.Count > 0) ClearMultiSelection();
        _rangeAnchor = node;
    }
}
