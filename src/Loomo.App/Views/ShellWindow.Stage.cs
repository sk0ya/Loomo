using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: ソロモード（舞台＋袖）。1ペインを全面の「舞台」に立て、残りのペインは
/// 右端の「袖」でペインを VisualBrush として縮小表示する。袖カードは実コントロールを
/// 子に持たず、元の表示を描くだけなので、袖表示のためにペインを動かさない。
/// レイアウトモード（タイル表示／PaneHost）とは表示の差し替えだけで切替わり、
/// レイアウトツリー（_root）には一切触れない — レイアウトへ戻せば元のタイル配置・比率がそのまま戻る。
/// 「俯瞰」は全セッションをカードで一望する Exposé 風レイヤ（クリックで舞台へダイブ）。
/// ソロ中に <c>FocusPane</c> が呼ばれると対象が自動で舞台に立つので、AI がファイルを
/// 開いた・差分を出した等の既存フローがそのまま「舞台の自動転換」になる。
/// </summary>
public partial class ShellWindow
{
    // ===== ソロモード（舞台＋袖＋俯瞰） =====

    /// <summary>ソロモード中か（true＝ソロ、false＝レイアウトモード）。中は RebuildPaneLayout がステージの組み直しへ委譲される。</summary>
    private bool _stageActive;
    /// <summary>舞台に立っているペイン。</summary>
    private PaneKind _stagePane;

    /// <summary>指定ペインが舞台に立っているか。</summary>
    private bool OnStage(PaneKind kind) => _stagePane == kind;

    /// <summary>俯瞰（全カード一望）レイヤを表示中か。</summary>
    private bool _overviewActive;
    /// <summary>リサイズ追従のデバウンス用タイマー。発火時に仮想寸法が変わっていたら組み直す。</summary>
    private DispatcherTimer? _stageResizeTimer;
    /// <summary>直近の構築に使った仮想キャンバス寸法（＝舞台の実寸）。</summary>
    private Size _stageBuiltSize;
    /// <summary>袖カードの VisualBrush が参照する、舞台サイズにレイアウト済みの非表示ホスト。</summary>
    private readonly Dictionary<PaneKind, Grid> _stageThumbnailHosts = new();

    /// <summary>有効なセッション（タイトルバーの表示トグルが ON のもの）。タイル配置（<c>_root</c>）とは独立した集合で、
    /// 通常はタイルより広い。Main に出ている有効セッションはそのまま Main に、Main に出ていない有効セッションは
    /// 袖（ミニチュア）に出る（＝有効セッションは Main と袖のどちらかに必ず出る）。無効なセッションはどちらにも出さない。</summary>
    private readonly HashSet<PaneKind> _enabledSessions = new();
    /// <summary>袖カードの幅。高さは舞台の縦横比から導出される。</summary>
    private const double WingCardWidth = 180;
    /// <summary>俯瞰カードの幅。</summary>
    private const double OverviewCardWidth = 320;
    /// <summary>袖の列（カード＋余白＋スクロールバー）が占める幅の見積もり。舞台幅の算出に使う。</summary>
    private const double WingColumnReserve = 210;
    /// <summary>袖カードの待機時不透明度（暗がりで生きて待っている感を出す）。</summary>
    private const double WingRestOpacity = 0.72;

    /// <summary>袖カードのドラッグ判定（しきい値を超えたらタイルへの配置ドラッグを始める）。</summary>
    private Point _wingDragStart;
    private bool _wingDragArmed;

    /// <summary>袖・俯瞰での並び順（よく使うものから）。</summary>
    private static readonly PaneKind[] StageOrder =
    [
        PaneKind.Editor, PaneKind.Terminal, PaneKind.Browser, PaneKind.EditorSupport,
        PaneKind.Git, PaneKind.Diff, PaneKind.Ai,
        // AI トレースは通常セッションとしては表示しない。
        // PaneKind.Trace,
    ];

    /// <summary>ソロ⇄レイアウトの切替（タイトルバーのトグル／Ctrl+Shift+T）。</summary>
    private void OnToggleStageMode(object sender, RoutedEventArgs e) => ToggleDisplayMode();

    /// <summary>ソロ⇄レイアウトを切り替える。</summary>
    private void ToggleDisplayMode()
    {
        if (_stageActive)
            ExitStageMode();   // → レイアウトモード
        else
            EnterStageMode();  // → ソロモード
    }

    private void EnterStageMode()
        => EnterStageMode(null);

    /// <summary>レイアウトモードからソロモード（単一ステージ）へ入る。</summary>
    private void EnterStageMode(PaneKind? pane)
    {
        if (_stageActive)
            return;
        _stageActive = true;
        _overviewActive = false;
        _zoomedPane = null;
        _stagePane = pane is { } requested && _paneElements.ContainsKey(requested)
            ? requested
            : _focusedRegion?.Pane
            ?? AllLeaves().FirstOrDefault(l => !l.Hidden)?.Kind
            ?? PaneKind.Editor;
        PaneHost.Opacity = 0;
        PaneHost.IsHitTestVisible = false;
        StageHost.Visibility = Visibility.Visible;
        StageHost.SizeChanged += OnStageHostSizeChanged;
        UpdateModeButtons();
        RebuildStage();
        FocusPane(_stagePane);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>ワークスペース切替前に、保存済み状態を変えずステージ表示だけ通常状態へ戻す。</summary>
    private void ClearStageModeForWorkspaceSwitch()
    {
        if (!_stageActive)
            return;

        _stageActive = false;
        _overviewActive = false;
        StageHost.SizeChanged -= OnStageHostSizeChanged;
        _stageResizeTimer?.Stop();
        DetachPaneElements();
        StageArea.Children.Clear();
        StageSourceArea.Children.Clear();
        WingStrip.Children.Clear();
        OverviewPanel.Children.Clear();
        _stageThumbnailHosts.Clear();
        OverviewLayer.Visibility = Visibility.Collapsed;
        StageHost.Visibility = Visibility.Collapsed;
        PaneHost.Opacity = 1;
        PaneHost.IsHitTestVisible = true;
        PaneToggleBar.Visibility = Visibility.Visible;
        UpdateModeButtons();
    }

    /// <summary>
    /// ワークスペース復元時、ペインレイアウト適用前にソロ表示状態だけ先に立てる。
    /// これにより ApplyPaneLayout がタイル表示を描かず、最初からステージとして組み直す。
    /// </summary>
    private void PrepareStageSnapshot(bool solo, StageSnapshot? snapshot)
    {
        ClearStageModeForWorkspaceSwitch();
        if (!solo)
            return;

        snapshot ??= StageSnapshot.Default();
        _stageActive = true;
        _overviewActive = snapshot.Overview;   // 俯瞰を開いたまま離れたら俯瞰のまま戻る
        _zoomedPane = null;
        _stagePane = snapshot.Pane is { } requested && _paneElements.ContainsKey(requested)
            ? requested
            : PaneKind.Editor;
        PaneHost.Opacity = 0;
        PaneHost.IsHitTestVisible = false;
        StageHost.Visibility = Visibility.Visible;
        StageHost.SizeChanged += OnStageHostSizeChanged;
        UpdateModeButtons();
    }

    /// <summary>タブ実体の復元後に、ステージの内容とフォーカスを確定する。</summary>
    private void CompleteStageSnapshotRestore()
    {
        if (!_stageActive)
            return;

        RebuildStage();
        // 舞台が EditorSupport なら、復元直後に現在のエディタ内容でプレビューを描き直す
        // （タブ復元経路で描かれない場合の保険。可視意図 false でも onStage で描く）。
        if (_stagePane == PaneKind.EditorSupport)
            _ = UpdateEditorSupportAsync();
        // 組み直し直後は舞台の要素がまだレイアウト前（IsVisible=false）で Focus が失敗し得るため、
        // レイアウト確定後（Loaded 優先度）にフォーカスを入れる。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_stageActive)
                FocusPane(_stagePane);
        }));
    }

    /// <summary>ソロモードを抜けてレイアウトモード（タイル表示）へ戻す。</summary>
    private void ExitStageMode()
    {
        if (!_stageActive)
            return;
        _stageActive = false;
        _overviewActive = false;
        StageHost.SizeChanged -= OnStageHostSizeChanged;
        _stageResizeTimer?.Stop();
        DetachPaneElements();
        StageArea.Children.Clear();
        StageSourceArea.Children.Clear();
        WingStrip.Children.Clear();
        OverviewPanel.Children.Clear();
        _stageThumbnailHosts.Clear();
        OverviewLayer.Visibility = Visibility.Collapsed;
        StageHost.Visibility = Visibility.Collapsed;
        PaneHost.Opacity = 1;
        PaneHost.IsHitTestVisible = true;
        PaneToggleBar.Visibility = Visibility.Visible;
        UpdateModeButtons();
        RebuildPaneLayout();
        FocusPane(_stagePane);
        // タイル表示でターミナルが見えるようになったなら、未確認の結果は「見た」扱い。
        if (IsPaneVisible(PaneKind.Terminal))
            MarkPaneActivitySeen(PaneKind.Terminal);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>舞台のペインを差し替える（袖・俯瞰カードのクリック先）。</summary>
    private void SetStagePane(PaneKind kind)
    {
        if (!_stageActive)
            return;
        _overviewActive = false;
        _stagePane = kind;
        RebuildStage();
        // EditorSupport を舞台へ立てた瞬間に、現在のエディタ内容でプレビューを描き直す
        // （RebuildStage はペイン実体を移すだけで中身は更新しないため、ここで明示的に流し込む）。
        if (kind == PaneKind.EditorSupport)
            _ = UpdateEditorSupportAsync();
        MarkPaneActivitySeen(kind);   // 舞台に立った＝目に入ったので未確認バッジを流す
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>
    /// Ctrl+T：現在のモードに応じた巡回。ソロは舞台のものを並び順で前後へ転換し、
    /// レイアウトは保存レイアウトを巡回する（<see cref="CycleLayout"/>）。
    /// </summary>
    private void CycleInActiveMode(int direction)
    {
        if (_stageActive)
            CycleStage(direction);
        else
            CycleLayout(direction);
    }

    /// <summary>舞台を並び順で前後のペインへ転換する（ソロ中の Ctrl+T / Ctrl+W h/j/k/l）。</summary>
    private void CycleStage(int direction)
    {
        var index = Array.IndexOf(StageOrder, _stagePane);
        var next = StageOrder[((index < 0 ? 0 : index) + direction + StageOrder.Length) % StageOrder.Length];
        SetStagePane(next);
        FocusPane(next);
    }

    /// <summary>全ペインを現在の親から外す（ステージ⇄タイルどちらの構成でも直親は必ず Panel）。</summary>
    private void DetachPaneElements()
    {
        foreach (var element in _paneElements.Values)
            if (element.Parent is Panel parent)
                parent.Children.Remove(element);
    }

    /// <summary>
    /// 仮想キャンバス＝舞台の実寸。初回（StageArea 未レイアウト）は同じセルを
    /// 占めていた PaneHost の実寸から見積もる。カードの縦横比もこの寸法に追従する。
    /// </summary>
    private Size StageVirtualSize()
    {
        if (StageArea.ActualWidth > 0 && StageArea.ActualHeight > 0)
            return new Size(StageArea.ActualWidth, StageArea.ActualHeight);
        var hostW = StageHost.ActualWidth > 0 ? StageHost.ActualWidth : PaneHost.ActualWidth;
        var hostH = StageHost.ActualHeight > 0 ? StageHost.ActualHeight : PaneHost.ActualHeight;
        return new Size(
            Math.Max(hostW - WingColumnReserve - 16, 480),   // 16 ≒ StageArea の左右マージン
            Math.Max(hostH - 18, 320));                      // 18 ≒ 上下マージン
    }

    /// <summary>ウィンドウリサイズへの追従。連続イベントをデバウンスし、寸法が実際に変わったときだけ組み直す。</summary>
    private void OnStageHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_stageActive)
            return;
        if (_stageResizeTimer is null)
        {
            _stageResizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _stageResizeTimer.Tick += (_, _) =>
            {
                _stageResizeTimer!.Stop();
                if (!_stageActive)
                    return;
                var size = StageVirtualSize();
                if (Math.Abs(size.Width - _stageBuiltSize.Width) > 1
                    || Math.Abs(size.Height - _stageBuiltSize.Height) > 1)
                    RebuildStage();
            };
        }
        _stageResizeTimer.Stop();
        _stageResizeTimer.Start();
    }

    /// <summary>セッションが有効か（タイトルバーの表示トグルが ON）。タイル配置とは独立で、
    /// 有効なら Main か袖のどちらかに必ず出る。無効ならどちらにも出さない。</summary>
    private bool IsSessionEnabled(PaneKind kind) => _enabledSessions.Contains(kind);

    /// <summary>そのセッションが Main（舞台／タイル）に実際に表示されているか。
    /// ソロは舞台に立っている1枚、レイアウトはズーム中なら対象1枚・通常は可視リーフすべて。
    /// 有効なのに Main に出ていないセッションは袖（ミニチュア）に出す（＝有効なら Main か袖のどちらか）。</summary>
    private bool IsShownInMain(PaneKind kind)
    {
        if (_stageActive)
            return !_overviewActive && OnStage(kind);
        if (_zoomedPane is { } zoom)
            return zoom == kind && IsPaneVisible(kind);
        return IsPaneVisible(kind);
    }

    /// <summary>有効セッション集合を復元する（null／空の旧データは全セッション有効＝袖が常時にぎわう既定）。</summary>
    private void LoadEnabledSessions(IEnumerable<PaneKind>? enabled)
    {
        _enabledSessions.Clear();
        if (enabled is not null)
            foreach (var kind in enabled)
                if (_paneElements.ContainsKey(kind))
                    _enabledSessions.Add(kind);
        if (_enabledSessions.Count == 0)
            foreach (var kind in StageOrder)
                _enabledSessions.Add(kind);
    }

    /// <summary>タイトルバーのトグルでセッションの有効／無効を切り替える。
    /// 有効化：レイアウトにタイル（隠れたリーフ）があれば Main へ戻し、無ければ袖（ミニチュア）に出す。
    /// 無効化：Main に出ていれば隠して（最後の1枚は隠さない）どこにも出さない。</summary>
    private void ToggleSessionEnabled(PaneKind kind)
    {
        if (_enabledSessions.Contains(kind))
        {
            // 無効化：Main に出ていれば隠す。最後の1枚は隠せないので、その場合は有効のまま据え置く。
            if (IsPaneVisible(kind))
            {
                SetPaneVisible(kind, false);
                if (IsPaneVisible(kind))
                    return;
            }
            _enabledSessions.Remove(kind);
            RebuildSessionsView();
        }
        else
        {
            _enabledSessions.Add(kind);
            // レイアウトに在って隠れているタイルなら Main へ戻す（SetPaneVisible が再構築・保存まで行う）。
            // タイルが無い（＝レイアウト未配置）なら袖に出すだけ。
            if (FindLeaf(kind) is { Hidden: true })
                SetPaneVisible(kind, true);
            else
                RebuildSessionsView();
        }
    }

    /// <summary>有効セッションの変化を画面へ反映する（トグル状態・Main／袖の組み直し・保存）。
    /// SetPaneVisible を経た経路は既に再構築済みなので、それ以外の経路から呼ぶ。</summary>
    private void RebuildSessionsView()
    {
        UpdatePaneToggleStates();
        if (_stageActive)
            RebuildStage();
        else
        {
            RebuildWings();
            UpdateWingHostVisibility();
        }
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>俯瞰に並べるセッション（有効なもの＋舞台に立っているもの）。</summary>
    private IEnumerable<PaneKind> OverviewKinds() => StageOrder.Where(k => IsSessionEnabled(k) || OnStage(k));

    /// <summary>ソロモードの画面を組み直す（舞台1枚＋袖カード、または俯瞰カード一覧）。</summary>
    private void RebuildStage()
    {
        if (!_stageActive)
            return;

        DetachPaneElements();
        StageArea.Children.Clear();
        StageSourceArea.Children.Clear();
        OverviewPanel.Children.Clear();
        _stageThumbnailHosts.Clear();
        _stageActivityBadges.Clear();

        var virtualSize = StageVirtualSize();
        _stageBuiltSize = virtualSize;
        BuildStageThumbnailSources(virtualSize);

        if (_overviewActive)
        {
            OverviewLayer.Visibility = Visibility.Visible;
            foreach (var kind in OverviewKinds())
                OverviewPanel.Children.Add(BuildSessionCard(
                    kind, OverviewCardWidth, virtualSize, isOverview: true));
        }
        else
        {
            OverviewLayer.Visibility = Visibility.Collapsed;
            // 舞台：主役を全面に立てる。
            StageArea.Children.Add(BuildLiveSlot(_stagePane));
        }

        RebuildWings();
        UpdatePaneToggleStates();
        UpdateWingHostVisibility();
        ScheduleBrowserRealize(_activeBrowserTab);

        // 一時診断：LOOMO_STAGE_DEBUG=1 でレイアウト確定後の実寸を %TEMP% へ書き出す
        if (Environment.GetEnvironmentVariable("LOOMO_STAGE_DEBUG") == "1")
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(DumpStageDiagnostics));
    }

    /// <summary>袖・俯瞰カードの描画元（実体ペイン）を舞台サイズでレイアウトする（ソロモード専用）。</summary>
    private void BuildStageThumbnailSources(Size virtualSize)
    {
        var kinds = _overviewActive
            ? OverviewKinds()
            : StageOrder.Where(k => !OnStage(k) && IsSessionEnabled(k));
        foreach (var kind in kinds)
        {
            var element = _paneElements[kind];
            element.Visibility = Visibility.Visible;

            var host = new Grid
            {
                Width = virtualSize.Width,
                Height = virtualSize.Height,
                Clip = new RectangleGeometry(new Rect(0, 0, virtualSize.Width, virtualSize.Height)),
            };
            host.Children.Add(element);
            StageSourceArea.Children.Add(host);
            _stageThumbnailHosts[kind] = host;
        }
    }

    /// <summary>袖（ミニチュア）を組み直す。両モードとも「有効だが Main に出ていない」セッションを
    /// 実体ペインのライブ縮小で並べる（Main に出ているもの・無効なものは出さない）。
    /// ソロは舞台外の有効セッション、レイアウトはタイル未配置やズーム中の非ズームペインが対象。</summary>
    private void RebuildWings()
    {
        WingStrip.Children.Clear();
        if (_stageActive)
        {
            foreach (var kind in StageOrder.Where(k => !OnStage(k) && IsSessionEnabled(k)))
                WingStrip.Children.Add(BuildSessionCard(kind, WingCardWidth, _stageBuiltSize, isOverview: false));
        }
        else
        {
            var virtualSize = BuildLayoutWingSources();
            foreach (var kind in StageOrder.Where(k => IsSessionEnabled(k) && !IsShownInMain(k)))
                WingStrip.Children.Add(BuildLayoutWingCard(kind, virtualSize, WingCardWidth));
        }
    }

    /// <summary>レイアウトモードの袖カードの描画元を組む：有効だが Main に出ていないペイン
    /// （タイル未配置・ズーム中の非ズームペイン等）を Main 領域サイズの非表示ホスト（StageSourceArea）へ
    /// 寄せてレイアウトする。これらは PaneHost に居ないため、ライブ縮小の描画元として別途アレンジが要る。</summary>
    private Size BuildLayoutWingSources()
    {
        StageSourceArea.Children.Clear();
        _stageThumbnailHosts.Clear();

        var width = PaneHost.ActualWidth > 0 ? PaneHost.ActualWidth : StageVirtualSize().Width;
        var height = PaneHost.ActualHeight > 0 ? PaneHost.ActualHeight : StageVirtualSize().Height;
        var virtualSize = new Size(Math.Max(width, 1), Math.Max(height, 1));

        foreach (var kind in StageOrder.Where(k => IsSessionEnabled(k) && !IsShownInMain(k)))
        {
            var element = _paneElements[kind];
            if (element.Parent is Panel parent)
                parent.Children.Remove(element);
            element.Visibility = Visibility.Visible;

            var host = new Grid
            {
                Width = virtualSize.Width,
                Height = virtualSize.Height,
                Clip = new RectangleGeometry(new Rect(0, 0, virtualSize.Width, virtualSize.Height)),
            };
            host.Children.Add(element);
            StageSourceArea.Children.Add(host);
            _stageThumbnailHosts[kind] = host;
        }
        return virtualSize;
    }

    /// <summary>レイアウトモードで袖を組み直す。実体ペインはタイルに在るので、レイアウト確定後（Loaded）に
    /// ライブ要素の実寸でカードを作る。</summary>
    private void ScheduleLayoutWings()
    {
        if (_stageActive)
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_stageActive)
                return;
            RebuildWings();
            UpdateWingHostVisibility();
        }));
    }

    /// <summary>袖の列（WingHost）の表示と、俯瞰ボタン（ソロ専用）の表示を現状へ同期する。</summary>
    private void UpdateWingHostVisibility()
    {
        if (WingHost is null)
            return;
        WingHost.Visibility = WingStrip.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OverviewButton.Visibility = _stageActive ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>一時診断：袖カードと VisualBrush 描画元の実寸をログする。</summary>
    private void DumpStageDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"virtual={_stageBuiltSize.Width:F0}x{_stageBuiltSize.Height:F0} " +
                      $"StageArea={StageArea.ActualWidth:F0}x{StageArea.ActualHeight:F0} " +
                      $"StageHost={StageHost.ActualWidth:F0}x{StageHost.ActualHeight:F0} " +
                      $"PaneHost={PaneHost.ActualWidth:F0}x{PaneHost.ActualHeight:F0}");
        foreach (var child in WingStrip.Children)
        {
            if (child is not Border { Child: Grid root } card || root.Children.Count == 0
                || root.Children[0] is not Border { Background: VisualBrush { Visual: FrameworkElement pane } })
                continue;
            sb.AppendLine($"card={card.ActualWidth:F0}x{card.ActualHeight:F0} " +
                          $"pane={pane.Name}:{pane.ActualWidth:F0}x{pane.ActualHeight:F0}");
        }
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "loomo-stage-diag.txt"), sb.ToString());
    }

    /// <summary>ソロ／俯瞰のカード：舞台サイズにレイアウトした非表示ホスト（StageSourceArea）を縮小描画する。
    /// クリックでそのセッションを舞台へ立てる。</summary>
    private Border BuildSessionCard(PaneKind kind, double width, Size virtualSize, bool isOverview)
    {
        Visual source = _stageThumbnailHosts.TryGetValue(kind, out var host) ? host : _paneElements[kind];
        return BuildCard(kind, width, source, virtualSize.Width, virtualSize.Height, isOverview,
            () => { SetStagePane(kind); FocusPane(kind); });
    }

    /// <summary>レイアウトモードの袖カード：Main 領域サイズへ寄せた非表示ホスト（<see cref="BuildLayoutWingSources"/>）
    /// をライブ縮小で描画する。クリックでそのペインを左上ペインと入れ替える（クリックしたセッションが左上の
    /// 位置を引き継ぎ、元の左上ペインは袖へ退場）。ズーム中は対象をズームへ昇格。</summary>
    private Border BuildLayoutWingCard(PaneKind kind, Size virtualSize, double width)
    {
        Visual source = _stageThumbnailHosts.TryGetValue(kind, out var host) ? host : _paneElements[kind];
        return BuildCard(kind, width, source, virtualSize.Width, virtualSize.Height, isOverview: false,
            () =>
            {
                if (_zoomedPane is not null)
                {
                    if (IsPaneVisible(kind))
                        ZoomPane(kind);   // ズーム中の袖カード＝そのペインを舞台（ズーム）へ昇格
                    return;
                }
                // ミニチュアのクリックは「追加」ではなく左上ペインとの入れ替え。
                if (TopLeftPane() is { } topLeft && topLeft != kind)
                    PlaceWingPane(kind, topLeft, center: true, zone: null);   // 左上の位置を引き継ぎ、元の左上は袖へ
                else
                {
                    // 左上が無い／クリック対象自身が左上のときは従来どおり Main へ出してフォーカス。
                    if (!IsPaneVisible(kind))
                        SetPaneVisible(kind, true);
                    FocusPane(kind);
                }
            });
    }

    /// <summary>タイル上で最も左上（上端優先・次に左端）に表示されている可視ペイン。袖クリックの入れ替え先。
    /// まだレイアウトされておらず矩形が取れないときはツリー順の最初の可視リーフへフォールバックする。</summary>
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

    /// <summary>
    /// セッションカードの共通部分を作る。カード枠は元の縦横比で固定し、VisualBrush で <paramref name="source"/>
    /// を描く。入りきらない部分はカードの Clip で切る。
    /// </summary>
    private Border BuildCard(
        PaneKind kind, double width, Visual source, double sourceWidth, double sourceHeight,
        bool isOverview, Action onClick)
    {
        var borderBrush = (Brush)FindResource("Border");
        var accent = (Brush)FindResource("Accent");
        var onStage = isOverview && OnStage(kind);

        sourceWidth = Math.Max(sourceWidth, 1);
        sourceHeight = Math.Max(sourceHeight, 1);
        var height = Math.Round(width * sourceHeight / sourceWidth);

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

        var root = new Grid
        {
            Width = width,
            Height = height,
            ClipToBounds = true,
        };

        var thumbnail = new Border
        {
            Width = width,
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Background = new VisualBrush(source)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, sourceWidth, sourceHeight),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, width, height),
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            },
        };
        root.Children.Add(thumbnail);

        // 名札（下端のバー）
        root.Children.Add(new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromArgb(0xB4, 0x10, 0x10, 0x10)),
            Child = new TextBlock
            {
                Text = PaneLabel(kind),
                FontSize = isOverview ? 12 : 11,
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

    /// <summary>実体ペインを角丸枠に入れた舞台スロットを作る。ペイン自身のタイトルバーがあるので余計なヘッダは足さない。</summary>
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

    private void OnToggleOverview(object sender, RoutedEventArgs e) => ToggleOverview();

    /// <summary>俯瞰（全セッションのカード一覧）をトグルする。ソロ中の Ctrl+W z でも入れる。</summary>
    private void ToggleOverview()
    {
        if (!_stageActive)
            return;
        _overviewActive = !_overviewActive;
        RebuildStage();
    }

    private void OnOverviewBackgroundClick(object sender, MouseButtonEventArgs e)
    {
        // カード上のクリックは Handled 済み。素通りしてきた＝背景クリックなので俯瞰を閉じる。
        if (_overviewActive)
        {
            _overviewActive = false;
            RebuildStage();
        }
    }
}
