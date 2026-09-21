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
    private DockBarPresenter? _dockBarPresenter;
    private DockBarPresenter DockBarPresentation
        => _dockBarPresenter ??= new DockBarPresenter(
            _dockMode,
            new DockBarHosts(DockBarRightZone, DockBarCenterZone, DockBarBottomZone),
            this, IsPaneApplicable, ToggleDockPane, PlaceDockPane, DockPaneDrag.Hook,
            PaneActivityFailedBrush, PaneActivitySucceededBrush);
    private DockLayoutPresenter? _dockPresenter;
    private DockLayoutPresenter DockPresenter
        => _dockPresenter ??= new DockLayoutPresenter(
            _dockMode,
            _paneElements,
            new DockLayoutHosts(
                PaneHost, DockBottomHost, DockRightHost, DockBar,
                DockBarRightZone, DockBarCenterZone, DockBarBottomZone,
                CenterRow, DockBottomRow, DockBottomSplitterRow,
                CenterColumn, DockRightColumn, DockRightSplitterColumn, DockBarColumn,
                DockBottomSplitter, DockRightSplitter),
            DockBarPresentation.Badges, DockSplitterThickness, CenterMinWidth, CenterMinHeight);
    private DockPaneDragController? _dockPaneDrag;
    private DockPaneDragController DockPaneDrag
        => _dockPaneDrag ??= new DockPaneDragController(this, BeginDockPaneDrag);

    private void InitializeDock()
        => new DockSplitterController(
            _dockMode, DockBottomSplitter, DockRightSplitter, DockBottomRow, DockRightColumn,
            () => DockPresenter.ApplyTracks(), dragging => _paneSplitterDragging = dragging,
            () => SaveActiveWorkspaceSnapshot()).Attach();

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
        DockPresenter.ClearHosts();
    }

    // ===== 復元・保存 =====

    /// <summary>ワークスペース復元の前半：状態だけ入れて器を出す（中身は
    /// <see cref="CompleteDockSnapshotRestore"/> で組む）。集中モードの
    /// <c>PrepareStageSnapshot</c> と同じ二段構え。</summary>
    private void PrepareDockSnapshot(bool dock, DockSnapshot? snapshot) {
        ClearDockModeForWorkspaceSwitch();
        _dockMode.Restore(dock, snapshot);
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
    private DockSnapshot CaptureDockSnapshot() => _dockMode.CaptureSnapshot();

    // ===== 問い合わせ =====

    /// <summary>ビュー・スイッチャーとコマンドパレットに並べる面。ドック中は帯に出せる面だけ
    /// （Diff などドックに置かない面を並べると、押しても何も出ない行になる）。</summary>
    private IEnumerable<PaneKind> PaneOrderForMode()
        => DockLayoutCoordinator.PaneOrderForMode(StageModeCoordinator.StageOrder, _dockActive);
    /// <summary>ドックモードで、そのペインがいま見えているか。3領域とも「出ている1枚か」で決まる。</summary>
    private bool IsDockPaneShown(PaneKind kind) => _dockMode.IsOpen(kind);
    /// <summary>実体を作って動かしておくべきか（＝画面のどこかに出ている）。集中モードは袖でも
    /// 生きたまま縮小表示するので常に true。</summary>
    private bool IsPaneMaterialized(PaneKind kind)
        => _stageActive || (_dockActive ? IsDockPaneShown(kind) : IsPaneVisible(kind));
    /// <summary>全画面・ズームのように「画面に出ている1枚」を要る操作の対象を、ドック中は
    /// <b>実際に出ている面</b>へ寄せる。畳んである面（閉じた中央を含む）を選ぶと、親も
    /// 表示も無い要素を全画面にしようとして落ちるだけなので、出ている面が1つも無ければ null。</summary>
    private PaneKind? DockShownPane(PaneKind? preferred)
        => _dockMode.ResolveShownPane(preferred);
    /// <summary>ペインを現在の親に据え置ける集合（袖＝集中／分割、ドックの領域＝ドック）。
    /// 全画面（F11）中はどちらも畳んで対象1枚だけを出すので、据え置きは無し。</summary>
    private IReadOnlyCollection<PaneKind> PanesToKeepAttached()
        => _dockMode.PanesToKeepAttached(_paneFullscreen, WingKinds);
    /// <summary>軌跡の配置キーに混ぜるドックの状態（開いている面）。中央のタイルが同じでも
    /// 道具の開閉で見え方は変わるので、これを入れないとドック中の操作が軌跡に残らない。</summary>
    private string? CurrentDockKey()
        => _dockMode.LayoutKey();
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
        if (_dockMode.ResolveShownPane(preferred: null) is { } next)
            FocusPane(next);
        else
            _focusedRegion = null;
    }
    /// <summary>ドックモードの Ctrl+T 相当：中央に立てる面を順に切り替える
    /// （中央を畳んであるなら、まず中央の先頭の面を立て直す）。</summary>
    private void CycleDockCenter(int direction) {
        var next = _dockMode.NextInRegion(DockRegion.Center, _dockMode.CenterPane, direction, IsPaneApplicable);
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
        DockPresenter.ApplyCenter(_paneFullscreen, _zoomedPane);
        DockPresenter.ApplyRegion(DockRegion.Bottom, _paneFullscreen);
        DockPresenter.ApplyRegion(DockRegion.Right, _paneFullscreen);
        DockPresenter.ApplyTracks();
        // 帯の印だけでなくヘッダー（モード名・中央の面）も合わせる。
        // UpdatePaneToggleStates が中で RebuildDockBar を呼ぶので、帯はここで二度組まない。
        UpdatePaneToggleStates();
        ScheduleBrowserRealize(_activeBrowserTab);
    }
    /// <summary>右帯は「上＝中央（タイル）／真ん中＝右の領域／下＝下の領域」の3区画。
    /// アイコンの居る位置がそのまま「画面のどこに出るか」を表すので、中央の面もここに並べる
    /// （並べないと、タイルへ戻した面をドラッグで動かす取っ手が無くなる）。</summary>
    private void RebuildDockBar() {
        foreach (var kind in DockBarPresentation.Rebuild())
            UpdatePaneActivityBadge(kind);
    }

    // ===== ドラッグで割り当てを変える =====

    private const string DockDragFormat = "Loomo.DockPane";
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
            ? DockLayoutPresentation.ParsePaneKind(data.GetData(DockDragFormat) as string)
            : null;
    private static DockRegion? DockZoneRegion(object sender)
        => sender is FrameworkElement { Tag: string tag }
            ? DockLayoutPresentation.ParseRegion(tag)
            : null;
    private void OnDockZoneDragOver(object sender, DragEventArgs e) {
        var kind = DockDragPayload(e.Data);
        var region = DockZoneRegion(sender);
        // 同じ区画へ落としても何も起きないので、受けない（＝カーソルも「不可」のまま）。
        var accepted = DockLayoutPresentation.CanDrop(_dockMode, kind, region);
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
        return DockBarPresentation.BuildPlacementMenu(kind, openLeftOfCursor);
    }
    /// <summary>帯のアイコンの活動の点（袖のバッジ相当）。色は袖と同じ意味で、
    /// 実行中＝アクセント／失敗＝赤／完了＝緑。</summary>
    private void UpdateDockBarBadge(PaneKind kind, PaneActivityKind activity) {
        _dockBarPresenter?.UpdateBadge(kind, activity);
    }
}
