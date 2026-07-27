namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ソロモード（舞台＋袖＋俯瞰）のカード／ミニチュア描画。袖・俯瞰カードの描画元の アレンジ、ライブ縮小カード（VisualBrush）、舞台スロットの生成。モード制御は ShellWindow.Stage.cs。</summary>
public partial class ShellWindow {
    private const double WingCardWidth = 180;
    private const double OverviewCardWidth = 320;
    private const double CardAspect = 3.0 / 2.0;
    private const double WingRestOpacity = 0.72;
    private double _layoutWingSourceWidth;
    /// <summary>今の描画元ホストを組んだ仮想幅（0＝未構築）。変わらない限りホストは据え置ける。</summary>
    private double _thumbnailSourceWidth;
    private bool _layoutWingBuildQueued;
    private bool _layoutWingBuildPending;
    /// <summary>袖に出すペイン（有効だが Main に出ていないもの）。</summary>
    private IReadOnlyList<PaneKind> LayoutWingKinds()
        => StageOrder.Where(k => IsSessionEnabled(k) && !IsShownInMain(k)).ToList();
    /// <summary>ミニチュアの描画元を <paramref name="sourceSize"/>（= <see cref="StageThumbnailPlanner"/> が
    /// 決めた固定仮想サイズ。Main 実寸ではない）でレイアウトする。</summary>
    private void ArrangeThumbnailSource(PaneKind kind, Size sourceSize)
        => PaneLayoutDebugLog.Time($"  ArrangeThumbnailSource({kind}) {sourceSize.Width:0}x{sourceSize.Height:0}",
            () => ArrangeThumbnailSourceCore(kind, sourceSize));
    private void ArrangeThumbnailSourceCore(PaneKind kind, Size sourceSize) {
        var element = _paneElements[kind];
        var w = Math.Max(sourceSize.Width, 1);
        var h = Math.Max(sourceSize.Height, 1);
        var host = new Grid {
            Width = w, Height = h, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Clip = new RectangleGeometry(new Rect(0, 0, w, h)), };
        if (element.Parent is Panel parent)
            parent.Children.Remove(element);
        element.Visibility = Visibility.Visible;
        host.Children.Add(element);
        StageSourceArea.Children.Add(host);
        var clamped = new Size(w, h);
        host.Measure(clamped);
        host.Arrange(new Rect(clamped));
        host.UpdateLayout();
        _stageThumbnailHosts[kind] = host;
    }
    /// <summary>描画元ホストを必要な集合へ差分で寄せる。据え置けるものは一切触らない
    /// （ペインの親を付け替える行為自体が Measure/Arrange と同等に高いため）。</summary>
    private void SyncThumbnailSources(IReadOnlyCollection<PaneKind> required, Size sourceSize) {
        var sizeChanged = StageThumbnailPlanner.SourceSizeChanged(_thumbnailSourceWidth, sourceSize.Width);
        var reusable = sizeChanged
            ? Array.Empty<PaneKind>()
            : _stageThumbnailHosts.Keys.Where(IsThumbnailSourceIntact).ToArray();
        var plan = StageThumbnailPlanner.PlanSources(
            _stageThumbnailHosts.Keys.ToArray(), reusable, required);
        PaneLayoutDebugLog.Log(
            $"SyncThumbnailSources keep={plan.Keep.Count} add={plan.Add.Count} remove={plan.Remove.Count}");
        foreach (var kind in plan.Remove)
            ReleaseThumbnailSource(kind);
        foreach (var kind in plan.Add)
            ArrangeThumbnailSource(kind, sourceSize);
        _thumbnailSourceWidth = sourceSize.Width;
    }
    /// <summary>ホストが健在で、そのペインを今も抱えているか。他の経路（タイル再構築・ワークスペース
    /// 切替など）でペインが外されていたら false ＝作り直す。差分の取りこぼしで空のミニチュアが
    /// 残らないための保険。</summary>
    private bool IsThumbnailSourceIntact(PaneKind kind)
        => _stageThumbnailHosts.TryGetValue(kind, out var host)
        && host.Parent is not null
        && host.Children.Count == 1
        && ReferenceEquals(host.Children[0], _paneElements[kind]);
    private void ReleaseThumbnailSource(PaneKind kind) {
        if (!_stageThumbnailHosts.Remove(kind, out var host))
            return;
        host.Children.Clear();   // ペインは親なしへ戻す（呼び出し側が別の場所へ据える）
        StageSourceArea.Children.Remove(host);
    }
    /// <summary>描画元を全部捨てる（モード切替・ワークスペース切替など、据え置きが成り立たない場面用）。</summary>
    private void ClearThumbnailSources() {
        foreach (var host in _stageThumbnailHosts.Values)
            host.Children.Clear();
        StageSourceArea.Children.Clear();
        _stageThumbnailHosts.Clear();
        _thumbnailSourceWidth = 0;
    }
    private void RebuildWings()
        => PaneLayoutDebugLog.Time("RebuildWings", RebuildWingsCore);
    private void RebuildWingsCore() {
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
            foreach (var kind in LayoutWingKinds())
                WingStrip.Children.Add(BuildLayoutWingCard(kind, WingCardWidth));
        }
    }
    private void BuildLayoutWingSources() {
        _layoutWingSourceWidth = StageSourceArea.ActualWidth;
        SyncThumbnailSources(
            LayoutWingKinds(), StageThumbnailPlanner.SourceSize(_layoutWingSourceWidth, CardAspect));
    }
    private void ScheduleLayoutWings() {
        if (_stageActive)
            return;
        if (_paneSplitterDragging) {
            PaneLayoutDebugLog.Log("ScheduleLayoutWings skipped: splitter drag in progress");
            return;
        }
        var hasWings = LayoutWingKinds().Count > 0;
        PaneLayoutDebugLog.Log($"ScheduleLayoutWings hasWings={hasWings} prevWingColumnWidth={WingColumn.Width}", withCaller: true);
        WingColumn.Width = hasWings ? new GridLength(WingColumnReserve) : GridLength.Auto;
        WingHost.Visibility = hasWings ? Visibility.Visible : Visibility.Collapsed;
        if (!hasWings) {
            _layoutWingBuildPending = false;
            WingStrip.Children.Clear();
            ClearThumbnailSources();
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
        // 描画元は固定仮想幅で頭打ちなので、そこを超える範囲のリサイズでは組み直さない
        // （以前はリサイズのたびに全ペインを実寸レイアウトし直していた）。
        if (_stageActive || e.NewSize.Width <= 0
            || !StageThumbnailPlanner.SourceSizeChanged(_layoutWingSourceWidth, e.NewSize.Width))
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
