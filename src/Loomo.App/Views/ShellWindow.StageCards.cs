namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ソロモード（舞台＋袖＋俯瞰）のカード／ミニチュア描画。袖・俯瞰カードの描画元の アレンジ、ライブ縮小カード（VisualBrush）、舞台スロットの生成。モード制御は ShellWindow.Stage.cs。</summary>
public partial class ShellWindow {
    private const double WingCardWidth = 180;
    private const double OverviewCardWidth = 320;
    private const double CardAspect = 3.0 / 2.0;
    private const double WingRestOpacity = 0.72;
    private double _layoutWingSourceWidth;
    private bool _layoutWingBuildQueued;
    private bool _layoutWingBuildPending;
    private void ArrangeThumbnailSource(PaneKind kind, Size virtualSize) {
        var element = _paneElements[kind];
        if (element.Parent is Panel parent)
            parent.Children.Remove(element);
        element.Visibility = Visibility.Visible;
        var w = Math.Max(virtualSize.Width, 1);
        var h = Math.Max(w / CardAspect, 1);
        var host = new Grid {
            Width = w, Height = h, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Clip = new RectangleGeometry(new Rect(0, 0, w, h)), };
        host.Children.Add(element);
        StageSourceArea.Children.Add(host);
        var sourceSize = new Size(w, h);
        host.Measure(sourceSize);
        host.Arrange(new Rect(sourceSize));
        host.UpdateLayout();
        _stageThumbnailHosts[kind] = host;
    }
    private void BuildStageThumbnailSources(Size virtualSize) {
        var kinds = _overviewActive
            ? OverviewKinds()
            : StageOrder.Where(k => !OnStage(k) && IsSessionEnabled(k));
        foreach (var kind in kinds)
            ArrangeThumbnailSource(kind, virtualSize);
    }
    private void RebuildWings() {
        PaneLayoutDebugLog.Log("RebuildWings()", withCaller: true);
        if (!_stageActive && (StageSourceArea.ActualWidth <= 0 || StageSourceArea.ActualHeight <= 0)) {
            ScheduleLayoutWings();
            return;
        }
        if (!_stageActive)
            _layoutWingBuildPending = false;
        WingStrip.Children.Clear();
        if (_stageActive) {
            foreach (var kind in StageOrder.Where(k => !OnStage(k) && IsSessionEnabled(k)))
                WingStrip.Children.Add(BuildSessionCard(kind, WingCardWidth, isOverview: false));
        } else {
            BuildLayoutWingSources();
            foreach (var kind in StageOrder.Where(k => IsSessionEnabled(k) && !IsShownInMain(k)))
                WingStrip.Children.Add(BuildLayoutWingCard(kind, WingCardWidth));
        }
    }
    private void BuildLayoutWingSources() {
        StageSourceArea.Children.Clear();
        _stageThumbnailHosts.Clear();
        var virtualSize = new Size(StageSourceArea.ActualWidth, StageSourceArea.ActualHeight);
        _layoutWingSourceWidth = virtualSize.Width;
        foreach (var kind in StageOrder.Where(k => IsSessionEnabled(k) && !IsShownInMain(k)))
            ArrangeThumbnailSource(kind, virtualSize);
    }
    private void ScheduleLayoutWings() {
        if (_stageActive)
            return;
        if (_paneSplitterDragging) {
            PaneLayoutDebugLog.Log("ScheduleLayoutWings skipped: splitter drag in progress");
            return;
        }
        var hasWings = StageOrder.Any(k => IsSessionEnabled(k) && !IsShownInMain(k));
        PaneLayoutDebugLog.Log($"ScheduleLayoutWings hasWings={hasWings} prevWingColumnWidth={WingColumn.Width}", withCaller: true);
        WingColumn.Width = hasWings ? new GridLength(WingColumnReserve) : GridLength.Auto;
        WingHost.Visibility = hasWings ? Visibility.Visible : Visibility.Collapsed;
        if (!hasWings) {
            _layoutWingBuildPending = false;
            WingStrip.Children.Clear();
            StageSourceArea.Children.Clear();
            _stageThumbnailHosts.Clear();
            return;
        }
        _layoutWingBuildPending = true;
        if (_layoutWingBuildQueued)
            return;
        _layoutWingBuildQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => {
            _layoutWingBuildQueued = false;
            if (_paneSplitterDragging)
                return;
            if (_stageActive || !_layoutWingBuildPending)
                return;
            if (StageSourceArea.ActualWidth <= 0 || StageSourceArea.ActualHeight <= 0)
                return;
            _layoutWingBuildPending = false;
            PaneLayoutDebugLog.Log("ScheduleLayoutWings deferred callback -> RebuildWings()");
            RebuildWings();
            UpdateWingHostVisibility();
        }));
    }
    private void OnStageSourceAreaSizeChanged(object sender, SizeChangedEventArgs e) {
        if (_stageActive || e.NewSize.Width <= 0
            || Math.Abs(e.NewSize.Width - _layoutWingSourceWidth) <= 1)
            return;
        PaneLayoutDebugLog.Log($"OnStageSourceAreaSizeChanged {_layoutWingSourceWidth:0.#} -> {e.NewSize.Width:0.#}");
        ScheduleLayoutWings();
    }
    private void UpdateWingHostVisibility() {
        if (WingHost is null)
            return;
        WingHost.Visibility = WingStrip.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OverviewButton.Visibility = _stageActive ? Visibility.Visible : Visibility.Collapsed;
    }
    private Border BuildSessionCard(PaneKind kind, double width, bool isOverview) {
        Visual source = _stageThumbnailHosts.TryGetValue(kind, out var host) ? host : _paneElements[kind];
        return BuildCard(kind, width, source, isOverview, () => { SetStagePane(kind); FocusPane(kind); });
    }
    private Border BuildLayoutWingCard(PaneKind kind, double width) {
        Visual source = _stageThumbnailHosts.TryGetValue(kind, out var host) ? host : _paneElements[kind];
        return BuildCard(kind, width, source, isOverview: false, () => {
                if (_zoomedPane is not null) {
                    if (IsPaneVisible(kind))
                        ZoomPane(kind);   // ズーム中の袖カード＝そのペインを舞台（ズーム）へ昇格
                    return;
                }
                if (IsPaneVisible(kind)) {
                    FocusPane(kind);
                    return;
                }
                PlacePaneByBehavior(kind);
                FocusPane(kind);
            });
    }
    private PaneKind? TopLeftPane() {
        PaneKind? best = null;
        Rect bestRect = default;
        foreach (var leaf in AllLeaves()) {
            if (leaf.Hidden || !TryGetPaneRect(leaf.Kind, out var rect))
                continue;
            if (best is null
                || rect.Y < bestRect.Y - 0.5
                || (Math.Abs(rect.Y - bestRect.Y) <= 0.5 && rect.X < bestRect.X)) {
                best = leaf.Kind;
                bestRect = rect;
            }
        }
        return best ?? AllLeaves().FirstOrDefault(l => !l.Hidden)?.Kind;
    }
    private PaneKind? TopRightPane()
        => PaneLayoutTree.RightmostVisibleLeaf(PaneLayoutTree.TopRow(_root))?.Kind
            ?? AllLeaves().FirstOrDefault(l => !l.Hidden)?.Kind;
    private PaneKind? TopRowLeftPane()
        => PaneLayoutTree.LeftmostVisibleLeaf(PaneLayoutTree.TopRow(_root))?.Kind;
    private Border BuildCard(PaneKind kind, double width, Visual source, bool isOverview, Action onClick) {
        var borderBrush = (Brush)FindResource("Border");
        var accent = (Brush)FindResource("Accent");
        var onStage = isOverview && OnStage(kind);
        var height = Math.Round(width / CardAspect);
        var card = new Border {
            Width = width, Height = height, Margin = isOverview ? new Thickness(10) : new Thickness(0, 4, 0, 4), CornerRadius = new CornerRadius(6), Background = (Brush)FindResource("Panel"), BorderBrush = onStage ? accent : borderBrush, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = PaneLabel(kind), Clip = new RectangleGeometry(new Rect(0, 0, width, height), 6, 6), };
        var root = new Grid { ClipToBounds = true };
        var sourceWidth = source is FrameworkElement sourceElement
            ? double.IsFinite(sourceElement.Width) && sourceElement.Width > 0
                ? sourceElement.Width
                : sourceElement.ActualWidth
            : width;
        var sourceHeight = source is FrameworkElement sourceElement2
            ? double.IsFinite(sourceElement2.Height) && sourceElement2.Height > 0
                ? sourceElement2.Height
                : sourceElement2.ActualHeight
            : height;
        root.Children.Add(new Border {
            IsHitTestVisible = false, Background = new VisualBrush(source) {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, Math.Max(sourceWidth, 1), Math.Max(sourceHeight, 1)), Stretch = Stretch.Uniform, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, }, });
        root.Children.Add(new Border {
            VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Color.FromArgb(0xB4, 0x10, 0x10, 0x10)), Child = new TextBlock {
                Text = PaneLabel(kind), FontSize = UiFontManager.Scaled(isOverview ? 12 : 11), Margin = new Thickness(8, 3, 8, 3), Foreground = Brushes.White, }, });
        card.Child = root;
        AttachActivityBadge(root, kind, isOverview);
        var rest = isOverview ? 1.0 : WingRestOpacity;
        card.Opacity = rest;
        card.MouseEnter += (_, _) => {
            card.BorderBrush = accent;
            card.Opacity = 1;
        };
        card.MouseLeave += (_, _) => {
            card.BorderBrush = onStage ? accent : borderBrush;
            card.Opacity = rest;
        };
        card.MouseLeftButtonUp += (_, e) => {
            _wingDragArmed = false;
            e.Handled = true; // 俯瞰レイヤの背景クリック（＝俯瞰を閉じる）と区別する
            onClick();
        };
        card.PreviewMouseLeftButtonDown += (_, e) => {
            _wingDragStart = e.GetPosition(this);
            _wingDragArmed = true;
        };
        card.PreviewMouseMove += (_, e) => {
            if (isOverview || !_wingDragArmed || e.LeftButton != MouseButtonState.Pressed)
                return;
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _wingDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(pos.Y - _wingDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            _wingDragArmed = false;
            if (_stageActive)
                BeginStageDrag(kind);
            else
                BeginWingDrag(kind);
        };
        return card;
    }
    private Border BuildLiveSlot(PaneKind kind) {
        var element = _paneElements[kind];
        element.Visibility = Visibility.Visible;
        var host = new Grid();
        host.SizeChanged += (_, e) => host.Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);
        host.Children.Add(element);
        return new Border {
            Background = (Brush)FindResource("Panel"), BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = host, };
    }
}
