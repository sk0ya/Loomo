namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ソロモード（舞台＋袖＋俯瞰）のカード／ミニチュア描画。袖・俯瞰カードの描画元の アレンジ、ライブ縮小カード（VisualBrush）、舞台スロットの生成。モード制御は ShellWindow.Stage.cs。</summary>
public partial class ShellWindow {
    private const double OverviewCardWidth = 320;
    private const double CardAspect = 3.0 / 2.0;
    private const double WingRestOpacity = 0.90;
    private double _layoutWingSourceWidth;
    /// <summary>今の描画元ホストを組んだ仮想幅（0＝未構築）。変わらない限りホストは据え置ける。</summary>
    private double _thumbnailSourceWidth;
    private bool _layoutWingBuildQueued;
    private bool _layoutWingBuildPending;
    private double CurrentWingCardWidth => Math.Max(150, _wingWidth - 10);
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
        // WebView2CompositionControl はライブ VisualBrush の描画元へ移さない。内部の
        // GraphicsCaptureSession が強制 SizeChanged 中に CaptureFramePool.Recreate で落ちるため、
        // Browser / EditorSupport のカードは CapturePreviewAsync の静止画を使う。
        // 非表示化・親外しより先に現在の WPF 合成結果も写し、初回カードが空になるのを防ぐ。
        foreach (var kind in required.Where(kind =>
                     StageThumbnailPlanner.UsesSnapshotThumbnail(kind)
                     && SnapshotThumbnailNeedsSeed(kind)))
            CaptureComposedPaneThumbnail(kind);
        var liveRequired = required.Where(kind => !StageThumbnailPlanner.UsesSnapshotThumbnail(kind)).ToArray();
        var sizeChanged = StageThumbnailPlanner.SourceSizeChanged(_thumbnailSourceWidth, sourceSize.Width);
        var reusable = sizeChanged
            ? Array.Empty<PaneKind>()
            : _stageThumbnailHosts.Keys.Where(IsThumbnailSourceIntact).ToArray();
        var plan = StageThumbnailPlanner.PlanSources(
            _stageThumbnailHosts.Keys.ToArray(), reusable, liveRequired);
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
                WingStrip.Children.Add(BuildSessionCard(kind, CurrentWingCardWidth, isOverview: false));
        } else {
            BuildLayoutWingSources();
            foreach (var kind in LayoutWingKinds())
                WingStrip.Children.Add(BuildLayoutWingCard(kind, CurrentWingCardWidth));
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
        WingColumn.Width = hasWings ? new GridLength(_wingWidth) : GridLength.Auto;
        WingSplitterColumn.Width = hasWings ? new GridLength(6) : new GridLength(0);
        WingHost.Visibility = hasWings ? Visibility.Visible : Visibility.Collapsed;
        WingSplitter.Visibility = hasWings ? Visibility.Visible : Visibility.Collapsed;
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
        WingSplitter.Visibility = WingHost.Visibility;
        WingSplitterColumn.Width = WingHost.Visibility == Visibility.Visible ? new GridLength(6) : new GridLength(0);
        OverviewButton.Visibility = _stageActive ? Visibility.Visible : Visibility.Collapsed;
    }
    private Border BuildSessionCard(PaneKind kind, double width, bool isOverview) {
        if (StageThumbnailPlanner.UsesSnapshotThumbnail(kind))
            return BuildCard(kind, width, SnapshotThumbnailBrush(kind), isOverview,
                () => { SetStagePane(kind); FocusPane(kind); });
        Visual source = _stageThumbnailHosts.TryGetValue(kind, out var host) ? host : _paneElements[kind];
        return BuildCard(kind, width, VisualThumbnailBrush(source), isOverview,
            () => { SetStagePane(kind); FocusPane(kind); });
    }
    private Border BuildLayoutWingCard(PaneKind kind, double width) {
        var brush = StageThumbnailPlanner.UsesSnapshotThumbnail(kind)
            ? SnapshotThumbnailBrush(kind)
            : VisualThumbnailBrush(_stageThumbnailHosts.TryGetValue(kind, out var host) ? host : _paneElements[kind]);
        return BuildCard(kind, width, brush, isOverview: false, () => {
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
    private static Brush VisualThumbnailBrush(Visual source) {
        var sourceWidth = source is FrameworkElement sourceElement
            ? double.IsFinite(sourceElement.Width) && sourceElement.Width > 0
                ? sourceElement.Width
                : sourceElement.ActualWidth
            : 1;
        var sourceHeight = source is FrameworkElement sourceElement2
            ? double.IsFinite(sourceElement2.Height) && sourceElement2.Height > 0
                ? sourceElement2.Height
                : sourceElement2.ActualHeight
            : 1;
        return new VisualBrush(source) {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, Math.Max(sourceWidth, 1), Math.Max(sourceHeight, 1)),
            Stretch = Stretch.Uniform,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
    }
    private ImageBrush SnapshotThumbnailBrush(PaneKind kind) {
        if (_webThumbnailBrushes.TryGetValue(kind, out var existing))
            return existing;
        var brush = new ImageBrush {
            Stretch = Stretch.Uniform,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
        _webThumbnailBrushes[kind] = brush;
        return brush;
    }

    /// <summary>同期 RenderTargetBitmap は初回の空カード回避にだけ使う。いったん画像を得た後は
    /// WebView2 の非同期 CapturePreview 更新を再利用し、分割ドラッグ完了時の UI 停止を避ける。</summary>
    private bool SnapshotThumbnailNeedsSeed(PaneKind kind)
        => !_webThumbnailBrushes.TryGetValue(kind, out var brush) || brush.ImageSource is null;

    /// <summary>
    /// 現在画面へ合成済みのペインを即座にカード画像へ写す。WebView2 の API キャプチャは非同期で、
    /// 非表示化と競合すると失敗し得るため、これは初回表示を保証する同期フォールバック。
    /// 元要素を再ペアレントもリサイズもしない。
    /// </summary>
    private void CaptureComposedPaneThumbnail(PaneKind kind) {
        if (!_paneElements.TryGetValue(kind, out var element)
            || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return;
        try {
            const double maxWidth = StageThumbnailPlanner.VirtualWidth * 2;
            var scale = Math.Min(1, maxWidth / element.ActualWidth);
            var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * scale));
            var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * scale));
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen()) {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawRectangle(new VisualBrush(element), null,
                    new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            }
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            bitmap.Freeze();
            SnapshotThumbnailBrush(kind).ImageSource = bitmap;
        } catch {
            // 合成面の取得に失敗しても、前回画像または後続の CapturePreviewAsync を使用する。
        }
    }
    private Border BuildCard(PaneKind kind, double width, Brush thumbnail, bool isOverview, Action onClick) {
        var borderBrush = (Brush)FindResource("Border");
        var accent = (Brush)FindResource("Accent");
        var onStage = isOverview && OnStage(kind);
        var height = Math.Round(width / CardAspect);
        var card = new Border {
            Width = width, Height = height, Margin = isOverview ? new Thickness(10) : new Thickness(0, 4, 0, 4), CornerRadius = new CornerRadius(6), Background = (Brush)FindResource("Panel"), BorderBrush = onStage ? accent : borderBrush, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = PaneLabel(kind), Clip = new RectangleGeometry(new Rect(0, 0, width, height), 6, 6), };
        var root = new Grid { ClipToBounds = true };
        root.Children.Add(new Border {
            IsHitTestVisible = false, Background = thumbnail, });
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

    /// <summary>
    /// WebView2 の現在表示をカード用 PNG として非同期取得する。連続更新は最新だけを採用し、
    /// 失敗時は前回画像を維持する。実 WebView のサイズ・親・Visibility は一切変更しない。
    /// </summary>
    private async Task CaptureWebThumbnailAsync(PaneKind kind) {
        if (!StageThumbnailPlanner.UsesSnapshotThumbnail(kind))
            return;
        var sequence = _webThumbnailCaptureSequences.GetValueOrDefault(kind) + 1;
        _webThumbnailCaptureSequences[kind] = sequence;
        // NavigationCompleted 直後や本文差し替え直後の最終描画フレームを待つ。
        await Task.Delay(80);
        if (_webThumbnailCaptureSequences.GetValueOrDefault(kind) != sequence)
            return;
        var core = kind switch {
            PaneKind.EditorSupport => _editorSupport.WebView.View?.CoreWebView2,
            PaneKind.Browser => ActiveBrowserView?.CoreWebView2,
            _ => null,
        };
        if (core is null)
            return;
        try {
            using var stream = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            if (_webThumbnailCaptureSequences.GetValueOrDefault(kind) != sequence)
                return;
            stream.Position = 0;
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            SnapshotThumbnailBrush(kind).ImageSource = image;
        } catch {
            // 非表示化・ナビゲーション競合時は取得できないことがある。前回画像をそのまま使う。
        }
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
