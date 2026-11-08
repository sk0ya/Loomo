using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using sk0ya.Loomo.App.Services.Infrastructure;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 一覧（ListBox・TreeView）の行を、ファイルとしてほかのペインへドラッグで運ぶ源（§24.102）。
/// Git パネルの変更・Git ペインのコミットのファイル・差分ペインのファイル一覧に付ける。運ぶのはエクスプローラー
/// （<see cref="FolderTreeDragDropController"/>）と同じ <see cref="DataFormats.FileDrop"/> なので、ターミナルへのパスの差し込み・
/// エディタで開く・ペグボードへ置く、の受け口は既存のものがそのまま効く。
/// <para>行の DataContext からパスを引く関数を貰う（フォルダーの行なら配下のファイルを全部返してよい）。複数選択の ListBox では、
/// 掴んだ行が選択の一員なら選択全体を運ぶ。チェックボックス・ボタンの上で押したときは始めない（押す操作の邪魔をしない）。
/// どれもイベントを「処理済み」にしないので、行の選択・ダブルクリック・右クリックはこれまでどおり動く。</para>
/// </summary>
public sealed class ItemsFileDragSource
{
    private readonly ItemsControl _list;
    private readonly Func<object, IEnumerable<string>> _pathsOf;
    private Point _dragStart;
    private object? _candidate;

    private ItemsFileDragSource(ItemsControl list, Func<object, IEnumerable<string>> pathsOf)
    {
        _list = list;
        _pathsOf = pathsOf;
        list.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        list.PreviewMouseMove += OnPreviewMouseMove;
        list.PreviewMouseLeftButtonUp += (_, _) => _candidate = null;
    }

    /// <summary><paramref name="list"/> の行をドラッグの源にする。<paramref name="pathsOf"/> は行の DataContext → 運ぶファイルの絶対パス
    /// （無い・消えたファイルは運ばない）。</summary>
    public static ItemsFileDragSource Attach(ItemsControl list, Func<object, IEnumerable<string>> pathsOf)
        => new(list, pathsOf);

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        var source = e.OriginalSource as DependencyObject;
        _candidate = WpfTreeTraversal.FindAncestor<ButtonBase>(source) is null
            ? RowOf(source)?.DataContext
            : null;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _candidate is not { } item)
            return;
        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _candidate = null;

        var paths = FileDragDrop.ExistingPaths(ItemsToCarry(item).SelectMany(SafePaths).Distinct(StringComparer.OrdinalIgnoreCase));
        if (paths.Count == 0)
            return;
        var data = new DataObject();
        FileDragDrop.SetPaths(data, paths);
        try { DragDrop.DoDragDrop(_list, data, DragDropEffects.Copy | DragDropEffects.Link); }
        catch { /* ドラッグ中の例外は無視（受け手の失敗で一覧を壊さない） */ }
    }

    /// <summary>掴んだ行と、それが複数選択の一員なら選択全体。</summary>
    private IEnumerable<object> ItemsToCarry(object grabbed)
    {
        if (_list is ListBox { SelectionMode: not SelectionMode.Single } box && box.SelectedItems.Contains(grabbed))
            return box.SelectedItems.Cast<object>().ToList();
        return [grabbed];
    }

    private IEnumerable<string> SafePaths(object item)
    {
        try { return _pathsOf(item).Where(path => !string.IsNullOrWhiteSpace(path)).ToList(); }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or NotSupportedException) { return []; }
    }

    private static FrameworkElement? RowOf(DependencyObject? source)
        => (FrameworkElement?)WpfTreeTraversal.FindAncestor<TreeViewItem>(source)
           ?? WpfTreeTraversal.FindAncestor<ListBoxItem>(source);
}
