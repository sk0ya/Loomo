namespace sk0ya.Loomo.App.Views;

/// <summary>軌跡バーのスクロール、日時選択、ホイール移動を管理する。</summary>
internal sealed class TrailBarController
{
    private const double DotWidth = 20;
    private static double HourLabelWidth => UiFontManager.Scaled(38);
    private readonly TrailViewModel _trail;
    private readonly ScrollViewer _scroll;
    private readonly ItemsControl _dots;
    private readonly Popup _popup;
    private readonly Calendar _calendar;
    private readonly FrameworkElement _popupRoot;
    private readonly Action<TrailEntryViewModel> _jump;
    private DispatcherTimer? _scrubTimer;
    private TrailEntryViewModel? _scrubTarget;
    private DateTime _popupClosedAt;
    private bool _popupSelectionMade;

    public TrailBarController(TrailViewModel trail, ScrollViewer scroll, ItemsControl dots,
        Popup popup, Calendar calendar, FrameworkElement popupRoot, Action<TrailEntryViewModel> jump)
    {
        _trail = trail;
        _scroll = scroll;
        _dots = dots;
        _popup = popup;
        _calendar = calendar;
        _popupRoot = popupRoot;
        _jump = jump;
    }

    public bool BrowsingPast { get; set; }

    public void OnWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            Scrub(e.Delta);
            return;
        }
        var target = _scroll.HorizontalOffset + (e.Delta > 0 ? -1 : 1) * (DotWidth * 3);
        _scroll.ScrollToHorizontalOffset(target);
        BrowsingPast = target < _scroll.ScrollableWidth - 1;
    }

    public void ScrollToCurrent()
    {
        var index = _trail.CurrentIndex;
        if (index < 0)
        {
            _scroll.ScrollToRightEnd();
            return;
        }
        _scroll.ScrollToHorizontalOffset(SlotOffset(index));
    }

    public void UpdateTrailingMargin()
    {
        var trailing = Math.Max(0, _scroll.ViewportWidth - DotWidth);
        if (Math.Abs(_dots.Padding.Right - trailing) < 0.5)
            return;
        _dots.Padding = new Thickness(0, 0, trailing, 0);
        if (!BrowsingPast)
            _scroll.Dispatcher.BeginInvoke(new Action(ScrollToCurrent), DispatcherPriority.Loaded);
    }

    public void BackToLatest()
    {
        _trail.BackToTodayCommand.Execute(null);
        _trail.MoveToLatest();
        BrowsingPast = false;
        _scroll.Dispatcher.BeginInvoke(new Action(ScrollToCurrent), DispatcherPriority.Loaded);
    }

    public void BackToLatestFromPopup()
    {
        BackToLatest();
        _popupSelectionMade = true;
        _popup.IsOpen = false;
    }

    public void ToggleDateTimePopup()
    {
        if (_popup.IsOpen || (DateTime.UtcNow - _popupClosedAt).TotalMilliseconds < 250)
        {
            _popup.IsOpen = false;
            return;
        }
        _popupSelectionMade = false;
        _calendar.SelectedDate = _trail.DisplayDate.ToDateTime(TimeOnly.MinValue);
        _calendar.DisplayDate = _calendar.SelectedDate.Value;
        _calendar.DisplayDateEnd = DateTime.Today;
        _popup.IsOpen = true;
        _scroll.Dispatcher.BeginInvoke(new Action(() => _popupRoot.Focus()), DispatcherPriority.Input);
    }

    public void SelectCalendarDate()
    {
        if (!_popup.IsOpen || _calendar.SelectedDate is not { } picked)
            return;
        Mouse.Capture(null);
        _trail.ShowDate(DateOnly.FromDateTime(picked));
        BrowsingPast = false;
        _popupSelectionMade = true;
        _scroll.Dispatcher.BeginInvoke(new Action(ScrollToCurrent), DispatcherPriority.Loaded);
    }

    public void SelectHour(TrailHourViewModel hour)
    {
        Mouse.Capture(null);
        _trail.SelectHour(hour);
        BrowsingPast = _trail.CurrentIndex < _trail.Entries.Count - 1;
        _popupSelectionMade = true;
        _scroll.Dispatcher.BeginInvoke(new Action(ScrollToCurrent), DispatcherPriority.Loaded);
    }

    public void PopupClosed()
    {
        _popupClosedAt = DateTime.UtcNow;
        if (!_popupSelectionMade)
            return;
        _popupSelectionMade = false;
        if (_trail.CurrentEntry is { } entry)
            _jump(entry);
    }

    public void ClosePopupIfFocusLeaves(KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject next && IsWithin(next, _popupRoot))
            return;
        _popup.IsOpen = false;
    }

    private void Scrub(int delta)
    {
        var entry = _trail.MoveCurrent(delta > 0 ? -1 : 1);
        BrowsingPast = _trail.CurrentIndex < _trail.Entries.Count - 1;
        ScrollToCurrent();
        if (entry is null)
            return;
        _scrubTarget = entry;
        _scrubTimer ??= CreateScrubTimer();
        _scrubTimer.Stop();
        _scrubTimer.Start();
    }

    private DispatcherTimer CreateScrubTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_scrubTarget is not { } target)
                return;
            _scrubTarget = null;
            _jump(target);
        };
        return timer;
    }

    private double SlotOffset(int index)
    {
        double x = 0;
        for (var i = 0; i < index && i < _trail.Entries.Count; i++)
            x += DotWidth + (_trail.Entries[i].StartsNewHour ? HourLabelWidth : 0);
        return x;
    }

    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null;
             current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor))
                return true;
        return false;
    }
}
