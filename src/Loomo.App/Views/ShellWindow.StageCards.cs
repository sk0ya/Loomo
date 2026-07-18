
namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: ソロモード（舞台＋袖＋俯瞰）のカード／ミニチュア描画。袖・俯瞰カードの描画元の
/// アレンジ、ライブ縮小カード（VisualBrush）、舞台スロットの生成。モード制御は ShellWindow.Stage.cs。</summary>
public partial class ShellWindow
{
    // 袖カードの幅。高さは CardAspect（固定）から導出される。
    private const double WingCardWidth = 180;
    // 俯瞰カードの幅。
    private const double OverviewCardWidth = 320;
    // カード枠の固定縦横比（幅÷高さ）。サイドバー幅でペインの縦横比が変わっても枠は揺れない。 中身は枠へ Uniform で歪ませず・クロップせず収める（描画元の縦横比が変わっても揺れない）。
    private const double CardAspect = 3.0 / 2.0;
    // 袖カードの待機時不透明度（暗がりで生きて待っている感を出す）。
    private const double WingRestOpacity = 0.72;
    private double _layoutWingSourceWidth;
    private bool _layoutWingBuildQueued;
    private bool _layoutWingBuildPending;

    // 描画元ペインをカードと同じ固定縦横比（CardAspect）でレイアウトした非表示ホストを 作り、StageSourceArea に登録する。ミニチュア（VisualBrush）はこのホストを縮小描画する。両モード共通。 幅は実領域（virtualSize）に合わせて中身の縮尺を保ち、高さだけ枠の比率へ寄せるので、 カードへ Uniform で収めても余白もはみ出しも出ず、サイドバー幅で揺れない。
    private void ArrangeThumbnailSource(PaneKind kind, Size virtualSize)
    {
        var element = _paneElements[kind];
        if (element.Parent is Panel parent)
            parent.Children.Remove(element);
        element.Visibility = Visibility.Visible;

        var w = Math.Max(virtualSize.Width, 1);
        var h = Math.Max(w / CardAspect, 1);
        var host = new Grid
        {
            Width = w,
            Height = h,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Clip = new RectangleGeometry(new Rect(0, 0, w, h)),
        };
        host.Children.Add(element);
        StageSourceArea.Children.Add(host);

        // WebView2CompositionControl は親を付け替えただけでは composition surface が
        // 以前のタイル寸法を保持するため、新しい描画元寸法をここで確定させる。
        var sourceSize = new Size(w, h);
        host.Measure(sourceSize);
        host.Arrange(new Rect(sourceSize));
        host.UpdateLayout();

        _stageThumbnailHosts[kind] = host;
    }

    // 袖・俯瞰カードの描画元（実体ペイン）を舞台サイズでレイアウトする（ソロモード専用）。
    private void BuildStageThumbnailSources(Size virtualSize)
    {
        var kinds = _overviewActive
            ? OverviewKinds()
            : StageOrder.Where(k => !OnStage(k) && IsSessionEnabled(k));
        foreach (var kind in kinds)
            ArrangeThumbnailSource(kind, virtualSize);
    }

    // 袖（ミニチュア）を組み直す。両モードとも「有効だが Main に出ていない」セッションを 実体ペインのライブ縮小で並べる（Main に出ているもの・無効なものは出さない）。 ソロは舞台外の有効セッション、レイアウトはタイル未配置やズーム中の非ズームペインが対象。
    private void RebuildWings()
    {
        PaneLayoutDebugLog.Log("RebuildWings()", withCaller: true);
        // レイアウトモードの初回は、描画元の実寸が確定するまで何も作らない。
        // フォールバック寸法で仮構築して SizeChanged 後に作り直す二段描画を避ける。
        if (!_stageActive && (StageSourceArea.ActualWidth <= 0 || StageSourceArea.ActualHeight <= 0))
        {
            ScheduleLayoutWings();
            return;
        }
        if (!_stageActive)
            _layoutWingBuildPending = false;

        WingStrip.Children.Clear();
        if (_stageActive)
        {
            foreach (var kind in StageOrder.Where(k => !OnStage(k) && IsSessionEnabled(k)))
                WingStrip.Children.Add(BuildSessionCard(kind, WingCardWidth, isOverview: false));
        }
        else
        {
            BuildLayoutWingSources();
            foreach (var kind in StageOrder.Where(k => IsSessionEnabled(k) && !IsShownInMain(k)))
                WingStrip.Children.Add(BuildLayoutWingCard(kind, WingCardWidth));
        }
    }

    // レイアウトモードの袖カードの描画元を組む：有効だが Main に出ていないペイン （タイル未配置・ズーム中の非ズームペイン等）を Main 領域サイズの非表示ホスト（StageSourceArea）へ 寄せてレイアウトする。これらは PaneHost に居ないため、ライブ縮小の描画元として別途アレンジが要る。
    private void BuildLayoutWingSources()
    {
        StageSourceArea.Children.Clear();
        _stageThumbnailHosts.Clear();

        // PaneHost は袖列までまたぐため、その幅を使うと Grid.Column=2 の StageSourceArea から
        // はみ出した分がクリップされ、VisualBrush に透明な余白として現れる。
        // 描画元を実際に置く StageSourceArea の内寸を使う。
        var virtualSize = new Size(StageSourceArea.ActualWidth, StageSourceArea.ActualHeight);
        _layoutWingSourceWidth = virtualSize.Width;

        foreach (var kind in StageOrder.Where(k => IsSessionEnabled(k) && !IsShownInMain(k)))
            ArrangeThumbnailSource(kind, virtualSize);
    }

    // レイアウトモードで袖を組み直す。実体ペインはタイルに在るので、レイアウト確定後（Loaded）に ライブ要素の実寸でカードを作る。
    private void ScheduleLayoutWings()
    {
        if (_stageActive)
            return;
        // ドラッグ中は組み直さない：RebuildWings の強制 UpdateLayout がドラッグ中の GridSplitter の
        // マウスキャプチャを奪い、ドラッグが分断されて離した瞬間の幅が実質ランダムになってしまう
        // （DragCompleted 側で改めて ScheduleLayoutWings を呼ぶので、ここで取りこぼしても後で追いつく）。
        if (_paneSplitterDragging)
        {
            PaneLayoutDebugLog.Log("ScheduleLayoutWings skipped: splitter drag in progress");
            return;
        }

        // Auto 列を空のまま測ってからカード追加で広げると、Main が縮んで二度目の
        // SizeChanged が発生する。描画前に袖の最終幅を予約してレイアウトを一度で確定する。
        var hasWings = StageOrder.Any(k => IsSessionEnabled(k) && !IsShownInMain(k));
        PaneLayoutDebugLog.Log($"ScheduleLayoutWings hasWings={hasWings} prevWingColumnWidth={WingColumn.Width}", withCaller: true);
        WingColumn.Width = hasWings ? new GridLength(WingColumnReserve) : GridLength.Auto;
        WingHost.Visibility = hasWings ? Visibility.Visible : Visibility.Collapsed;
        if (!hasWings)
        {
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
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _layoutWingBuildQueued = false;
            // ドラッグ中は RebuildWings の強制 UpdateLayout がスプリッターのマウスキャプチャを奪い、
            // GridSplitter がリサイズを取り消して幅が元へ戻ってしまう。入口のガード（上）だけでは、
            // ドラッグ開始直前にキューへ積まれたこのコールバックがドラッグ中の ContextIdle で発火する
            // 取りこぼしが残るため、ここでも弾く。_layoutWingBuildPending は残すので DragCompleted 側の
            // ScheduleLayoutWings で必ず組み直される。
            if (_paneSplitterDragging)
                return;
            if (_stageActive || !_layoutWingBuildPending)
                return;
            // まだ Measure/Arrange 前なら SizeChanged が次の一度を予約する。
            if (StageSourceArea.ActualWidth <= 0 || StageSourceArea.ActualHeight <= 0)
                return;
            _layoutWingBuildPending = false;
            PaneLayoutDebugLog.Log("ScheduleLayoutWings deferred callback -> RebuildWings()");
            RebuildWings();
            UpdateWingHostVisibility();
        }));
    }

    private void OnStageSourceAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_stageActive || e.NewSize.Width <= 0
            || Math.Abs(e.NewSize.Width - _layoutWingSourceWidth) <= 1)
            return;
        PaneLayoutDebugLog.Log($"OnStageSourceAreaSizeChanged {_layoutWingSourceWidth:0.#} -> {e.NewSize.Width:0.#}");
        ScheduleLayoutWings();
    }

    // 袖の列（WingHost）の表示と、俯瞰ボタン（ソロ専用）の表示を現状へ同期する。
    private void UpdateWingHostVisibility()
    {
        if (WingHost is null)
            return;
        WingHost.Visibility = WingStrip.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OverviewButton.Visibility = _stageActive ? Visibility.Visible : Visibility.Collapsed;
    }

    // ソロ／俯瞰のカード：舞台サイズにレイアウトした非表示ホスト（StageSourceArea）を縮小描画する。 クリックでそのセッションを舞台へ立てる。
    private Border BuildSessionCard(PaneKind kind, double width, bool isOverview)
    {
        Visual source = _stageThumbnailHosts.TryGetValue(kind, out var host) ? host : _paneElements[kind];
        return BuildCard(kind, width, source, isOverview,
            () => { SetStagePane(kind); FocusPane(kind); });
    }

    // レイアウトモードの袖カード：Main 領域サイズへ寄せた非表示ホスト（BuildLayoutWingSources） をライブ縮小で描画する。クリックでそのペインを左上ペインと入れ替える（クリックしたセッションが左上の 位置を引き継ぎ、元の左上ペインは袖へ退場）。ズーム中は対象をズームへ昇格。
    private Border BuildLayoutWingCard(PaneKind kind, double width)
    {
        Visual source = _stageThumbnailHosts.TryGetValue(kind, out var host) ? host : _paneElements[kind];
        return BuildCard(kind, width, source, isOverview: false,
            () =>
            {
                if (_zoomedPane is not null)
                {
                    if (IsPaneVisible(kind))
                        ZoomPane(kind);   // ズーム中の袖カード＝そのペインを舞台（ズーム）へ昇格
                    return;
                }
                // 既に画面に出ているならレイアウトの入れ替えはせず、フォーカスだけ移す（全モード共通）。
                if (IsPaneVisible(kind))
                {
                    FocusPane(kind);
                    return;
                }
                // 非表示のミニチュアのクリックは設定 PaneOpenBehavior に従って前面へ配置する
                // （main＝左上と入れ替え〔従来〕／sub＝右上と入れ替え／loop＝サブ表示・サブ起点ならメインへ繰り上げ）。
                PlacePaneByBehavior(kind);
                FocusPane(kind);
            });
    }

    // タイル上で最も左上（上端優先・次に左端）に表示されている可視ペイン。袖クリックの入れ替え先。 まだレイアウトされておらず矩形が取れないときはツリー順の最初の可視リーフへフォールバックする。
    private PaneKind? TopLeftPane()
    {
        PaneKind? best = null;
        Rect bestRect = default;
        foreach (var leaf in AllLeaves())
        {
            if (leaf.Hidden || !TryGetPaneRect(leaf.Kind, out var rect))
                continue;
            if (best is null
                || rect.Y < bestRect.Y - 0.5
                || (Math.Abs(rect.Y - bestRect.Y) <= 0.5 && rect.X < bestRect.X))
            {
                best = leaf.Kind;
                bestRect = rect;
            }
        }
        return best ?? AllLeaves().FirstOrDefault(l => !l.Hidden)?.Kind;
    }

    // タイル上段（最上端の行）で最も右にある可視ペイン。サブ（右上）ペインの入れ替え先。 上段が横1枚ならメイン（左上）と一致する。ジオメトリ（矩形）に依存すると、レイアウト直後で 矩形が未確定のとき下段のペインを誤って返し得るため、ツリー構造から決定的に求める （PaneLayoutTree.TopRow＋PaneLayoutTree.RightmostVisibleLeaf）。
    private PaneKind? TopRightPane()
        => PaneLayoutTree.RightmostVisibleLeaf(PaneLayoutTree.TopRow(_root))?.Kind
            ?? AllLeaves().FirstOrDefault(l => !l.Hidden)?.Kind;

    // タイル上段（最上端の行）で最も左にある可視ペイン＝サブ判定でのメイン（左上）。 TopRightPane と対で、どちらも上段ノードから構造的に求めるので判定がぶれない。
    private PaneKind? TopRowLeftPane()
        => PaneLayoutTree.LeftmostVisibleLeaf(PaneLayoutTree.TopRow(_root))?.Kind;

    // セッションカードの共通部分を作る。カード枠は固定縦横比（CardAspect）。描画元ホストも 同じ比率（ArrangeThumbnailSource）なので、VisualBrush の source を Uniform で収めると歪み・余白・クロップなく枠いっぱいに埋まる（縦横比が一致するので揺れない）。
    private Border BuildCard(PaneKind kind, double width, Visual source, bool isOverview, Action onClick)
    {
        var borderBrush = (Brush)FindResource("Border");
        var accent = (Brush)FindResource("Accent");
        var onStage = isOverview && OnStage(kind);

        var height = Math.Round(width / CardAspect);

        var card = new Border
        {
            Width = width,
            Height = height,
            Margin = isOverview ? new Thickness(10) : new Thickness(0, 4, 0, 4),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("Panel"),
            BorderBrush = onStage ? accent : borderBrush,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = PaneLabel(kind),
            Clip = new RectangleGeometry(new Rect(0, 0, width, height), 6, 6),
        };

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

        root.Children.Add(new Border
        {
            IsHitTestVisible = false,
            // 自動 Viewbox は透明な非表示ホストの描画境界を使うため、実際のレイアウト範囲より
            // 狭い領域を拾って余白が生じる。ホスト全体を絶対座標で指定してカードへ収める。
            Background = new VisualBrush(source)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, Math.Max(sourceWidth, 1), Math.Max(sourceHeight, 1)),
                Stretch = Stretch.Uniform,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            },
        });

        // 名札（下端のバー）
        root.Children.Add(new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromArgb(0xB4, 0x10, 0x10, 0x10)),
            Child = new TextBlock
            {
                Text = PaneLabel(kind),
                FontSize = UiFontManager.Scaled(isOverview ? 12 : 11),
                Margin = new Thickness(8, 3, 8, 3),
                Foreground = Brushes.White,
            },
        });
        card.Child = root;

        // ターミナルカードには活動バッジ（実行中／未確認の成功・失敗）を重ねる（§袖=周辺視野）。
        AttachActivityBadge(root, kind, isOverview);

        var rest = isOverview ? 1.0 : WingRestOpacity;
        card.Opacity = rest;

        // ホバー：手前へ浮き上がって明るくなる（袖の奥行き感）
        card.MouseEnter += (_, _) =>
        {
            card.BorderBrush = accent;
            card.Opacity = 1;
        };
        card.MouseLeave += (_, _) =>
        {
            card.BorderBrush = onStage ? accent : borderBrush;
            card.Opacity = rest;
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            _wingDragArmed = false;
            e.Handled = true; // 俯瞰レイヤの背景クリック（＝俯瞰を閉じる）と区別する
            onClick();
        };

        // ミニチュアはドラッグして配置できる。しきい値を超えたら BeginWingDrag／BeginStageDrag が
        // オーバーレイへ捕捉を移し、このクリック（=onClick）は不発になる。
        //   レイアウト：タイルへドロップ（中央=入れ替え／端=分割挿入）。
        //   ソロ：舞台へドロップ（中央=舞台を入れ替え／端=レイアウトモードへ切り替えて分割挿入）。
        // 俯瞰カードはダイブ専用なのでドラッグしない。
        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _wingDragStart = e.GetPosition(this);
            _wingDragArmed = true;
        };
        card.PreviewMouseMove += (_, e) =>
        {
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

    // 実体ペインを角丸枠に入れた舞台スロットを作る。ペイン自身のタイトルバーがあるので余計なヘッダは足さない。
    private Border BuildLiveSlot(PaneKind kind)
    {
        var element = _paneElements[kind];
        element.Visibility = Visibility.Visible;

        var host = new Grid();
        host.SizeChanged += (_, e) => host.Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);
        host.Children.Add(element);

        return new Border
        {
            Background = (Brush)FindResource("Panel"),
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = host,
        };
    }
}
