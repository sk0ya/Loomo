namespace sk0ya.Loomo.App.Services;

/// <summary>ドック帯のアイコンを掴んだまま区画へ運ぶマウス操作を扱う。</summary>
internal sealed class DockPaneDragController(
    Window window,
    Action<DependencyObject, PaneKind> beginDrag)
{
    private Point _start;
    private UIElement? _source;
    private PaneKind _kind;
    private bool _armed;

    public void Hook(UIElement source, PaneKind kind)
        => source.PreviewMouseLeftButtonDown += (_, e) => Arm(source, kind, e.GetPosition(null));

    public void Arm(UIElement source, PaneKind kind, Point start)
    {
        Disarm();
        _source = source;
        _kind = kind;
        _start = start;
        _armed = true;
        window.PreviewMouseMove += OnMove;
        window.PreviewMouseLeftButtonUp += OnMouseUp;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e) => Disarm();

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_armed)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            Disarm();
            return;
        }

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var source = _source;
        var kind = _kind;
        Disarm();
        if (source is not null)
            beginDrag(source, kind);
    }

    private void Disarm()
    {
        if (!_armed)
            return;
        _armed = false;
        _source = null;
        window.PreviewMouseMove -= OnMove;
        window.PreviewMouseLeftButtonUp -= OnMouseUp;
    }
}
