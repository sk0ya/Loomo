using System.Linq;
using System.Windows.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class FolderTreeView
{
    // ===== 複数選択（Ctrl/Shift+クリック） =====
    // ネイティブ TreeView は単一選択しか持たない（IsSelected を複数項目で true にしても内部で
    // 一つに畳まれる）ため、選択集合は専用 controller が別に持つ。ネイティブの IsSelected／
    // SelectedItem は「キーボード移動の現在地・シングルクリックの通常選択」のまま触らず、
    // 見た目のハイライトだけを担う FileNodeViewModel.IsMultiSelected を追加で操作する。
    // 一括削除・コピー/切り取りはこの集合（無ければ単一選択1件）を対象にする。

    private readonly FolderTreeMultiSelectionController _multiSelection = new();

    private void ClearMultiSelection() => _multiSelection.Clear();

    private void AddToMultiSelection(FileNodeViewModel node) => _multiSelection.Add(node);

    private void ToggleMultiSelection(FileNodeViewModel node) => _multiSelection.Toggle(node);

    /// <summary>操作対象の集合。複数選択中ならその集合、そうでなければ <paramref name="fallback"/>
    /// （省略時はネイティブの現在選択）の1件（どちらも無ければ空）。Delete・コピー/切り取りが使う。</summary>
    private IReadOnlyList<FileNodeViewModel> CurrentSelection(FileNodeViewModel? fallback = null)
    {
        return _multiSelection.Resolve(fallback, FileTree.SelectedItem as FileNodeViewModel);
    }

    /// <summary>クリックされたノードに現在の WPF 修飾キーと表示順を渡し、選択状態を更新する。呼び出し元
    /// （<see cref="OnTreePreviewMouseLeftButtonDown"/>）はこの後もネイティブの単一選択処理へ
    /// そのまま渡す（e.Handled はしない）——ネイティブの「現在地」移動と、ここでの複数選択集合の
    /// 更新は独立に共存できるため。</summary>
    private void ApplySelectionModifiers(FileNodeViewModel node)
    {
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        IReadOnlyList<FileNodeViewModel> visible = shift && DataContext is FolderTreeViewModel vm
            ? VisibleNodes(vm.Nodes).ToList()
            : Array.Empty<FileNodeViewModel>();
        _multiSelection.ApplyModifiers(
            node,
            shift: shift,
            control: (Keyboard.Modifiers & ModifierKeys.Control) != 0,
            currentSelection: FileTree.SelectedItem as FileNodeViewModel,
            visibleNodes: visible);
    }
}
