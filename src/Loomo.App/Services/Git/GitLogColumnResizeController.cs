using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>Git コミット一覧の GridView 列幅、境界ドラッグ、幅計測を管理する。</summary>
internal sealed class GitLogColumnResizeController : IDisposable
{
    private const double UserSizeThresholdPx = 3;
    private const int TailMeasureRows = 400;

    private static readonly (string Header, Func<GitLogRow, string?> Value)[] TailColumns =
    [
        ("日時", row => row.Date),
        ("作成者", row => row.Author),
        ("ID", row => row.ShortHash),
    ];

    private readonly FrameworkElement _owner;
    private readonly ListView _logList;
    private readonly Canvas _overlay;
    private readonly List<Thumb> _thumbs = new();
    private readonly HashSet<GridViewColumn> _userSizedColumns = new();
    private readonly List<(DependencyPropertyDescriptor Descriptor, object Target, EventHandler Handler)> _valueHandlers = new();
    private GridView? _gridView;
    private ScrollViewer? _scrollViewer;
    private GridViewHeaderRowPresenter? _headerPresenter;
    private Border? _headerFillerMask;
    private TextBlock? _fontProbe;
    private INotifyCollectionChanged? _measureSource;
    private Window? _attachedWindow;
    private bool _ready;
    private bool _fillColumnUpdating;
    private bool _tailWidthsQueued;
    private bool _disposed;
    private double _dragTotal;

    internal GitLogColumnResizeController(FrameworkElement owner, ListView logList, Canvas overlay)
    {
        _owner = owner;
        _logList = logList;
        _overlay = overlay;
    }

    internal void Setup()
    {
        if (_disposed) return;
        _owner.Loaded += OnOwnerLoaded;
        if (_owner.IsLoaded)
            OnOwnerLoaded(_owner, new RoutedEventArgs());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.Loaded -= OnOwnerLoaded;
        if (_attachedWindow is not null)
            _attachedWindow.Closed -= OnWindowClosed;
        _attachedWindow = null;
        if (_scrollViewer is not null)
            _scrollViewer.ScrollChanged -= OnLogScrollChanged;
        _logList.SizeChanged -= OnLogListSizeChanged;
        if (_measureSource is not null)
            _measureSource.CollectionChanged -= OnLogMeasureSourceChanged;
        foreach (var (descriptor, target, handler) in _valueHandlers)
            descriptor.RemoveValueChanged(target, handler);
        _valueHandlers.Clear();
        foreach (var thumb in _thumbs)
        {
            thumb.DragStarted -= OnLogColumnDragStarted;
            thumb.DragDelta -= OnLogColumnThumbDragDelta;
            _overlay.Children.Remove(thumb);
        }
        _thumbs.Clear();
        if (_fontProbe is not null)
            _overlay.Children.Remove(_fontProbe);
        if (_headerFillerMask is not null)
            _overlay.Children.Remove(_headerFillerMask);
        _measureSource = null;
        _scrollViewer = null;
        _gridView = null;
        _headerPresenter = null;
        _fontProbe = null;
        _headerFillerMask = null;
    }

    private void OnOwnerLoaded(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(_owner);
        if (!ReferenceEquals(_attachedWindow, window))
        {
            if (_attachedWindow is not null)
                _attachedWindow.Closed -= OnWindowClosed;
            _attachedWindow = window;
            if (_attachedWindow is not null)
                _attachedWindow.Closed += OnWindowClosed;
        }
        Initialize();
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Dispose();

    private void Initialize()
    {
        if (_disposed || _ready || _logList.View is not GridView gridView) return;
        _scrollViewer = FindScrollViewer(_logList);
        if (_scrollViewer is null) return;
        _headerPresenter = FindVisualChild<GridViewHeaderRowPresenter>(_logList);
        _ready = true;
        _gridView = gridView;

        _headerFillerMask = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            IsHitTestVisible = false,
        };
        _headerFillerMask.SetResourceReference(Border.BackgroundProperty, "BgAlt");
        _headerFillerMask.SetResourceReference(Border.BorderBrushProperty, "Border");
        _overlay.Children.Add(_headerFillerMask);

        _fontProbe = new TextBlock { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _fontProbe.SetResourceReference(TextBlock.FontSizeProperty, "Fs11");
        _overlay.Children.Add(_fontProbe);
        Watch(DependencyPropertyDescriptor.FromProperty(TextBlock.FontSizeProperty, typeof(TextBlock)),
            _fontProbe, OnLogFontSizeChanged);

        var thumbStyle = (Style)_owner.FindResource("LogColumnResizeThumb");
        for (var index = 0; index < gridView.Columns.Count; index++)
        {
            var column = gridView.Columns[index];
            var thumb = new Thumb { Width = 6, Style = thumbStyle };
            if (index == gridView.Columns.Count - 1) thumb.Visibility = Visibility.Collapsed;
            thumb.DragStarted += OnLogColumnDragStarted;
            thumb.DragDelta += OnLogColumnThumbDragDelta;
            _thumbs.Add(thumb);
            _overlay.Children.Add(thumb);
            Watch(DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn)),
                column, OnLogColumnWidthChanged);
        }

        _scrollViewer.ScrollChanged += OnLogScrollChanged;
        _logList.SizeChanged += OnLogListSizeChanged;
        Watch(DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ListView)),
            _logList, OnLogItemsSourceChanged);
        HookMeasureSource();
        HideHeaderGrippers();
        UpdateThumbPositions();
    }

    private void Watch(DependencyPropertyDescriptor descriptor, object target, EventHandler handler)
    {
        descriptor.AddValueChanged(target, handler);
        _valueHandlers.Add((descriptor, target, handler));
    }

    private void HideHeaderGrippers(bool retry = true)
    {
        if (_disposed || _headerPresenter is null) return;
        var found = false;
        foreach (var header in FindVisualChildren<GridViewColumnHeader>(_headerPresenter))
        {
            if (header.Template?.FindName("PART_HeaderGripper", header) is not Thumb gripper) continue;
            gripper.Visibility = Visibility.Collapsed;
            found = true;
        }
        if (found || !retry) return;
        _owner.Dispatcher.BeginInvoke(new Action(() => HideHeaderGrippers(false)), DispatcherPriority.Loaded);
    }

    private void OnLogColumnDragStarted(object sender, DragStartedEventArgs e) => _dragTotal = 0;

    private void OnLogColumnWidthChanged(object? sender, EventArgs e) => UpdateThumbPositions();
    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateThumbPositions();
    private void OnLogListSizeChanged(object sender, SizeChangedEventArgs e) => UpdateThumbPositions();
    private void OnLogFontSizeChanged(object? sender, EventArgs e) => QueueTailColumnWidths();
    private void OnLogItemsSourceChanged(object? sender, EventArgs e) => HookMeasureSource();

    private void OnLogColumnThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_gridView is null) return;
        var index = _thumbs.IndexOf((Thumb)sender);
        if (index < 0 || index + 1 >= _gridView.Columns.Count) return;
        var column = _gridView.Columns[index + 1];
        _dragTotal += e.HorizontalChange;
        if (Math.Abs(_dragTotal) >= UserSizeThresholdPx) _userSizedColumns.Add(column);
        column.Width = Math.Max(30, column.ActualWidth - e.HorizontalChange);
    }

    private static Func<GitLogRow, string?>? TailColumnValue(object? header)
    {
        if (header is not string title) return null;
        foreach (var (name, value) in TailColumns)
            if (name == title) return value;
        return null;
    }

    private IEnumerable MeasureRows
        => _logList.ItemsSource is ICollectionView view ? view.SourceCollection : _logList.Items;

    private void HookMeasureSource()
    {
        if (_disposed) return;
        var source = MeasureRows as INotifyCollectionChanged ?? _logList.Items;
        if (ReferenceEquals(source, _measureSource)) return;
        if (_measureSource is not null)
            _measureSource.CollectionChanged -= OnLogMeasureSourceChanged;
        _measureSource = source;
        _measureSource.CollectionChanged += OnLogMeasureSourceChanged;
        QueueTailColumnWidths();
    }

    private void OnLogMeasureSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => QueueTailColumnWidths();

    private void QueueTailColumnWidths()
    {
        if (_disposed || _tailWidthsQueued) return;
        _tailWidthsQueued = true;
        _owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            _tailWidthsQueued = false;
            if (!_disposed) ApplyTailColumnWidths();
        }), DispatcherPriority.Background);
    }

    private void ApplyTailColumnWidths()
    {
        if (_gridView is null || _fontProbe is not { } probe) return;
        var columns = _gridView.Columns;
        if (columns.Count <= 1) return;
        var fontSize = probe.FontSize;
        var typeface = new Typeface(probe.FontFamily, probe.FontStyle, probe.FontWeight, probe.FontStretch);
        var pixelsPerDip = VisualTreeHelper.GetDpi(_owner).PixelsPerDip;

        foreach (var column in columns)
        {
            if (_userSizedColumns.Contains(column)) continue;
            if (TailColumnValue(column.Header) is not { } value) continue;
            var content = 0.0;
            var rows = 0;
            foreach (var item in MeasureRows)
            {
                if (item is not GitLogRow row || value(row) is not { Length: > 0 } text) continue;
                content = Math.Max(content, Measure(text));
                if (++rows >= TailMeasureRows) break;
            }
            if (rows == 0) continue;
            var header = column.Header is string title && title.Length > 0 ? Measure(title) : 0;
            var width = GitLogColumnLayoutPolicy.LogTailColumnWidth(
                content, header, ReferenceEquals(column, columns[columns.Count - 1]), ColumnGapGridViewRowPresenter.ColumnGap);
            if (Math.Abs(ColumnWidth(column) - width) >= 1) column.Width = width;
        }

        double Measure(string text) => new FormattedText(
            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize,
            Brushes.Black, pixelsPerDip).WidthIncludingTrailingWhitespace;
    }

    private void ApplyFillColumn()
    {
        if (_gridView is null || _scrollViewer is null || _fillColumnUpdating) return;
        var columns = _gridView.Columns;
        if (columns.Count == 0) return;
        var viewport = _scrollViewer.ViewportWidth;
        if (viewport <= 0) return;
        var extent = 0.0;
        foreach (var column in columns) extent += ColumnWidth(column);
        var fillColumn = columns[0];
        var current = fillColumn.ActualWidth;
        var target = GitLogColumnLayoutPolicy.LogFillColumnWidth(current, viewport, extent);
        if (Math.Abs(current - target) < 1.5) return;
        _fillColumnUpdating = true;
        try { fillColumn.Width = target; }
        finally { _fillColumnUpdating = false; }
    }

    private static double ColumnWidth(GridViewColumn column)
        => double.IsNaN(column.Width) ? column.ActualWidth : column.Width;

    private void UpdateThumbPositions()
    {
        if (_disposed || _gridView is null || _scrollViewer is null) return;
        ApplyFillColumn();
        var offset = -_scrollViewer.HorizontalOffset;
        var x = offset;
        for (var index = 0; index < _gridView.Columns.Count; index++)
        {
            x += ColumnWidth(_gridView.Columns[index]);
            var thumb = _thumbs[index];
            Canvas.SetLeft(thumb, x - thumb.Width / 2);
            Canvas.SetTop(thumb, 0);
            thumb.Height = _logList.ActualHeight;
        }

        if (_headerFillerMask is null) return;
        var headerHeight = _headerPresenter?.ActualHeight ?? 0;
        const double overlapBuffer = 12;
        if (_logList.ActualWidth - x <= overlapBuffer + 1)
        {
            _headerFillerMask.Width = 0;
            _headerFillerMask.Height = 0;
            return;
        }
        var maskLeft = Math.Max(0, x - overlapBuffer);
        var fillerWidth = Math.Max(0, _logList.ActualWidth - maskLeft);
        Canvas.SetLeft(_headerFillerMask, maskLeft);
        Canvas.SetTop(_headerFillerMask, 0);
        _headerFillerMask.Width = fillerWidth;
        _headerFillerMask.Height = headerHeight;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scrollViewer) return scrollViewer;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, index));
            if (found is not null) return found;
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } found) return found;
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var found in FindVisualChildren<T>(child)) yield return found;
        }
    }
}
