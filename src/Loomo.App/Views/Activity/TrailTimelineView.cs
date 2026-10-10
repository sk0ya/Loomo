using System.Collections.Specialized;
using System.Windows.Automation;
using Shape = System.Windows.Shapes.Shape;
using Rectangle = System.Windows.Shapes.Rectangle;
using Line = System.Windows.Shapes.Line;
using ShapePath = System.Windows.Shapes.Path;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// 軌跡の展開ビュー（§27.14）。バーの上に開く帯で、表示中の日を<b>時刻の軸</b>に並べ直す：
/// 上から「移動」（軌跡の点）・「コマンド」（可視ターミナルで終わった実行の成否）・「しおり」（§27.13）。
/// バーのドット列は並び順なので「何をしたか」は読めても「いつ・どれだけ間が空いたか」「どの移動の合間に
/// テストが落ちたか」は読めない——それを同じ時刻の上に重ねて見せるのがこの面の役目。
/// 点を押せばバーと同じジャンプ、コマンドを押せばそれを実行したときにいた地点へ戻る。
/// 素のホイールは横スクロール、Ctrl＋ホイールでカーソルの下の時刻を保ったまま拡大縮小。
/// DataContext は <see cref="TrailViewModel"/>。見えている間だけ組み立てる（隠れている間は何もしない）。
/// </summary>
public sealed class TrailTimelineView : Grid
{
    private const double LabelColumnWidth = 64;
    private const double AxisHeight = 18;
    private const double LaneHeight = 22;
    private const double SidePadding = 12;
    private const double NoteMaxWidth = 200;
    private static readonly string[] LaneNames = ["移動", "コマンド", "しおり"];

    private readonly ScrollViewer _scroll;
    private readonly Canvas _canvas;
    private readonly DispatcherTimer _clock;
    private TrailViewModel? _trail;
    private readonly List<TrailEntryViewModel> _watchedEntries = new();
    private bool _rebuildPending;
    private double _pixelsPerMinute = TrailTimelineLayout.DefaultPixelsPerMinute;
    private DateTime _start;
    // 人が横へ動かしたら、追記のたびに末尾へ引き戻さない（バーの _trailBrowsingPast と同じ考え方）。
    private bool _userScrolled;
    private DateOnly? _builtDay;

    public TrailTimelineView()
    {
        Height = AxisHeight + LaneHeight * LaneNames.Length + 6;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColumnWidth) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labels = new Canvas { ClipToBounds = true };
        for (var i = 0; i < LaneNames.Length; i++)
        {
            var text = Text(LaneNames[i], "FgDim");
            Canvas.SetLeft(text, 10);
            Canvas.SetTop(text, LaneTop(i) + 3);
            labels.Children.Add(text);
        }
        Children.Add(labels);

        _canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.HorizontalOnly,
            Content = _canvas,
        };
        _scroll.PreviewMouseWheel += OnWheel;
        SetColumn(_scroll, 1);
        Children.Add(_scroll);

        // 今日を見ている間は「今」の線を1分ごとに進める。
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _clock.Tick += (_, _) => RequestRebuild();

        DataContextChanged += (_, _) => Attach(DataContext as TrailViewModel);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { RequestRebuild(); _clock.Start(); }
            else _clock.Stop();
        };
        Unloaded += (_, _) => _clock.Stop();
    }

    private static double LaneTop(int lane) => AxisHeight + LaneHeight * lane;

    private void Attach(TrailViewModel? trail)
    {
        if (_trail is not null)
        {
            _trail.Entries.CollectionChanged -= OnEntriesChanged;
            _trail.PropertyChanged -= OnTrailPropertyChanged;
            _trail.CommandRunRecorded -= OnCommandRunRecorded;
        }
        _trail = trail;
        if (trail is not null)
        {
            trail.Entries.CollectionChanged += OnEntriesChanged;
            trail.PropertyChanged += OnTrailPropertyChanged;
            trail.CommandRunRecorded += OnCommandRunRecorded;
        }
        RequestRebuild();
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RequestRebuild();
    private void OnCommandRunRecorded(object? sender, EventArgs e) => RequestRebuild();

    private void OnTrailPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrailViewModel.CurrentIndex) or nameof(TrailViewModel.DisplayDate))
            RequestRebuild();
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // しおりの付け外し・デデュープで時刻が進んだ点。
        if (e.PropertyName is nameof(TrailEntryViewModel.Note) or nameof(TrailEntryViewModel.Timestamp))
            RequestRebuild();
    }

    /// <summary>組み立ては後回しにして1回へ畳む（記録は1操作で数回飛ぶ）。見えていなければ組み立てない。</summary>
    private void RequestRebuild()
    {
        if (_rebuildPending || !IsVisible)
            return;
        _rebuildPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _rebuildPending = false;
            if (IsVisible)
                Rebuild();
        });
    }

    private void Rebuild()
    {
        foreach (var entry in _watchedEntries)
            entry.PropertyChanged -= OnEntryPropertyChanged;
        _watchedEntries.Clear();
        _canvas.Children.Clear();
        if (_trail is null)
            return;

        var trail = _trail;
        var entries = trail.Entries.ToList();
        var runs = trail.LoadCommandRuns(trail.DisplayDate);
        DateTime? now = trail.IsViewingPast ? null : DateTime.Now;
        var (start, end) = TrailTimelineLayout.Range(
            entries.Select(e => e.Timestamp).Concat(runs.Select(r => r.Timestamp)), now);
        _start = start;
        var height = AxisHeight + LaneHeight * LaneNames.Length;
        _canvas.Width = TrailTimelineLayout.Width(start, end, _pixelsPerMinute) + SidePadding * 2;
        _canvas.Height = height;

        DrawAxis(start, end, height);
        if (now is { } n)
            DrawVertical(XOf(n), 2, height, "Accent", 1, 0.7, dashed: true);

        var current = trail.CurrentEntry;
        if (current is not null)
            DrawVertical(XOf(current.Timestamp), AxisHeight, height, "AccentHover", 1, 0.45);

        foreach (var entry in entries)
        {
            entry.PropertyChanged += OnEntryPropertyChanged;
            _watchedEntries.Add(entry);
            AddEntryMark(entry, ReferenceEquals(entry, current));
        }
        foreach (var run in runs)
            AddCommandMark(run);
        AddNotes(entries.Where(e => e.HasNote));

        ScrollIntoPlace(trail, current, now);
    }

    private double XOf(DateTime time) => SidePadding + TrailTimelineLayout.X(time, _start, _pixelsPerMinute);

    private void DrawAxis(DateTime start, DateTime end, double height)
    {
        var tick = TrailTimelineLayout.TickMinutes(_pixelsPerMinute);
        for (var t = start; t <= end; t = t.AddMinutes(tick))
        {
            var x = XOf(t);
            var onHour = t.Minute == 0;
            DrawVertical(x, onHour ? 2 : 10, height, onHour ? "FgDim" : "Border", 1, onHour ? 0.55 : 0.8);
            var label = Text(t.ToString("HH:mm"), onHour ? "Fg" : "FgDim");
            Canvas.SetLeft(label, x + 3);
            Canvas.SetTop(label, 0);
            _canvas.Children.Add(label);
        }
    }

    private void DrawVertical(double x, double top, double bottom, string brushKey, double thickness, double opacity, bool dashed = false)
    {
        var line = new Line { X1 = x, X2 = x, Y1 = top, Y2 = bottom, StrokeThickness = thickness, Opacity = opacity, IsHitTestVisible = false, SnapsToDevicePixels = true };
        line.SetResourceReference(Shape.StrokeProperty, brushKey);
        if (dashed)
            line.StrokeDashArray = [3, 3];
        _canvas.Children.Add(line);
    }

    /// <summary>移動の段：種別の4カテゴリ色（バーと同じ）の細い縦棒。現在地は太く・アクセント色。</summary>
    private void AddEntryMark(TrailEntryViewModel entry, bool isCurrent)
    {
        var bar = new Rectangle
        {
            Width = isCurrent ? 4 : 3,
            Height = isCurrent ? 18 : 14,
            RadiusX = 1, RadiusY = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bar.SetResourceReference(Shape.FillProperty, isCurrent ? "AccentHover" : CategoryBrush(entry.Kind));
        var hit = Hit(bar, entry.Tooltip, entry.AccessibleName, width: 9);
        hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; _trail?.JumpCommand.Execute(entry); };
        Place(hit, XOf(entry.Timestamp), 0);
    }

    /// <summary>コマンドの段：成功＝緑・失敗＝赤・終了コード不明＝淡色の小さな角丸。押すと実行時にいた地点へ。</summary>
    private void AddCommandMark(CommandRunRecord run)
    {
        var mark = new Border { Width = 6, Height = 12, CornerRadius = new CornerRadius(2) };
        mark.SetResourceReference(Border.BackgroundProperty, run.ExitCode switch
        {
            0 => "Success",
            null => "FgDim",
            _ => "Error",
        });
        var outcome = run.ExitCode switch
        {
            0 => "成功",
            null => "終了コード不明",
            { } code => $"失敗（終了コード {code}）",
        };
        var tooltip = $"{run.Timestamp:HH:mm:ss}  {run.Command}\n{outcome}\nクリックでこのときいた地点へ戻る";
        var hit = Hit(mark, tooltip, $"コマンド {run.Command} {outcome}", width: 10);
        hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; _trail?.JumpToTime(run.Timestamp); };
        Place(hit, XOf(run.Timestamp), 1);
    }

    /// <summary>しおりの段：リボン＋メモ。前のメモと重なるならメモは出さずリボンだけ（ツールチップで読める）。</summary>
    private void AddNotes(IEnumerable<TrailEntryViewModel> notes)
    {
        var occupiedUntil = double.NegativeInfinity;
        foreach (var entry in notes.OrderBy(e => e.Timestamp))
        {
            var x = XOf(entry.Timestamp);
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var ribbon = new ShapePath
            {
                Data = Geometry.Parse("M0,0 L7,0 L7,10 L3.5,7.5 L0,10 Z"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ribbon.SetResourceReference(Shape.FillProperty, "Accent");
            panel.Children.Add(ribbon);
            var showText = x - 4 >= occupiedUntil;
            if (showText)
            {
                var text = Text(entry.Note ?? "", "Fg");
                text.MaxWidth = NoteMaxWidth;
                text.TextTrimming = TextTrimming.CharacterEllipsis;
                text.Margin = new Thickness(4, 0, 0, 0);
                panel.Children.Add(text);
                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                occupiedUntil = x + 12 + text.DesiredSize.Width;
            }
            var hit = new Border
            {
                Background = Brushes.Transparent,
                Height = LaneHeight,
                Cursor = Cursors.Hand,
                Child = panel,
                ToolTip = $"しおり: {entry.Note}\n{entry.Tooltip}",
            };
            ToolTipService.SetInitialShowDelay(hit, 150);
            AutomationProperties.SetName(hit, $"しおり「{entry.Note}」");
            hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; _trail?.JumpCommand.Execute(entry); };
            Canvas.SetLeft(hit, x - 3.5);
            Canvas.SetTop(hit, LaneTop(2));
            _canvas.Children.Add(hit);
        }
    }

    private static Border Hit(FrameworkElement mark, string tooltip, string name, double width)
    {
        var hit = new Border
        {
            Width = width,
            Height = LaneHeight,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = mark,
            ToolTip = tooltip,
        };
        ToolTipService.SetInitialShowDelay(hit, 150);
        AutomationProperties.SetName(hit, name);
        return hit;
    }

    private void Place(FrameworkElement hit, double centerX, int lane)
    {
        Canvas.SetLeft(hit, centerX - hit.Width / 2);
        Canvas.SetTop(hit, LaneTop(lane));
        _canvas.Children.Add(hit);
    }

    private static TextBlock Text(string text, string brushKey)
    {
        var block = new TextBlock { Text = text, IsHitTestVisible = false };
        TextOptions.SetTextFormattingMode(block, TextFormattingMode.Ideal);
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        block.SetResourceReference(TextBlock.FontSizeProperty, "Fs11");
        return block;
    }

    /// <summary>バーの点と同じ4カテゴリ（§27.10）：内容=Fg／変更=Success／構造=FgDim／AI=Accent。</summary>
    private static string CategoryBrush(TrailEntryKind kind) => kind switch
    {
        TrailEntryKind.Edit or TrailEntryKind.Git => "Success",
        TrailEntryKind.Pane or TrailEntryKind.Panel or TrailEntryKind.Layout => "FgDim",
        TrailEntryKind.Session => "Accent",
        _ => "Fg",
    };

    /// <summary>日が変わったら現在地（無ければ末尾）を見せる。同じ日の追記では、人が横へ動かしていなければ
    /// 今日のライブ端（末尾）へ追従し、動かしていれば位置を保つ。</summary>
    private void ScrollIntoPlace(TrailViewModel trail, TrailEntryViewModel? current, DateTime? now)
    {
        var dayChanged = _builtDay != trail.DisplayDate;
        _builtDay = trail.DisplayDate;
        if (dayChanged)
            _userScrolled = false;
        if (_userScrolled && !dayChanged)
            return;
        _canvas.UpdateLayout();
        var viewport = _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : ActualWidth - LabelColumnWidth;
        var focusX = current is not null && (trail.IsViewingPast || trail.CurrentIndex < trail.Entries.Count - 1)
            ? XOf(current.Timestamp)
            : now is { } n ? XOf(n) : _canvas.Width;
        _scroll.ScrollToHorizontalOffset(Math.Max(0, focusX - viewport * 0.75));
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var cursorX = e.GetPosition(_scroll).X;
            var old = _pixelsPerMinute;
            _pixelsPerMinute = TrailTimelineLayout.Zoom(old, e.Delta);
            if (_pixelsPerMinute == old)
                return;
            // 左の余白（SidePadding）は縮尺で伸び縮みしないので、軸の上の位置で比を取る。
            var offset = TrailTimelineLayout.ScrollOffsetKeeping(
                _scroll.HorizontalOffset - SidePadding, cursorX, old, _pixelsPerMinute) + SidePadding;
            _userScrolled = true;
            Rebuild();
            _scroll.ScrollToHorizontalOffset(Math.Max(0, offset));
            return;
        }
        var target = Math.Clamp(_scroll.HorizontalOffset - e.Delta, 0, _scroll.ScrollableWidth);
        _scroll.ScrollToHorizontalOffset(target);
        // 末尾まで戻したらライブ追従を再開する。
        _userScrolled = target < _scroll.ScrollableWidth - 1;
    }
}
