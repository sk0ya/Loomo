using System.Windows.Controls;
using System.Windows.Input;

namespace sk0ya.Loomo.App.Services;

/// <summary>左右差分本文・行番号ガターのスクロールを同期する。</summary>
internal sealed class DiffScrollSyncController
{
    private readonly Func<ScrollViewer?> _leftGutter;
    private readonly Func<ScrollViewer?> _leftText;
    private readonly Func<ScrollViewer?> _rightGutter;
    private readonly Func<ScrollViewer?> _rightText;
    private readonly Func<ScrollViewer?> _unified;
    private readonly Func<object> _leftTextSource;
    private readonly Func<object> _rightTextSource;
    private readonly Action _positionGutter;
    private bool _syncing;

    internal DiffScrollSyncController(
        Func<object> leftTextSource,
        Func<object> rightTextSource,
        Func<ScrollViewer?> leftGutter,
        Func<ScrollViewer?> leftText,
        Func<ScrollViewer?> rightGutter,
        Func<ScrollViewer?> rightText,
        Func<ScrollViewer?> unified,
        Action positionGutter)
    {
        _leftTextSource = leftTextSource;
        _rightTextSource = rightTextSource;
        _leftGutter = leftGutter;
        _leftText = leftText;
        _rightGutter = rightGutter;
        _rightText = rightText;
        _unified = unified;
        _positionGutter = positionGutter;
    }

    internal void OnSideScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_syncing && (e.VerticalChange != 0 || e.HorizontalChange != 0))
        {
            _syncing = true;
            try
            {
                var source = (ScrollViewer)sender;
                if (e.VerticalChange != 0)
                {
                    var offset = source.VerticalOffset;
                    SetVerticalOffset(_leftText(), offset);
                    SetVerticalOffset(_rightText(), offset);
                    SetVerticalOffset(_leftGutter(), offset);
                    SetVerticalOffset(_rightGutter(), offset);
                }
                if (e.HorizontalChange != 0)
                {
                    var offset = DiffRowLineMapper.HorizontalOffsetForSync(
                        source.HorizontalOffset, source.ScrollableWidth,
                        _leftText()?.ScrollableWidth, _rightText()?.ScrollableWidth);
                    SetHorizontalOffset(_leftText(), offset);
                    SetHorizontalOffset(_rightText(), offset);
                }
            }
            finally
            {
                _syncing = false;
            }
        }
        _positionGutter();
    }

    internal void ScrollHorizontally(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            return;
        var scrollViewer = sender switch
        {
            var leftSource when ReferenceEquals(leftSource, _leftTextSource()) => _leftText(),
            var rightSource when ReferenceEquals(rightSource, _rightTextSource()) => _rightText(),
            _ => _unified(),
        };
        if (scrollViewer is null)
            return;
        scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private static void SetVerticalOffset(ScrollViewer? scrollViewer, double offset)
    {
        if (scrollViewer is not null && Math.Abs(scrollViewer.VerticalOffset - offset) > 0.5)
            scrollViewer.ScrollToVerticalOffset(offset);
    }

    private static void SetHorizontalOffset(ScrollViewer? scrollViewer, double offset)
    {
        if (scrollViewer is not null && Math.Abs(scrollViewer.HorizontalOffset - offset) > 0.5)
            scrollViewer.ScrollToHorizontalOffset(offset);
    }
}
