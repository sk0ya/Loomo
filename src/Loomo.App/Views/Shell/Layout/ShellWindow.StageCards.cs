namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ソロモード（舞台＋袖＋俯瞰）のカード／ミニチュア描画。袖・俯瞰カードの描画元の アレンジ、ライブ縮小カード（VisualBrush）、舞台スロットの生成。モード制御は ShellWindow.Stage.cs。</summary>
public partial class ShellWindow {
    private const double OverviewCardWidth = 320;
    private const double CardAspect = 3.0 / 2.0;
    private double _layoutWingSourceWidth;
    private bool _layoutWingBuildQueued;
    private bool _layoutWingBuildPending;
    /// <summary>袖カードの列間の隙間（2列表示のとき）。</summary>
    private const double WingCardGap = 6;
    /// <summary>袖の候補（タブで絞る<b>前</b>）。有効だが Main に出ていないペイン。
    /// 「袖そのものを出すか」はこちらで判断する——選択中のタブが空でも、ほかのタブにカードが
    /// あるなら袖ごと畳んではいけない（畳むとタブに戻れなくなる）。</summary>
    private IReadOnlyList<PaneKind> WingCandidates()
        => StageCardPolicy.WingCandidates(
            StageModeCoordinator.StageOrder, _stageActive, OnStage, IsSessionEnabled, IsShownInMain);
    /// <summary>いま袖に並べるペイン（＝候補のうち選択中のタブぶん）。</summary>
    private IReadOnlyList<PaneKind> WingKinds()
        => WingCandidates().Where(InActiveWingTab).ToList();
    private StageThumbnailSourceController? _stageThumbnailSources;
    private StageThumbnailSourceController StageThumbnailSources
        => _stageThumbnailSources ??= new StageThumbnailSourceController(StageSourceArea, _paneElements);
    private StageCardPresenter? _stageCardPresenter;
    private StageCardPresenter StageCardPresentation
        => _stageCardPresenter ??= new StageCardPresenter(
            this, StageThumbnailSources, _paneElements, AttachActivityBadge,
            kind => { if (_stageActive) BeginStageDrag(kind); else BeginWingDrag(kind); });

    /// <summary>描画元を全部捨て、通常レイアウトへペインを戻す。</summary>
    private void ClearThumbnailSources() => StageThumbnailSources.Clear();
    private void RebuildWings() {
        try {
            PaneLayoutDebugLog.Time("RebuildWings", RebuildWingsCore);
        }
        catch (InvalidOperationException ex) when (StageCardPolicy.IsTreeWalkMutation(ex.Message)) {
            // ScrollViewer.OnLayoutUpdated／バインディング更新の再入中は、Dispatcher に送った処理でも
            // WPF が論理ツリーを歩いている場合がある。ここで落ちると Loomo 本体だけでなく、共有している
            // WebView2 のブラウザプロセスまで孤児化し、次回起動の WebView2 初期化を壊す。
            // 次のアイドル時へ戻して再試行する。既存のキューを使うので、リサイズ中も多重化しない。
            _layoutWingBuildPending = true;
            PaneLayoutDebugLog.Log($"RebuildWings: ツリーウォーク中のため再試行 ({ex.Message})");
            ScheduleLayoutWings();
        }
    }

    private void RebuildWingsCore() {
        PaneLayoutDebugLog.Log("RebuildWings()", withCaller: true);
        if (CollapseWingsForFullscreen())
            return;
        if (!_stageActive && (StageSourceArea.ActualWidth <= 0 || StageSourceArea.ActualHeight <= 0)) {
            ScheduleLayoutWings();
            return;
        }
        if (!_stageActive)
            _layoutWingBuildPending = false;
        WingStrip.Children.Clear();
        if (!_stageActive)
            BuildLayoutWingSources();
        var cardLayout = StageThumbnailPlanner.PlanWingCards(
            _settings.Appearance.WingColumns, _isWingCollapsed, _wingWidth,
            WingScrollViewer?.ViewportWidth ?? 0, WingCardGap);
        var width = cardLayout.CardWidth;
        var cards = WingKinds()
            .Select(kind => _stageActive
                ? BuildSessionCard(kind, width, isOverview: false, iconOnly: _isWingCollapsed)
                : BuildLayoutWingCard(kind, width, iconOnly: _isWingCollapsed))
            .ToList();
        foreach (var row in StageCardPresenter.ArrangeWingRows(cards, cardLayout.Columns, WingCardGap))
            WingStrip.Children.Add(row);
        UpdateWingTabs();
    }
    /// <summary>袖のタブ（メイン／サブ／すべて）の選択印を現在のタブへ合わせる。</summary>
    private void UpdateWingTabs() {
        if (WingMainTab is null || WingSubTab is null || WingAllTab is null)
            return;
        UpdateWingTabLabels();
        WingMainTab.IsChecked = _activeWingTab == WingTab.Main;
        WingSubTab.IsChecked = _activeWingTab == WingTab.Sub;
        WingAllTab.IsChecked = _activeWingTab == WingTab.All;
        UpdateWingToolbar();
    }
    /// <summary>袖が細いときだけラベルを1文字へ縮める。ツールチップは詳細なまま残す。</summary>
    private void UpdateWingTabLabels() {
        if (WingTabs is null || WingMainTab is null || WingSubTab is null || WingAllTab is null)
            return;
        var compact = WingTabs.ActualWidth > 0 && WingTabs.ActualWidth < 190;
        WingAllTab.Content = compact ? "全" : "すべて";
        WingMainTab.Content = compact ? "主" : "メイン";
        WingSubTab.Content = compact ? "他" : "サブ";
    }
    private void OnWingTabsSizeChanged(object sender, SizeChangedEventArgs e) => UpdateWingTabLabels();
    private void BuildLayoutWingSources() {
        _layoutWingSourceWidth = StageSourceArea.ActualWidth;
        StageThumbnailSources.Sync(
            WingKinds(), StageThumbnailPlanner.SourceSize(_layoutWingSourceWidth, CardAspect));
    }
    /// <summary>袖を出さない状況（F11 の全画面、および袖なしのドックモード）なら畳んで true。
    /// 舞台・通常どちらの再構築経路も最後に袖の幅／表示を組み直すので、そこで畳み直さないと
    /// <see cref="TogglePaneFullscreen"/> が 0 にした袖幅がそのまま元に戻ってしまう。
    /// カードは捨てずに据え置く（通常レイアウトの組み直しは ContextIdle 送りなので、捨てると
    /// 全画面を抜けた直後の数フレーム、幅だけ戻った空の袖が見える）。</summary>
    private bool CollapseWingsForFullscreen() {
        if (!_paneFullscreen && !_dockActive)
            return false;
        _layoutWingBuildPending = false;
        WingHost.Visibility = Visibility.Collapsed;
        WingSplitter.Visibility = Visibility.Collapsed;
        WingColumn.Width = new GridLength(0);
        WingSplitterColumn.Width = new GridLength(0);
        return true;
    }
    private void ScheduleLayoutWings() {
        if (_stageActive)
            return;
        if (CollapseWingsForFullscreen())
            return;
        if (_paneSplitterDragging) {
            PaneLayoutDebugLog.Log("ScheduleLayoutWings skipped: splitter drag in progress");
            return;
        }
        var hasWings = WingCandidates().Count > 0;   // 選択中のタブが空でも、もう一方に有れば袖は出す
        PaneLayoutDebugLog.Log($"ScheduleLayoutWings hasWings={hasWings} prevWingColumnWidth={WingColumn.Width}", withCaller: true);
        WingColumn.Width = hasWings ? new GridLength(EffectiveWingWidth) : GridLength.Auto;
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
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
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
        // 描画元の実効幅が変わる範囲だけ組み直す。最大仮想幅を超えるリサイズでは
        // 既存の描画元を再利用する。
        if (_stageActive || e.NewSize.Width <= 0
            || !StageThumbnailPlanner.SourceSizeChanged(_layoutWingSourceWidth, e.NewSize.Width))
            return;
        PaneLayoutDebugLog.Log($"OnStageSourceAreaSizeChanged {_layoutWingSourceWidth:0.#} -> {e.NewSize.Width:0.#}");
        ScheduleLayoutWings();
    }
    private void OnWingScrollChanged(object sender, ScrollChangedEventArgs e) {
        if (Math.Abs(e.ViewportWidthChange) <= 0.5
            || _paneSplitterDragging
            || WingHost.Visibility != Visibility.Visible)
            return;
        // ScrollViewer.OnLayoutUpdated から直接呼ばれるため、ここでペインの親を
        // 付け替えると WPF のツリーウォーク中に論理子を変更することになり、
        // 「現時点では、このノードの論理子を変更できません」で落ちる。
        // レイアウトが落ち着いてから既存の遅延再構築経路へ送る。
        ScheduleLayoutWings();
    }
    private void UpdateWingHostVisibility() {
        if (WingHost is null)
            return;
        if (CollapseWingsForFullscreen())
            return;
        // 選択中のタブが空でも、もう一方にカードがあるなら袖は出したままにする（畳むとタブへ戻れない）。
        var hasWings = WingCandidates().Count > 0;
        WingHost.Visibility = hasWings ? Visibility.Visible : Visibility.Collapsed;
        WingSplitter.Visibility = WingHost.Visibility;
        WingSplitterColumn.Width = WingHost.Visibility == Visibility.Visible ? new GridLength(6) : new GridLength(0);
        if (hasWings)
            WingColumn.Width = new GridLength(EffectiveWingWidth);
        OverviewButton.Visibility = _stageActive && !_isWingCollapsed ? Visibility.Visible : Visibility.Collapsed;
        UpdateWingToolbar();
    }
    private Border BuildSessionCard(PaneKind kind, double width, bool isOverview, bool iconOnly = false) {
        return StageCardPresentation.BuildSessionCard(
            kind, width, isOverview, iconOnly, isOverview && OnStage(kind),
            () => { SetStagePane(kind); FocusPane(kind); });
    }
    private Border BuildLayoutWingCard(PaneKind kind, double width, bool iconOnly = false) {
        return StageCardPresentation.BuildLayoutWingCard(kind, width, iconOnly, onStage: false, onClick: () => {
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
        var positioned = new List<PaneStagePosition>();
        foreach (var leaf in AllLeaves()) {
            if (leaf.Hidden || !TryGetPaneRect(leaf.Kind, out var rect))
                continue;
            positioned.Add(new PaneStagePosition(leaf.Kind, rect.X, rect.Y));
        }
        return StageCardPolicy.TopLeftPane(positioned, PaneLayoutTree.FirstVisibleLeaf(_root)?.Kind);
    }
    /// <summary>メインとサブの並べ方（設定）。Columns＝横に並べる（サブ＝右）、Rows＝縦に並べる（サブ＝下）。</summary>
    private SplitKind SubAxis()
        => _settings.PaneSubDirection == PaneSubDirection.Vertical ? SplitKind.Rows : SplitKind.Columns;
    /// <summary>メイン（左上の可視ペイン）と、設定の並べ方に沿ったサブ（横並び＝メインと同じ行の右端／
    /// 縦並び＝同じ列の下端）。サブがまだ無ければ <c>Sub</c> は null。</summary>
    private (PaneKind? Main, PaneKind? Sub) MainAndSubPanes()
    {
        var (main, sub) = PaneLayoutTree.MainAndSub(_root, SubAxis());
        return (main?.Kind ?? AllLeaves().FirstOrDefault(l => !l.Hidden)?.Kind, sub?.Kind);
    }


    /// <summary>
    /// WebView2 の現在表示をカード用 PNG として非同期取得する。連続更新は最新だけを採用し、
    /// 失敗時は前回画像を維持する。実 WebView のサイズ・親・Visibility は一切変更しない。
    /// </summary>
    private Task CaptureWebThumbnailAsync(PaneKind kind)
        => StageThumbnailSources.CaptureWebThumbnailAsync(kind, ResolveThumbnailWebCore);

    private CoreWebView2? ResolveThumbnailWebCore(PaneKind kind) => kind switch {
        PaneKind.EditorSupport => _editorSupport.WebView.Core,
        PaneKind.Browser => ActiveBrowserView.TryCore(),
        _ => null,
    };
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
