using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>Git コミット一覧の GridView 列幅、境界ドラッグ、幅計測を管理する。
///
/// <para>ドラッグ中の幅は<b>掴んだ瞬間の幅とマウスの位置</b>から決める（グラフ幅のつまみと同じ）。
/// <c>DragDelta</c> の変化量を <c>ActualWidth</c> へ足し込むと、配置が追いつく前に次の変化量が来たときや
/// 最小幅で止まった後に、境界がマウスから離れていく。広げられるのは余りを吸う列（コミット）が最小幅に
/// 達するまで、かつその列の中身がちょうど収まる幅まで——日時・作成者・ID は中身より広げても空白が
/// 増えるだけで、その分件名が削られる。前者を超えると横スクロールが出て境界がマウスから逃げる。</para>
///
/// <para>手で決めた幅はドラッグを終えたときに設定へ残し、次に開いたときに戻す。境界のダブルクリックで
/// その列を「中身に合わせる」に戻す。</para>
/// </summary>
internal sealed class GitLogColumnResizeController : IDisposable
{
    private const double UserSizeThresholdPx = 3;
    private const double MinColumnWidth = 30;
    private const double HeaderPaddingMinWidth = 2;
    private const int TailMeasureRows = 400;
    private const string FillColumnHeader = "コミット";

    private static readonly (string Header, Func<GitLogRow, string?> Value)[] TailColumns =
    [
        ("日時", row => row.Date),
        ("作成者", row => row.Author),
        ("ID", row => row.ShortHash),
    ];

    private readonly FrameworkElement _owner;
    private readonly ListView _logList;
    private readonly Canvas _overlay;
    private readonly Func<GitPaneLayoutSettings?> _layout;
    private readonly Action _saveLayout;
    private readonly List<Thumb> _thumbs = new();
    private readonly HashSet<GridViewColumn> _userSizedColumns = new();
    private readonly HashSet<GridViewColumn> _watchedColumns = new();
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
    private GridViewColumn? _dragColumn;
    private bool _dragGrowsLeft;
    private double _dragStartWidth;
    private double _dragStartX;
    private double _dragMaxWidth;
    private bool _dragMoved;

    internal GitLogColumnResizeController(
        FrameworkElement owner, ListView logList, Canvas overlay,
        Func<GitPaneLayoutSettings?> layout, Action saveLayout)
    {
        _owner = owner;
        _logList = logList;
        _overlay = overlay;
        _layout = layout;
        _saveLayout = saveLayout;
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
        if (_gridView is not null)
            _gridView.Columns.CollectionChanged -= OnColumnsCollectionChanged;
        if (_measureSource is not null)
            _measureSource.CollectionChanged -= OnLogMeasureSourceChanged;
        foreach (var (descriptor, target, handler) in _valueHandlers)
            descriptor.RemoveValueChanged(target, handler);
        _valueHandlers.Clear();
        _watchedColumns.Clear();
        RemoveThumbs();
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

        BuildThumbs();
        gridView.Columns.CollectionChanged += OnColumnsCollectionChanged;

        _scrollViewer.ScrollChanged += OnLogScrollChanged;
        _logList.SizeChanged += OnLogListSizeChanged;
        Watch(DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ListView)),
            _logList, OnLogItemsSourceChanged);
        ApplySavedWidths();
        HookMeasureSource();
        HideHeaderGrippers();
        UpdateThumbPositions();
    }

    /// <summary>設定に残っている手決めの列幅を当てる（そうした列は中身に合わせた幅の自動調整から外す）。</summary>
    internal void ApplySavedWidths()
    {
        if (_disposed || _gridView is null || _layout() is not { } layout) return;
        foreach (var column in _gridView.Columns)
        {
            if (TailColumnValue(column.Header) is null || ColumnId(column) is not { } id) continue;
            if (!layout.LogColumnWidths.TryGetValue(id, out var width)) continue;
            _userSizedColumns.Add(column);
            column.Width = Math.Max(MinColumnWidth, width);
        }
    }

    /// <summary>列境界ごとのつまみを今の列数ぶん作り直す。列の表示／非表示・並べ替えで列数や
    /// 「最後の列」が変わるので、そのたびに呼ぶ（つまみは位置で列を指すので並べ替えにも追従する）。</summary>
    private void BuildThumbs()
    {
        if (_gridView is null) return;
        RemoveThumbs();
        var thumbStyle = (Style)_owner.FindResource("LogColumnResizeThumb");
        for (var index = 0; index < _gridView.Columns.Count; index++)
        {
            var column = _gridView.Columns[index];
            var thumb = new Thumb
            {
                Width = 6,
                Style = thumbStyle,
                ToolTip = "ドラッグで列幅を変更（ダブルクリックで中身に合わせる）",
            };
            if (index == _gridView.Columns.Count - 1) thumb.Visibility = Visibility.Collapsed;
            thumb.DragStarted += OnLogColumnDragStarted;
            thumb.DragDelta += OnLogColumnThumbDragDelta;
            thumb.DragCompleted += OnLogColumnDragCompleted;
            thumb.MouseDoubleClick += OnLogColumnThumbDoubleClick;
            _thumbs.Add(thumb);
            _overlay.Children.Add(thumb);
            if (_watchedColumns.Add(column))
                Watch(DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn)),
                    column, OnLogColumnWidthChanged);
        }
    }

    private void RemoveThumbs()
    {
        foreach (var thumb in _thumbs)
        {
            thumb.DragStarted -= OnLogColumnDragStarted;
            thumb.DragDelta -= OnLogColumnThumbDragDelta;
            thumb.DragCompleted -= OnLogColumnDragCompleted;
            thumb.MouseDoubleClick -= OnLogColumnThumbDoubleClick;
            _overlay.Children.Remove(thumb);
        }
        _thumbs.Clear();
    }

    /// <summary>列の出し入れ・並べ替え。見出しは作り直されるので既定のつまみをまた隠し、
    /// 「最後の列だけ隙間なし」の幅も並びで変わるので測り直す。</summary>
    private void OnColumnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;
        BuildThumbs();
        _owner.Dispatcher.BeginInvoke(new Action(() => HideHeaderGrippers()), DispatcherPriority.Loaded);
        QueueTailColumnWidths();
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

    /// <summary>つまみが幅を変える列。余りを吸う列（コミット）は幅を直接いじらない。境界がマウスに
    /// 付いてくるのは、境界と吸う列の<b>反対側</b>の列を変えたとき——吸う列が左にあれば右隣を
    /// （左へ引くと広がる）、右にあれば左隣を変える。</summary>
    private GridViewColumn? ResizeTarget(Thumb thumb, out bool growsLeft)
    {
        growsLeft = false;
        if (_gridView is null) return null;
        var columns = _gridView.Columns;
        var index = _thumbs.IndexOf(thumb);
        if (index < 0 || index + 1 >= columns.Count) return null;
        growsLeft = columns.IndexOf(FillColumn(columns)) <= index;
        return columns[growsLeft ? index + 1 : index];
    }

    private void OnLogColumnDragStarted(object sender, DragStartedEventArgs e)
    {
        _dragColumn = ResizeTarget((Thumb)sender, out _dragGrowsLeft);
        _dragMoved = false;
        if (_dragColumn is null || _gridView is null) return;
        _dragStartWidth = ColumnWidth(_dragColumn);
        _dragStartX = Mouse.GetPosition(_logList).X;
        // 広げてよいのは、吸う列が最小幅まで縮むぶんだけ。
        var fillSlack = Math.Max(0,
            ColumnWidth(FillColumn(_gridView.Columns)) - GitLogColumnLayoutPolicy.FillColumnMinWidth);
        _dragMaxWidth = _dragStartWidth + fillSlack;
        // 中身より広くしても空白が増えるだけなので、中身の幅で止める（縮めるのは自由）。
        if (FitWidth(_dragColumn) is { } fit) _dragMaxWidth = Math.Min(_dragMaxWidth, fit);
    }

    private void OnLogColumnWidthChanged(object? sender, EventArgs e) => UpdateThumbPositions();
    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateThumbPositions();
    private void OnLogListSizeChanged(object sender, SizeChangedEventArgs e) => UpdateThumbPositions();
    private void OnLogFontSizeChanged(object? sender, EventArgs e) => QueueTailColumnWidths();
    private void OnLogItemsSourceChanged(object? sender, EventArgs e) => HookMeasureSource();

    private void OnLogColumnThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_dragColumn is not { } column) return;
        var moved = Mouse.GetPosition(_logList).X - _dragStartX;
        // 押しただけ（ダブルクリックの1打目を含む）で中身合わせの幅から外さない。
        if (!_dragMoved && Math.Abs(moved) < UserSizeThresholdPx) return;
        _dragMoved = true;
        _userSizedColumns.Add(column);
        var width = GitLogColumnLayoutPolicy.DraggedColumnWidth(
            _dragStartWidth, _dragGrowsLeft ? -moved : moved, MinColumnWidth, _dragMaxWidth);
        if (Math.Abs(ColumnWidth(column) - width) >= 0.5) column.Width = width;
    }

    private void OnLogColumnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        var column = _dragColumn;
        _dragColumn = null;
        if (column is null || !_dragMoved || ColumnId(column) is not { } id || _layout() is not { } layout) return;
        layout.LogColumnWidths[id] = Math.Round(ColumnWidth(column));
        _saveLayout();
    }

    /// <summary>境界のダブルクリック＝その列を「中身に合わせる」へ戻す（手決めの幅を忘れる）。</summary>
    private void OnLogColumnThumbDoubleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (ResizeTarget((Thumb)sender, out _) is not { } column) return;
        _userSizedColumns.Remove(column);
        if (ColumnId(column) is { } id && _layout() is { } layout && layout.LogColumnWidths.Remove(id))
            _saveLayout();
        QueueTailColumnWidths();
    }

    /// <summary>列の ID（<see cref="GitLogColumnLayout"/>）。見出しの文言から引く。</summary>
    private static string? ColumnId(GridViewColumn column)
    {
        foreach (var id in GitLogColumnLayout.DefaultOrder)
            if (Equals(column.Header, GitLogColumnLayout.Title(id))) return id;
        return null;
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

    /// <summary>中身に合わせた幅を当てる。手で決めた列はその幅を保つが、中身より広い分は削る
    /// （日時・作成者・ID は中身の幅を超えて広げても空白が増えるだけ）。</summary>
    private void ApplyTailColumnWidths()
    {
        if (_gridView is null) return;
        foreach (var column in _gridView.Columns)
        {
            if (FitWidth(column) is not { } fit) continue;
            if (_userSizedColumns.Contains(column) && ColumnWidth(column) <= fit) continue;
            if (Math.Abs(ColumnWidth(column) - fit) >= 1) column.Width = fit;
        }
    }

    /// <summary>列の中身（先頭から <see cref="TailMeasureRows"/> 行と見出し）がちょうど収まる幅。
    /// 中身合わせの対象でない列・まだ行が無いときは null。</summary>
    private double? FitWidth(GridViewColumn column)
    {
        if (_gridView is null || _fontProbe is not { } probe) return null;
        var columns = _gridView.Columns;
        if (columns.Count <= 1 || TailColumnValue(column.Header) is not { } value) return null;
        var fontSize = probe.FontSize;
        var typeface = new Typeface(probe.FontFamily, probe.FontStyle, probe.FontWeight, probe.FontStretch);
        var pixelsPerDip = VisualTreeHelper.GetDpi(_owner).PixelsPerDip;

        var content = 0.0;
        var rows = 0;
        foreach (var item in MeasureRows)
        {
            if (item is not GitLogRow row || value(row) is not { Length: > 0 } text) continue;
            content = Math.Max(content, Measure(text));
            if (++rows >= TailMeasureRows) break;
        }
        if (rows == 0) return null;
        var header = column.Header is string title && title.Length > 0 ? Measure(title) : 0;
        return GitLogColumnLayoutPolicy.LogTailColumnWidth(
            content, header, ReferenceEquals(column, columns[columns.Count - 1]), ColumnGapGridViewRowPresenter.ColumnGap);

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
        // 見出し行は列の右に最低 2px の埋め草見出しを必ず置く（GridViewHeaderRowPresenter の既定）ので、
        // 中身の幅は列幅の合計より 2px 広い。これを数えないと吸う列が 2px 広すぎ、休んでいる間も
        // 横スクロールバーが出たままになる。
        var extent = HeaderPaddingMinWidth;
        foreach (var column in columns) extent += ColumnWidth(column);
        var fillColumn = FillColumn(columns);
        // ActualWidth ではなく Width で比べる。ドラッグ中は配置より先に次の幅が来るので、ActualWidth は
        // 1つ前の幅のまま＝他の列の新しい幅と混ぜると吸う量がずれて横スクロールバーが出る。
        var current = ColumnWidth(fillColumn);
        var target = GitLogColumnLayoutPolicy.LogFillColumnWidth(current, viewport, extent);
        if (Math.Abs(current - target) < 1.5) return;
        _fillColumnUpdating = true;
        try { fillColumn.Width = target; }
        finally { _fillColumnUpdating = false; }
    }

    /// <summary>余りの幅を吸う列（コミット）。並べ替えでどこへ動いても同じ列。見当たらなければ先頭。</summary>
    private static GridViewColumn FillColumn(GridViewColumnCollection columns)
    {
        foreach (var column in columns)
            if (Equals(column.Header, FillColumnHeader)) return column;
        return columns[0];
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
