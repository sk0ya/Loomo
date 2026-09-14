using Ellipse = System.Windows.Shapes.Ellipse;
using Path = System.Windows.Shapes.Path;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ドックモード（袖なし＝IDE 風ツールウィンドウ）。中央は<b>タイル配置のまま</b>で、
/// 下／右の領域へ割り当てたペインだけがタイルから抜け、右端の帯のアイコンで開閉する（アイコンがタブを兼ねる）。
/// 集中モードと同じく<b>表示の差し替えだけ</b>で切替わり、レイアウトツリー（<c>_root</c>）には触れない——
/// 分割モードへ戻せば元のタイル配置・比率がそのまま戻る。状態は <see cref="DockLayoutCoordinator"/>。</summary>
public partial class ShellWindow {
    private readonly DockLayoutCoordinator _dockMode = new();
    private bool _dockActive => _dockMode.Active;
    /// <summary>右帯の幅。ActivityBar と同じ 48px（対になる帯なので幅を揃える）。</summary>
    private const double DockBarWidth = 48;
    private const double DockSplitterThickness = 6;
    /// <summary>中央の枠の下限（XAML の既定値と揃える）。中央を畳むときだけ 0 に落として、
    /// 空いた場所を残った面へ渡す。</summary>
    private const double CenterMinWidth = 160;
    private const double CenterMinHeight = 80;
    /// <summary>帯のアイコンに付く活動の点（袖のバッジのドックモード版・§24.1）。</summary>
    private readonly Dictionary<PaneKind, Ellipse> _dockBarBadges = new();

    private void InitializeDock() {
        DockBottomSplitter.MouseDoubleClick += (_, e) => {
            _dockMode.SetBottomHeight(DockLayoutCoordinator.DefaultBottomHeight);
            ApplyDockTracks();
            SaveActiveWorkspaceSnapshot();
            e.Handled = true;
        };
        DockBottomSplitter.DragStarted += (_, _) => _paneSplitterDragging = true;
        DockBottomSplitter.DragDelta += (_, e) => {
            _dockMode.SetBottomHeight(_dockMode.BottomHeight - e.VerticalChange);
            DockBottomRow.Height = new GridLength(_dockMode.BottomHeight);
        };
        DockBottomSplitter.DragCompleted += (_, _) => {
            _paneSplitterDragging = false;
            SaveActiveWorkspaceSnapshot();
        };
        DockRightSplitter.MouseDoubleClick += (_, e) => {
            _dockMode.SetRightWidth(DockLayoutCoordinator.DefaultRightWidth);
            ApplyDockTracks();
            SaveActiveWorkspaceSnapshot();
            e.Handled = true;
        };
        DockRightSplitter.DragStarted += (_, _) => _paneSplitterDragging = true;
        DockRightSplitter.DragDelta += (_, e) => {
            _dockMode.SetRightWidth(_dockMode.RightWidth - e.HorizontalChange);
            DockRightColumn.Width = new GridLength(_dockMode.RightWidth);
        };
        DockRightSplitter.DragCompleted += (_, _) => {
            _paneSplitterDragging = false;
            SaveActiveWorkspaceSnapshot();
        };
    }

    // ===== モード出入り =====

    private void OnChooseDockMode(object sender, RoutedEventArgs e) {
        if (!_dockActive) {
            BeginTrailLayoutChange();
            EnterDockMode();
        }
        RefreshOpenPaneMenu();
    }
    private void EnterDockMode() {
        if (_stageActive)
            ExitStageMode();      // 舞台を畳んでタイルへ戻してから、そのタイルを中央に据える
        if (!_dockMode.Enter())
            return;
        _zoomedPane = null;
        _dockMode.DropInapplicable(IsPaneApplicable);
        CollapseWingsForFullscreen();   // 袖とドックの領域は同時に成り立たない（§26.3.1）
        DockBar.Visibility = Visibility.Visible;
        DockBarColumn.Width = new GridLength(DockBarWidth);
        UpdateModeButtons();
        RebuildPaneLayout();      // 中央は1枚に、下／右へ割り当てた面はそれぞれの領域へ
        RebuildDock();
        if (_dockMode.CenterPane is { } center)
            FocusPane(center);
        SaveActiveWorkspaceSnapshot();
    }
    private void ExitDockMode() {
        if (!_dockMode.Exit())
            return;
        ClearDockHosts();
        UpdateModeButtons();
        RebuildPaneLayout();      // ドックへ出していたペインがタイルへ戻る
        SaveActiveWorkspaceSnapshot();
    }
    /// <summary>ワークスペース切替のための後始末（保存も再構築もしない）。</summary>
    private void ClearDockModeForWorkspaceSwitch() {
        if (!_dockMode.Exit())
            return;
        ClearDockHosts();
        UpdateModeButtons();
    }
    private void ClearDockHosts() {
        // 中央の枠はドック以外（分割・集中・全画面）も使う共有の器なので、必ず元へ戻す。
        PaneHost.Visibility = Visibility.Visible;
        CenterRow.Height = new GridLength(1, GridUnitType.Star);
        CenterRow.MinHeight = CenterMinHeight;
        CenterColumn.Width = new GridLength(1, GridUnitType.Star);
        CenterColumn.MinWidth = CenterMinWidth;
        DockBottomHost.Children.Clear();
        DockRightHost.Children.Clear();
        DockBarRightZone.Children.Clear();
        DockBarCenterZone.Children.Clear();
        DockBarBottomZone.Children.Clear();
        _dockBarBadges.Clear();
        DockBottomHost.Visibility = Visibility.Collapsed;
        DockRightHost.Visibility = Visibility.Collapsed;
        DockBottomSplitter.Visibility = Visibility.Collapsed;
        DockRightSplitter.Visibility = Visibility.Collapsed;
        DockBar.Visibility = Visibility.Collapsed;
        DockBottomRow.Height = new GridLength(0);
        DockBottomSplitterRow.Height = new GridLength(0);
        DockRightColumn.Width = new GridLength(0);
        DockRightSplitterColumn.Width = new GridLength(0);
        DockBarColumn.Width = new GridLength(0);
    }

    // ===== 復元・保存 =====

    /// <summary>ワークスペース復元の前半：状態だけ入れて器を出す（中身は
    /// <see cref="CompleteDockSnapshotRestore"/> で組む）。集中モードの
    /// <c>PrepareStageSnapshot</c> と同じ二段構え。</summary>
    private void PrepareDockSnapshot(bool dock, DockSnapshot? snapshot) {
        ClearDockModeForWorkspaceSwitch();
        _dockMode.Restore(
            active: dock,
            regions: snapshot?.Placements?.Select(p =>
                new KeyValuePair<PaneKind, DockRegion>(p.Kind, p.Region)),
            centerPane: snapshot?.CenterPane,
            centerClosed: snapshot?.CenterClosed ?? false,
            bottomPane: snapshot?.BottomPane,
            rightPane: snapshot?.RightPane,
            bottomHeight: snapshot?.BottomHeight,
            rightWidth: snapshot?.RightWidth);
        if (!dock)
            return;
        _dockMode.DropInapplicable(IsPaneApplicable);
        CollapseWingsForFullscreen();   // 分割の部屋から切り替えたときに袖が残らないように
        DockBar.Visibility = Visibility.Visible;
        DockBarColumn.Width = new GridLength(DockBarWidth);
        UpdateModeButtons();
    }
    private void CompleteDockSnapshotRestore() {
        if (!_dockActive)
            return;
        RebuildDock();
        if (_dockMode.IsOpen(PaneKind.EditorSupport))
            InvalidateEditorSupport();
    }
    private DockSnapshot CaptureDockSnapshot() => new() {
        Placements = _dockMode.ChangedRegions()
            .Select(pair => new DockPlacementSnapshot { Kind = pair.Key, Region = pair.Value })
            .ToList(),
        CenterPane = _dockMode.CenterPane,
        CenterClosed = _dockMode.CenterClosed,
        BottomPane = _dockMode.BottomPane,
        RightPane = _dockMode.RightPane,
        BottomHeight = _dockMode.BottomHeight,
        RightWidth = _dockMode.RightWidth,
    };

    // ===== 問い合わせ =====

    /// <summary>ビュー・スイッチャーとコマンドパレットに並べる面。ドック中は帯に出せる面だけ
    /// （Diff などドックに置かない面を並べると、押しても何も出ない行になる）。</summary>
    private IEnumerable<PaneKind> PaneOrderForMode()
        => _dockActive ? StageOrder.Where(DockLayoutCoordinator.IsDockable) : StageOrder;
    /// <summary>ドックモードで、そのペインがいま見えているか。3領域とも「出ている1枚か」で決まる。</summary>
    private bool IsDockPaneShown(PaneKind kind) => _dockMode.IsOpen(kind);
    /// <summary>実体を作って動かしておくべきか（＝画面のどこかに出ている）。集中モードは袖でも
    /// 生きたまま縮小表示するので常に true。</summary>
    private bool IsPaneMaterialized(PaneKind kind)
        => _stageActive || (_dockActive ? IsDockPaneShown(kind) : IsPaneVisible(kind));
    /// <summary>ドックの領域に出したままにするペイン（＝親から外してはいけないもの）。中央→下→右の順。</summary>
    private IReadOnlyCollection<PaneKind> OpenDockPanes() => _dockMode.OpenPanes().ToList();
    /// <summary>全画面・ズームのように「画面に出ている1枚」を要る操作の対象を、ドック中は
    /// <b>実際に出ている面</b>へ寄せる。畳んである面（閉じた中央を含む）を選ぶと、親も
    /// 表示も無い要素を全画面にしようとして落ちるだけなので、出ている面が1つも無ければ null。</summary>
    private PaneKind? DockShownPane(PaneKind? preferred)
        => preferred is { } kind && _dockMode.IsOpen(kind)
            ? kind
            : _dockMode.OpenPanes().Cast<PaneKind?>().FirstOrDefault();
    /// <summary>ペインを現在の親に据え置ける集合（袖＝集中／分割、ドックの領域＝ドック）。
    /// 全画面（F11）中はどちらも畳んで対象1枚だけを出すので、据え置きは無し。</summary>
    private IReadOnlyCollection<PaneKind> PanesToKeepAttached()
        => _paneFullscreen ? Array.Empty<PaneKind>()
        : _dockActive ? OpenDockPanes()
        : (IReadOnlyCollection<PaneKind>)WingKinds();
    /// <summary>軌跡の配置キーに混ぜるドックの状態（開いている面）。中央のタイルが同じでも
    /// 道具の開閉で見え方は変わるので、これを入れないとドック中の操作が軌跡に残らない。</summary>
    private string? CurrentDockKey()
        => _dockActive
            ? $"{_dockMode.CenterPane?.ToString() ?? "-"}+{_dockMode.BottomPane?.ToString() ?? "-"}"
              + $"+{_dockMode.RightPane?.ToString() ?? "-"}"
            : null;
    /// <summary>F11 の全画面へ入る前に、ドックの器だけ畳む（割り当てと開いている面は保つ）。</summary>
    private void CollapseDockForFullscreen() {
        if (_dockActive)
            ClearDockHosts();
    }
    /// <summary>全画面から戻ったら、畳んだ器を組み直す（開いていた面がそのまま戻る）。</summary>
    private void RestoreDockAfterFullscreen() {
        if (!_dockActive)
            return;
        DockBar.Visibility = Visibility.Visible;
        DockBarColumn.Width = new GridLength(DockBarWidth);
        RebuildDock();
    }

    // ===== 開閉 =====

    /// <summary>帯のアイコン。出ていれば畳み、そうでなければ出して前面へ。3領域とも同じで、
    /// 開閉のどちらも <see cref="TryCloseDockPane"/>／<see cref="OpenDockPane"/> に通す
    /// ——閉じる側をここで直接書くと、フォーカスの受け渡しがこの1経路だけ抜ける。</summary>
    private void ToggleDockPane(PaneKind kind) {
        if (!_dockActive || TryCloseDockPane(kind))
            return;
        BeginTrailLayoutChange();
        OpenDockPane(kind, focus: true);
    }
    /// <summary>そのペインをその領域の1枚として出す（中央・下・右で扱いは同じ）。</summary>
    private void EnsureDockPaneShown(PaneKind kind) => OpenDockPane(kind, focus: false);
    /// <summary>ドックの領域にその1枚を立てる。<paramref name="focus"/> は
    /// <c>FocusPane</c> から呼ばれる場合は false——向こうがこの後フォーカスを当てるので、
    /// ここで当てると同じ経路を二度回る。</summary>
    private bool OpenDockPane(PaneKind kind, bool focus) {
        if (!_dockActive || !_dockMode.Open(kind))
            return false;
        RebuildDock();
        MarkPaneActivitySeen(kind);   // 出した＝目に入ったので未確認バッジを流す
        if (kind == PaneKind.EditorSupport)
            InvalidateEditorSupport();
        if (focus)
            FocusPane(kind);
        SaveActiveWorkspaceSnapshot();
        return true;
    }
    /// <summary>ペインヘッダーの「—」等からの非表示要求をドックの畳みに読み替える。受けたら true。
    /// 3領域とも同じで、閉じたぶんはその場が空くだけ。中央だけ次の面へ差し替えていたが、
    /// それでは閉じる操作が切り替える操作になり、押した面はいつまでも消えない。</summary>
    private bool TryCloseDockPane(PaneKind kind) {
        if (!_dockActive || !_dockMode.IsOpen(kind))
            return false;
        BeginTrailLayoutChange();
        var wasFocused = _focusedRegion?.Pane == kind;
        _dockMode.Close(_dockMode.RegionOf(kind));
        RebuildDock();
        if (wasFocused)
            HandOffFocusFromClosedDockPane();
        SaveActiveWorkspaceSnapshot();
        return true;
    }
    /// <summary>畳んだ面に「現在地」を残さない。残すと、パレットを閉じた・ズームを解いた拍子の
    /// 「元居た場所へ戻す」（<c>FocusPane</c>）が畳んだはずの面を開き直し、F11 は画面に居ない
    /// 要素を全画面にしようとして落ちる。渡す先は残って見えている面（中央→下→右）で、
    /// 1つも無ければ現在地ごと捨てる。</summary>
    private void HandOffFocusFromClosedDockPane() {
        if (_dockMode.OpenPanes().Cast<PaneKind?>().FirstOrDefault() is { } next)
            FocusPane(next);
        else
            _focusedRegion = null;
    }
    /// <summary>中央に置かれた面のうち <paramref name="current"/> の次のもの（<paramref name="direction"/>＝±1）。
    /// 中央に他の面が無ければ null。</summary>
    private PaneKind? NextCenterPane(PaneKind current, int direction = 1) {
        var panes = _dockMode.PanesIn(DockRegion.Center).Where(IsPaneApplicable).ToList();
        if (panes.Count <= 1)
            return null;
        var index = panes.IndexOf(current);
        if (index < 0)
            return panes[0];
        return panes[((index + direction) % panes.Count + panes.Count) % panes.Count];
    }
    /// <summary>ドックモードの Ctrl+T 相当：中央に立てる面を順に切り替える
    /// （中央を畳んであるなら、まず中央の先頭の面を立て直す）。</summary>
    private void CycleDockCenter(int direction) {
        var next = _dockMode.CenterPane is { } current
            ? NextCenterPane(current, direction)
            : _dockMode.PanesIn(DockRegion.Center).Where(IsPaneApplicable).Cast<PaneKind?>().FirstOrDefault();
        if (next is { } target)
            ToggleDockPane(target);   // 出す側に回るので、フォーカスもそこで当たる
    }
    /// <summary>ペインの住む領域を変える（帯とビュー・スイッチャーの右クリックから）。</summary>
    private void PlaceDockPane(PaneKind kind, DockRegion region) {
        if (!_dockActive || _dockMode.RegionOf(kind) == region)
            return;
        BeginTrailLayoutChange();   // 変える *前* の配置キーを捕まえないと軌跡に点が残らない
        _dockMode.Place(kind, region);
        _dockMode.EnsureCenterPane(IsPaneApplicable);   // 中央から出て行ったら次の面を立てる
        RebuildDock();
        FocusPane(kind);
        SaveActiveWorkspaceSnapshot();
    }

    // ===== 組み立て =====

    private void RebuildDock() {
        if (!_dockActive)
            return;
        PaneLayoutDebugLog.Time("RebuildDock", RebuildDockCore);
        UpdateEditorSupportFileWatch();   // 見え方が変わった＝自動リロード監視の張り替え時（§24.8）
        SyncEditorSupportRenderability(); // 同じ理由で、描けずに持ち越した要求を拾い直す時でもある
    }
    private void RebuildDockCore() {
        _dockMode.EnsureCenterPane(IsPaneApplicable);
        ApplyDockCenter();
        ApplyDockRegion(DockRegion.Bottom, DockBottomHost);
        ApplyDockRegion(DockRegion.Right, DockRightHost);
        ApplyDockTracks();
        // 帯の印だけでなくヘッダー（モード名・中央の面）も合わせる。
        // UpdatePaneToggleStates が中で RebuildDockBar を呼ぶので、帯はここで二度組まない。
        UpdatePaneToggleStates();
        ScheduleBrowserRealize(_activeBrowserTab);
    }
    /// <summary>中央に1枚だけ立てる（舞台と同じ考え方で、レイアウトツリーには触らない）。
    /// 全画面（F11）中は対象の1枚をそのまま中央に出す。畳んであれば<b>器ごと畳む</b>
    /// ——空の枠に案内文を置いても、閉じた人には閉じ切れていないようにしか見えない。
    /// 空いた場所は <see cref="ApplyDockTracks"/> が残った面へ渡す。</summary>
    private void ApplyDockCenter() {
        var kind = _paneFullscreen ? _zoomedPane ?? _dockMode.CenterPane : _dockMode.CenterPane;
        var element = kind is { } center && _paneElements.TryGetValue(center, out var pane) ? pane : null;
        PaneHost.Visibility = element is null ? Visibility.Collapsed : Visibility.Visible;
        // 据え置けるものは触らない（ApplyDockRegion と同じ理由——親の付け替えそのものが
        // 15〜40ms／枚。下や右を開閉するたびに中央のエディタを外して繋ぎ直すことになる）。
        if (element is not null && PaneHost.Children.Count == 1
            && ReferenceEquals(PaneHost.Children[0], element)) {
            element.Visibility = Visibility.Visible;
            return;
        }
        PaneHost.Children.Clear();
        PaneHost.RowDefinitions.Clear();
        PaneHost.ColumnDefinitions.Clear();
        if (element is null)
            return;
        if (element.Parent is Panel parent)
            parent.Children.Remove(element);
        element.Visibility = Visibility.Visible;
        PaneHost.Children.Add(element);
    }
    /// <summary>領域の中身を1枚に寄せる。据え置けるものは親を付け替えない（付け替え自体が高い）。
    /// 全画面（F11）中は対象の1枚だけを出すので、下／右は面が居ても畳んだ扱いにする
    /// （<c>CollapseDockForFullscreen</c> で畳んだ器を、この組み立てが開き直してしまわないように）。
    /// 枠の取り分とスプリッターは <see cref="ApplyDockTracks"/> が3領域まとめて決める。</summary>
    private void ApplyDockRegion(DockRegion region, Grid host) {
        var kind = _paneFullscreen ? null : _dockMode.OpenPaneIn(region);
        if (kind is null || !_paneElements.TryGetValue(kind.Value, out var element)) {
            host.Children.Clear();
            host.Visibility = Visibility.Collapsed;
            return;
        }
        if (!ReferenceEquals(element.Parent, host)) {
            if (element.Parent is Panel parent)
                parent.Children.Remove(element);
            host.Children.Clear();
            element.Visibility = Visibility.Visible;
            host.Children.Add(element);
        }
        host.Visibility = Visibility.Visible;
    }
    /// <summary>3領域の枠（行・列）とスプリッターを、出ている面から決める（取り分の判断は
    /// <see cref="DockTrackPlan"/> の純ロジック）。畳むときは 0 幅の器に入れたままにせず
    /// 行／列ごと 0 にして中身を Collapsed にする——0 サイズで配置された WebView2 は
    /// フレームプールごと壊れる（§26.3）。</summary>
    private void ApplyDockTracks() {
        var plan = DockTrackPlan.For(
            center: PaneHost.Visibility == Visibility.Visible,
            bottom: DockBottomHost.Visibility == Visibility.Visible,
            right: DockRightHost.Visibility == Visibility.Visible);
        CenterRow.Height = Track(plan.CenterRow, 0);
        CenterRow.MinHeight = plan.CenterRow == DockTrackSize.Collapsed ? 0 : CenterMinHeight;
        CenterColumn.Width = Track(plan.CenterColumn, 0);
        CenterColumn.MinWidth = plan.CenterColumn == DockTrackSize.Collapsed ? 0 : CenterMinWidth;
        DockBottomRow.Height = Track(plan.BottomRow, _dockMode.BottomHeight);
        DockRightColumn.Width = Track(plan.RightColumn, _dockMode.RightWidth);
        DockBottomSplitterRow.Height = new GridLength(plan.BottomSplitter ? DockSplitterThickness : 0);
        DockRightSplitterColumn.Width = new GridLength(plan.RightSplitter ? DockSplitterThickness : 0);
        DockBottomSplitter.Visibility = plan.BottomSplitter ? Visibility.Visible : Visibility.Collapsed;
        DockRightSplitter.Visibility = plan.RightSplitter ? Visibility.Visible : Visibility.Collapsed;
    }
    private static GridLength Track(DockTrackSize size, double fixedSize) => size switch {
        DockTrackSize.Fill => new GridLength(1, GridUnitType.Star),
        DockTrackSize.Fixed => new GridLength(fixedSize),
        _ => new GridLength(0),
    };
    /// <summary>右帯は「上＝中央（タイル）／真ん中＝右の領域／下＝下の領域」の3区画。
    /// アイコンの居る位置がそのまま「画面のどこに出るか」を表すので、中央の面もここに並べる
    /// （並べないと、タイルへ戻した面をドラッグで動かす取っ手が無くなる）。</summary>
    private void RebuildDockBar() {
        _dockBarBadges.Clear();
        FillDockZone(DockBarRightZone, DockRegion.Right);
        FillDockZone(DockBarCenterZone, DockRegion.Center);
        FillDockZone(DockBarBottomZone, DockRegion.Bottom);
        foreach (var kind in _dockBarBadges.Keys.ToList())
            UpdatePaneActivityBadge(kind);
    }
    private void FillDockZone(Panel zone, DockRegion region) {
        zone.Children.Clear();
        foreach (var kind in _dockMode.PanesIn(region).Where(IsPaneApplicable))
            zone.Children.Add(BuildDockBarButton(kind));
    }
    private static string DockRegionName(DockRegion region) => region switch {
        DockRegion.Right => "右",
        DockRegion.Bottom => "下",
        _ => "中央",
    };
    private Button BuildDockBarButton(PaneKind kind) {
        var region = _dockMode.RegionOf(kind);
        var open = _dockMode.IsOpen(kind);
        var icon = new Path {
            Data = TryFindResource(PaneIconKey(kind)) as Geometry,
            Width = 18, Height = 18, Stretch = Stretch.Uniform, StrokeThickness = 1.2,
            Stroke = (Brush)FindResource(open ? "Fg" : "FgDim"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var badge = new Ellipse {
            Width = 6, Height = 6, Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 11, 10, 0),
        };
        _dockBarBadges[kind] = badge;
        var content = new Grid();
        content.Children.Add(icon);
        content.Children.Add(badge);
        var button = new Button {
            Style = (Style)FindResource("ActivityIconButtonRight"),
            Content = content, Tag = open,
            ToolTip = $"{PaneLabel(kind)}（{DockRegionName(region)}）：クリックで開閉／ドラッグまたは右クリックで場所を変更",
        };
        button.ContextMenu = BuildDockPlacementMenu(kind, openLeftOfCursor: true);
        button.Click += (_, _) => OnDockBarButtonClick(kind);
        HookDockDragSource(button, kind);
        return button;
    }
    /// <summary>帯のアイコンのクリック。3領域とも「その1枚にする」で、
    /// 出ている面をもう一度押すと畳む（開いたときのフォーカスは <see cref="OpenDockPane"/> が当てる）。</summary>
    private void OnDockBarButtonClick(PaneKind kind) => ToggleDockPane(kind);

    // ===== ドラッグで割り当てを変える =====

    /// <summary>帯のアイコン／ドックに出ている面のヘッダーを掴んだときに載せる中身。</summary>
    private const string DockDragFormat = "Loomo.DockPane";
    private Point _dockDragStart;
    private bool _dockDragArmed;
    private UIElement? _dockDragSource;
    private PaneKind _dockDragKind;
    /// <summary>アイコンを掴んで区画へ落とすと割り当てが変わる（右クリックメニューと同じ操作の直接版）。</summary>
    private void HookDockDragSource(UIElement source, PaneKind kind)
        => source.PreviewMouseLeftButtonDown += (_, e) => ArmDockDrag(source, kind, e.GetPosition(null));
    /// <summary>掴んだ状態を仕込む。しきい値の監視は<b>ウィンドウ側</b>で拾う——掴んだ要素に
    /// 任せると、そこからカーソルが出た瞬間に Move が来なくなってドラッグが始まらないうえ、
    /// 通りすがりの別のアイコンが（共有していた「掴み中」を見て）代わりに掴まれてしまう。
    /// マウスキャプチャを使わないのは、ボタンの Click を壊さないため。</summary>
    private void ArmDockDrag(UIElement source, PaneKind kind, Point start) {
        DisarmDockDrag();
        _dockDragSource = source;
        _dockDragKind = kind;
        _dockDragStart = start;
        _dockDragArmed = true;
        PreviewMouseMove += OnDockDragArmedMove;
        PreviewMouseLeftButtonUp += OnDockDragArmedUp;
    }
    private void DisarmDockDrag() {
        if (!_dockDragArmed)
            return;
        _dockDragArmed = false;
        _dockDragSource = null;
        PreviewMouseMove -= OnDockDragArmedMove;
        PreviewMouseLeftButtonUp -= OnDockDragArmedUp;
    }
    private void OnDockDragArmedUp(object sender, MouseButtonEventArgs e) => DisarmDockDrag();
    private void OnDockDragArmedMove(object sender, MouseEventArgs e) {
        if (!_dockDragArmed)
            return;
        if (e.LeftButton != MouseButtonState.Pressed) {
            DisarmDockDrag();
            return;
        }
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dockDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _dockDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var source = _dockDragSource;
        var kind = _dockDragKind;
        DisarmDockDrag();
        if (source is not null)
            BeginDockPaneDrag(source, kind);
    }
    /// <summary>ドックの割り当てを変えるドラッグを始める。タイルの2D並べ替え（<c>BeginPaneDrag</c>）とは
    /// 別物で、行き先は帯の3区画だけ。</summary>
    private void BeginDockPaneDrag(DependencyObject source, PaneKind kind) {
        if (!_dockActive)
            return;
        var data = new DataObject(DockDragFormat, kind.ToString());
        PaneLayoutDebugLog.Log($"[dockdrag] begin {kind}");
        try {
            DragDrop.DoDragDrop(source, data, DragDropEffects.Move);
        } finally {
            ClearDockZoneHighlights();
        }
    }
    private static PaneKind? DockDragPayload(IDataObject data)
        => data.GetDataPresent(DockDragFormat)
           && Enum.TryParse<PaneKind>(data.GetData(DockDragFormat) as string, out var kind)
            ? kind
            : null;
    private static DockRegion? DockZoneRegion(object sender)
        => sender is FrameworkElement { Tag: string tag } && Enum.TryParse<DockRegion>(tag, out var region)
            ? region
            : null;
    private void OnDockZoneDragOver(object sender, DragEventArgs e) {
        var kind = DockDragPayload(e.Data);
        var region = DockZoneRegion(sender);
        // 同じ区画へ落としても何も起きないので、受けない（＝カーソルも「不可」のまま）。
        var accepted = kind is { } k && region is { } r && _dockMode.RegionOf(k) != r;
        e.Effects = accepted ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        ClearDockZoneHighlights();
        if (accepted && sender is Panel panel)
            panel.Background = (Brush)FindResource("SelectionBg");
    }
    private void OnDockZoneDragLeave(object sender, DragEventArgs e) => ClearDockZoneHighlights();
    private void OnDockZoneDrop(object sender, DragEventArgs e) {
        PaneLayoutDebugLog.Log($"[dockdrag] drop zone={DockZoneRegion(sender)} payload={DockDragPayload(e.Data)}");
        ClearDockZoneHighlights();
        e.Handled = true;
        if (DockDragPayload(e.Data) is { } kind && DockZoneRegion(sender) is { } region)
            PlaceDockPane(kind, region);
    }
    /// <summary>区画の受け皿の塗りを消す。<c>Transparent</c> に戻すのが要点で、null にすると
    /// 当たり判定が消えて空の区画へ落とせなくなる。</summary>
    private void ClearDockZoneHighlights() {
        foreach (var zone in new[] { DockBarRightZone, DockBarCenterZone, DockBarBottomZone })
            zone.Background = Brushes.Transparent;
    }
    /// <summary>「どこに置くか」の共通メニュー。帯のアイコンとビュー・スイッチャーの行の
    /// 両方が使う（中央へ移した面は帯から消えるので、戻す口が一覧側にも要る）。</summary>
    private ContextMenu BuildDockPlacementMenu(PaneKind kind, bool openLeftOfCursor = false) {
        var menu = new ContextMenu();
        if (openLeftOfCursor)
            // 帯は画面の右端なので、既定のままだとメニューがウィンドウの外＝デスクトップ上へ出る。
            // 右クリックで開いた ContextMenu の <c>Placement</c>／<c>PlacementTarget</c> は
            // 開く側（PopupControlService）がマウス位置に決め直すので指定しても効かない。
            // 幅が確定する Opened で左へずらして、メニューの右端をカーソルに合わせる。
            menu.Opened += (_, _) => menu.HorizontalOffset = -(menu.ActualWidth + 2);
        foreach (var (region, label) in new[] {
            (DockRegion.Center, "中央（タイル配置）へ"),
            (DockRegion.Bottom, "下の領域へ"),
            (DockRegion.Right, "右の領域へ"),
        }) {
            var item = new MenuItem {
                Header = label,
                IsChecked = _dockMode.RegionOf(kind) == region,
                IsEnabled = _dockMode.RegionOf(kind) != region,
            };
            var target = region;
            item.Click += (_, _) => PlaceDockPane(kind, target);
            menu.Items.Add(item);
        }
        return menu;
    }
    /// <summary>帯のアイコンの活動の点（袖のバッジ相当）。色は袖と同じ意味で、
    /// 実行中＝アクセント／失敗＝赤／完了＝緑。</summary>
    private void UpdateDockBarBadge(PaneKind kind, PaneActivityKind activity) {
        if (!_dockBarBadges.TryGetValue(kind, out var badge))
            return;
        switch (activity) {
            case PaneActivityKind.Running:
            case PaneActivityKind.Stopped:
            case PaneActivityKind.Approval:
                badge.Visibility = Visibility.Visible;
                badge.Fill = (Brush)FindResource("Accent");
                break;
            case PaneActivityKind.Failed:
                badge.Visibility = Visibility.Visible;
                badge.Fill = PaneActivityFailedBrush;
                break;
            case PaneActivityKind.Succeeded:
                badge.Visibility = Visibility.Visible;
                badge.Fill = PaneActivitySucceededBrush;
                break;
            default:
                badge.Visibility = Visibility.Collapsed;
                break;
        }
    }
}
