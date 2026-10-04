namespace sk0ya.Loomo.App.Services;

/// <summary>下・右ドックの分割器入力を状態、トラック、保存へ同期する。</summary>
internal sealed class DockSplitterController(
    DockLayoutCoordinator state,
    Thumb bottomSplitter,
    Thumb rightSplitter,
    RowDefinition bottomRow,
    ColumnDefinition rightColumn,
    Action applyTracks,
    Action<bool> setDragging,
    Action saveSnapshot)
{
    public void Attach()
    {
        bottomSplitter.MouseDoubleClick += (_, e) =>
        {
            state.SetBottomHeight(DockLayoutCoordinator.DefaultBottomHeight);
            applyTracks();
            saveSnapshot();
            e.Handled = true;
        };
        bottomSplitter.DragStarted += (_, _) => setDragging(true);
        bottomSplitter.DragDelta += (_, e) =>
        {
            // 窓に収まらず頭打ちになっている間は、見えている高さから動かす（保存値から動かすと、
            // 縮めても見た目が変わらない区間ができる）。
            state.SetBottomHeight(Math.Min(state.BottomHeight, bottomRow.MaxHeight) - e.VerticalChange);
            bottomRow.Height = new GridLength(state.BottomHeight);
        };
        bottomSplitter.DragCompleted += (_, _) =>
        {
            setDragging(false);
            saveSnapshot();
        };

        rightSplitter.MouseDoubleClick += (_, e) =>
        {
            state.SetRightWidth(DockLayoutCoordinator.DefaultRightWidth);
            applyTracks();
            saveSnapshot();
            e.Handled = true;
        };
        rightSplitter.DragStarted += (_, _) => setDragging(true);
        rightSplitter.DragDelta += (_, e) =>
        {
            state.SetRightWidth(Math.Min(state.RightWidth, rightColumn.MaxWidth) - e.HorizontalChange);   // 下と同じ理由
            rightColumn.Width = new GridLength(state.RightWidth);
        };
        rightSplitter.DragCompleted += (_, _) =>
        {
            setDragging(false);
            saveSnapshot();
        };
    }
}
