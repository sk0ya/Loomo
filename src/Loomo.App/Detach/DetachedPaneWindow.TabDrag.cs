using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.Detach;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// 切り離しウィンドウのタブ帯のドラッグ。2つの動きを1つのジェスチャーから出し分ける：
/// <b>帯の中を横へ</b>動かせば掴んだタブがカーソルに付いてきて、他のタブが避けて並べ替わる（VSCode 流の
/// 「タブが動いている」手応え。マウスキャプチャで自前に描く）。<b>帯の外へ</b>抜けたらその場で
/// <see cref="DragDrop.DoDragDrop"/> に切り替わり、他の窓のタブ帯へ結合／窓の外で離して新しい窓へ分離する
/// （調停は <see cref="DetachedWindowManager"/>）。
/// </summary>
public partial class DetachedPaneWindow
{
    /// <summary>帯の中の横ドラッグを並べ替えと見なす縦の許容量（これを超えて縦に動いたら切り離しドラッグ）。</summary>
    private const double TabReorderVerticalTolerance = 6.0;

    /// <summary>並べ替え中にタブ帯から抜けたと見なす余白（ぎりぎりで切り替わってちらつかないための遊び）。</summary>
    private const double TabStripLeaveMargin = 8.0;

    private Point _dragStart;
    private DetachedItem? _pressedItem;

    /// <summary>掴んだ位置のタブ内 X オフセット（カーソルとタブの相対位置を保って追従させる）。</summary>
    private double _grabOffsetX;

    private DetachedItem? _reorderItem;
    private Panel? _reorderPanel;
    private TranslateTransform? _reorderTransform;
    private FrameworkElement? _reorderContainer;

    private void OnTabPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _pressedItem = ResolveItem(e.OriginalSource);
        _grabOffsetX = _pressedItem is not null && ContainerFor(_pressedItem) is { } container
            ? e.GetPosition(container).X
            : 0;
    }

    private void OnTabPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_reorderItem is not null)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                EndReorder();
            else
                UpdateReorder(e);
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed || _pressedItem is null)
            return;

        var pos = e.GetPosition(this);
        var dx = Math.Abs(pos.X - _dragStart.X);
        var dy = Math.Abs(pos.Y - _dragStart.Y);
        if (dx < SystemParameters.MinimumHorizontalDragDistance
            && dy < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _pressedItem;
        _pressedItem = null;

        // 子要素（閉じるボタン等）がマウスをキャプチャしていると自前の追従も DoDragDrop も始まらないため解放する。
        if (Mouse.Captured is not null)
            Mouse.Capture(null);

        // 帯の中をほぼ横に動かしているあいだは並べ替え（＝タブが動く）。縦に抜けたら従来どおり切り離しドラッグ。
        if (_items.Count > 1 && dy <= TabReorderVerticalTolerance && TryBeginReorder(item, e))
            return;

        BeginCrossWindowDrag(item);
    }

    private void OnTabPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _pressedItem = null;
        if (_reorderItem is null)
            return;
        EndReorder();
        e.Handled = true;   // 並べ替えの終わりをタブのクリック（アクティブ切替）にしない
    }

    // ===== 帯の中の並べ替え（自前追従） =====

    private bool TryBeginReorder(DetachedItem item, MouseEventArgs e)
    {
        if (ContainerFor(item) is not { } container
            || VisualTreeHelper.GetParent(container) is not Panel panel)
            return false;

        SetActive(item);   // 掴んだ時点で前に出す（VSCode と同じ）
        _reorderItem = item;
        _reorderPanel = panel;
        _reorderTransform = new TranslateTransform();
        ApplyDragVisual(container);
        TabStripItems.LostMouseCapture += OnReorderLostCapture;
        Mouse.Capture(TabStripItems, CaptureMode.SubTree);
        UpdateReorder(e);
        return true;
    }

    private void UpdateReorder(MouseEventArgs e)
    {
        if (_reorderItem is not { } item || _reorderPanel is not { } panel || _reorderTransform is null)
            return;
        if (ContainerFor(item) is not { } container)
        {
            EndReorder();
            return;
        }

        // 帯から抜けたら、その場で「窓をまたぐドラッグ」へ引き継ぐ（掴み直さずに結合・分離できる）。
        if (IsOutsideTabStrip(e.GetPosition(TitleBar)))
        {
            EndReorder();
            BeginCrossWindowDrag(item);
            return;
        }

        var pointerX = e.GetPosition(panel).X;
        var slot = LayoutInformation.GetLayoutSlot(container);
        _reorderTransform.X = OffsetFor(pointerX, slot, panel.ActualWidth);

        var draggedIndex = _items.IndexOf(item);
        var left = slot.X + _reorderTransform.X;
        var newIndex = CalculateReorderIndex(HomeCenters(), draggedIndex, left, left + slot.Width);
        if (newIndex == draggedIndex || newIndex < 0)
            return;

        _items.Move(draggedIndex, newIndex);
        panel.UpdateLayout();
        // 並びが変わるとホーム位置も変わる。コンテナは作り直され得るので付け直したうえで追従量を計算し直す
        // （そのままだと入れ替わった幅の分だけタブが1フレーム跳ねる）。
        if (ContainerFor(item) is not { } moved)
        {
            EndReorder();   // 前の器に追従の見た目を残したまま止めない（隣のタブが重なって見える）
            return;
        }
        ApplyDragVisual(moved);
        _reorderTransform.X = OffsetFor(pointerX, LayoutInformation.GetLayoutSlot(moved), panel.ActualWidth);
    }

    private void EndReorder()
    {
        if (_reorderContainer is { } container)
            ClearDragVisual(container);
        if (_reorderItem is { } item && ContainerFor(item) is { } current)
            ClearDragVisual(current);
        TabStripItems.LostMouseCapture -= OnReorderLostCapture;
        if (ReferenceEquals(Mouse.Captured, TabStripItems))
            Mouse.Capture(null);

        var wasReordering = _reorderItem is not null;
        _reorderItem = null;
        _reorderPanel = null;
        _reorderTransform = null;
        _reorderContainer = null;
        if (wasReordering)
            _manager.NotifyChanged();   // タブの並びはスナップショットに乗る（Capture が _items 順で読む）
    }

    private void OnReorderLostCapture(object sender, MouseEventArgs e) => EndReorder();

    /// <summary>掴んでいるタブの見た目（追従・重なり・薄さ）を今のコンテナへ移す。
    /// <b>前のコンテナから必ず剥がす</b>——並べ替えで <c>ItemsControl</c> が器を入れ替えると、
    /// 同じ <see cref="TranslateTransform"/> を差したままの前の器に<b>隣のタブ</b>が入り、
    /// 2枚が同じ場所へ重なって隣が消えたように見える（実機のドラッグで踏んだ）。</summary>
    private void ApplyDragVisual(FrameworkElement container)
    {
        if (_reorderContainer is { } previous && !ReferenceEquals(previous, container))
            ClearDragVisual(previous);
        _reorderContainer = container;
        container.RenderTransform = _reorderTransform;
        container.Opacity = 0.85;
        Panel.SetZIndex(container, 1);   // 掴んだタブを隣の上に重ねる
    }

    private static void ClearDragVisual(FrameworkElement container)
    {
        container.RenderTransform = null;
        container.Opacity = 1;
        Panel.SetZIndex(container, 0);
    }

    /// <summary>カーソルに追従させる移動量。掴んだ相対位置を保ちつつ、帯からはみ出さないところで止める。</summary>
    private double OffsetFor(double pointerX, Rect slot, double panelWidth)
    {
        var left = Math.Clamp(pointerX - _grabOffsetX, 0, Math.Max(0, panelWidth - slot.Width));
        return left - slot.X;
    }

    private bool IsOutsideTabStrip(Point pointInTitleBar)
        => pointInTitleBar.Y < -TabStripLeaveMargin
           || pointInTitleBar.Y > TitleBar.ActualHeight + TabStripLeaveMargin
           || pointInTitleBar.X < -TabStripLeaveMargin
           || pointInTitleBar.X > TitleBar.ActualWidth + TabStripLeaveMargin;

    /// <summary>各タブのレイアウト上（＝追従の移動量を含まない）の中心 X。</summary>
    private IReadOnlyList<double> HomeCenters()
    {
        var centers = new List<double>(_items.Count);
        foreach (var item in _items)
        {
            if (ContainerFor(item) is not { } container)
            {
                centers.Add(double.NaN);
                continue;
            }
            var slot = LayoutInformation.GetLayoutSlot(container);
            centers.Add(slot.X + slot.Width / 2);
        }
        return centers;
    }

    /// <summary>
    /// 掴んだタブの落ち着き先の添字。判定は<b>進む側の端</b>と隣のタブの中心で行う——右へ運ぶなら
    /// 右端が隣の中心を越えたら入れ替え、左へ運ぶなら左端が手前の中心を越えたら入れ替え
    /// （＝隣に半分以上かぶったら場所を譲る）。
    ///
    /// <para>中心どうしで比べると<b>最後の位置へ届かない</b>：掴んだタブは帯の内側へクランプされるので、
    /// 掴んだタブが隣より幅広いと、右端まで運んでも中心が隣の中心を越えられず末尾へ行けない
    /// （実機のドラッグで踏んだ。幅の違う2枚では入れ替えが一度も起きなかった）。端で比べればこの取りこぼしが
    /// 無く、しかも入れ替えの境目は左右どちらへ運んでも同じ位置になる＝行き来しても暴れない。</para>
    /// </summary>
    internal static int CalculateReorderIndex(
        IReadOnlyList<double> homeCenters, int draggedIndex, double draggedLeft, double draggedRight)
    {
        if (draggedIndex < 0 || draggedIndex >= homeCenters.Count)
            return -1;
        var index = draggedIndex;
        for (var i = draggedIndex + 1; i < homeCenters.Count; i++)
            if (!double.IsNaN(homeCenters[i]) && homeCenters[i] < draggedRight)
                index = i;
        for (var i = draggedIndex - 1; i >= 0; i--)
            if (!double.IsNaN(homeCenters[i]) && homeCenters[i] > draggedLeft)
                index = i;
        return index;
    }

    private FrameworkElement? ContainerFor(DetachedItem item)
        => TabStripItems.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;

    // ===== 窓をまたぐドラッグ&ドロップ =====

    /// <summary>帯の外へ運ぶドラッグ。運んでいる物が手元から消えないよう、カーソルにタブ片を付けて
    /// （<see cref="TabDragGhost"/>）元のタブは薄く残す——OLE のドラッグは OS のカーソルしか出さないため。</summary>
    private void BeginCrossWindowDrag(DetachedItem item)
    {
        var source = ContainerFor(item);
        if (source is not null)
            source.Opacity = TabDragGhost.TornSourceOpacity;   // 抜けていく途中のタブ（戻ってくることもあるので消さない）

        // 1枚しか無い窓から引き出しても分かれない（もう単独の窓）——案内でそう約束しない。
        using var ghost = TabDragGhost.Show(this, item.Title, item.Icon, canSplit: _items.Count > 1);
        void OnGiveFeedback(object _, GiveFeedbackEventArgs e) => ghost.Follow(e.Effects);
        TabStripItems.GiveFeedback += OnGiveFeedback;

        _manager.BeginDrag(item, this);
        try
        {
            var data = new DataObject(DetachDragFormat, item.Id.ToString());
            var result = DragDrop.DoDragDrop(TabStripItems, data, DragDropEffects.Move);
            _manager.EndDrag(result);
        }
        finally
        {
            TabStripItems.GiveFeedback -= OnGiveFeedback;
            if (source is not null)
                source.Opacity = 1;            // 他の窓へ移ったなら、そこで作り直された器が素のまま出る
            _manager.ClearDrag();
        }
    }

    private void OnTabStripDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _manager.IsDragging && e.Data.GetDataPresent(DetachDragFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTabStripDrop(object sender, DragEventArgs e)
    {
        if (_manager.IsDragging && e.Data.GetDataPresent(DetachDragFormat))
        {
            e.Handled = true;
            _manager.DropOnto(this);
        }
    }
}
