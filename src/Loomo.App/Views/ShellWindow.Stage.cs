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
/// ShellWindow: ステージモード（舞台＋袖）。1ペインを全面の「舞台」に立て、残りのペインは
/// 右端の「袖」でペインを VisualBrush として縮小表示する。袖カードは実コントロールを
/// 子に持たず、元の表示を描くだけなので、袖表示のためにペインを動かさない。
/// タイル表示（PaneHost）とは表示の差し替えだけで切替わり、レイアウトツリー（_root）には
/// 一切触れない — 解除すれば元のタイル配置・比率へそのまま戻る。
/// 「俯瞰」は全セッションをカードで一望する Exposé 風レイヤ（クリックで舞台へダイブ）。
/// ステージ中に <c>FocusPane</c> が呼ばれると対象が自動で舞台に立つので、AI がファイルを
/// 開いた・差分を出した等の既存フローがそのまま「舞台の自動転換」になる。
/// </summary>
public partial class ShellWindow
{
    // ===== ステージモード（舞台＋袖＋俯瞰） =====

    /// <summary>ステージモード中か。中は RebuildPaneLayout がステージの組み直しへ委譲される。</summary>
    private bool _stageActive;
    /// <summary>舞台に立っている主役ペイン。</summary>
    private PaneKind _stagePane;
    /// <summary>配置モードのサブ（最大2枚・順序＝Sub1,Sub2）。空なら従来の単一ステージ。</summary>
    private readonly List<StageSub> _stageSubs = new();

    /// <summary>配置モード中か（サブが1枚以上立っている）。</summary>
    private bool ProgramActive => _stageSubs.Count > 0;
    /// <summary>舞台に立っているペイン（主役＋サブ）。</summary>
    private IEnumerable<PaneKind> OnStagePanes() => new[] { _stagePane }.Concat(_stageSubs.Select(s => s.Kind));
    /// <summary>指定ペインが舞台に立っているか。</summary>
    private bool OnStage(PaneKind kind) => _stagePane == kind || _stageSubs.Any(s => s.Kind == kind);
    /// <summary>現在の舞台状態を純ロジック用スナップショットへ。</summary>
    private StageState CurrentStage() => new(_stagePane, _stageSubs.ToList());
    /// <summary>純ロジックの結果を舞台状態へ書き戻す。</summary>
    private void SetStageState(StageState state)
    {
        _stagePane = state.Main;
        _stageSubs.Clear();
        _stageSubs.AddRange(state.Subs);
    }

    /// <summary>スロット境界の参照（ドロップオーバーレイのゾーン配置に使う）。</summary>
    private FrameworkElement? _mainSlotElement;
    private readonly List<(int Index, FrameworkElement Element)> _subSlotElements = new();

    /// <summary>右ドック列／下ドック行が占める割合（リサイズで更新・0 なら既定）。</summary>
    private const double DefaultStageFraction = 0.34;
    private double _stageRightFraction = DefaultStageFraction;
    private double _stageBottomFraction = DefaultStageFraction;
    /// <summary>リサイズ後にトラック実寸を比率へ取り込むためのグリッド／並び参照。</summary>
    private Grid? _stageOuterGrid;
    private Grid? _stageTopGrid;
    private Grid? _stageRightGrid;
    private Grid? _stageBottomGrid;
    private readonly List<StageSub> _stageRightOrder = new();
    private readonly List<StageSub> _stageBottomOrder = new();
    /// <summary>ドラッグ中の発生元スロット（袖／主役／サブ）。</summary>
    private StageSlot? _stageDragOrigin;
    private Point _stageDragStart;
    private bool _stageDragArmed;
    private bool _suppressNextWingClick;
    /// <summary>舞台スロットのドラッグであることを示す DataObject フォーマット。</summary>
    private const string StagePaneDragFormat = "Loomo.StagePane";
    /// <summary>俯瞰（全カード一望）レイヤを表示中か。</summary>
    private bool _overviewActive;
    /// <summary>リサイズ追従のデバウンス用タイマー。発火時に仮想寸法が変わっていたら組み直す。</summary>
    private DispatcherTimer? _stageResizeTimer;
    /// <summary>直近の構築に使った仮想キャンバス寸法（＝舞台の実寸）。</summary>
    private Size _stageBuiltSize;
    /// <summary>袖カードの VisualBrush が参照する、舞台サイズにレイアウト済みの非表示ホスト。</summary>
    private readonly Dictionary<PaneKind, Grid> _stageThumbnailHosts = new();
    /// <summary>袖カードの幅。高さは舞台の縦横比から導出される。</summary>
    private const double WingCardWidth = 180;
    /// <summary>俯瞰カードの幅。</summary>
    private const double OverviewCardWidth = 320;
    /// <summary>袖の列（カード＋余白＋スクロールバー）が占める幅の見積もり。舞台幅の算出に使う。</summary>
    private const double WingColumnReserve = 210;
    /// <summary>袖カードの待機時不透明度（暗がりで生きて待っている感を出す）。</summary>
    private const double WingRestOpacity = 0.72;

    /// <summary>袖・俯瞰での並び順（よく使うものから）。</summary>
    private static readonly PaneKind[] StageOrder =
    [
        PaneKind.Editor, PaneKind.Terminal, PaneKind.Browser, PaneKind.EditorSupport,
        PaneKind.Git, PaneKind.Diff, PaneKind.Ai,
        // AI トレースは通常セッションとしては表示しない。
        // PaneKind.Trace,
    ];

    private void OnToggleStageMode(object sender, RoutedEventArgs e)
    {
        if (_stageActive)
            ExitStageMode();
        else
            EnterStageMode();
    }

    /// <summary>配置をやめてステージ（舞台1枚）へ戻す。サブを全部降ろすだけで主役は据え置き。
    /// タイトルバーの配置ドロップダウンの「なし」から呼ぶ。</summary>
    private void StopProgram()
    {
        if (!_stageActive || !ProgramActive)
            return;
        _stageSubs.Clear();
        _activeProgramName = null;
        UpdateProgramButton();
        RebuildStage();
        FocusPane(_stagePane);
        SaveActiveWorkspaceSnapshot();
    }

    private void EnterStageMode()
        => EnterStageMode(null);

    private void EnterStageMode(PaneKind? pane)
    {
        if (_stageActive)
            return;
        _stageActive = true;
        _overviewActive = false;
        _zoomedPane = null;
        _stageSubs.Clear();   // 入場は単一ステージから（配置は袖ドラッグ／保存配置で立てる）
        _stageRightFraction = _stageBottomFraction = DefaultStageFraction;
        _stagePane = pane is { } requested && _paneElements.ContainsKey(requested)
            ? requested
            : _focusedRegion?.Pane
            ?? AllLeaves().FirstOrDefault(l => !l.Hidden)?.Kind
            ?? PaneKind.Editor;
        PaneHost.Opacity = 0;
        PaneHost.IsHitTestVisible = false;
        StageHost.Visibility = Visibility.Visible;
        // ステージ中はタイトルバーの表示トグルを畳み、舞台＋サムネイルカードの表示へ寄せる。
        PaneToggleBar.Visibility = Visibility.Collapsed;
        StageHost.SizeChanged += OnStageHostSizeChanged;
        UpdateProgramButton();
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
        _stageSubs.Clear();
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
        UpdateProgramButton();
    }

    /// <summary>
    /// ワークスペース復元時、ペインレイアウト適用前にステージ表示状態だけ先に立てる。
    /// これにより ApplyPaneLayout がタイル表示を描かず、最初からステージとして組み直す。
    /// </summary>
    private void PrepareStageSnapshot(StageSnapshot? snapshot)
    {
        ClearStageModeForWorkspaceSwitch();
        snapshot ??= StageSnapshot.Default();

        if (snapshot?.IsActive != true)
            return;

        _stageActive = true;
        _overviewActive = snapshot.Overview;   // 俯瞰を開いたまま離れたら俯瞰のまま戻る
        _zoomedPane = null;
        _stagePane = snapshot.Pane is { } requested && _paneElements.ContainsKey(requested)
            ? requested
            : PaneKind.Editor;
        // 配置モードのサブを復元（主役と重複・未知ペインは捨てる）。
        _stageSubs.Clear();
        foreach (var sub in snapshot.Subs)
            if (sub.Kind != _stagePane && _paneElements.ContainsKey(sub.Kind)
                && _stageSubs.All(s => s.Kind != sub.Kind) && _stageSubs.Count < StageProgramLogic.MaxSubs)
                _stageSubs.Add(new StageSub(sub.Kind, sub.Dock, sub.Weight));
        _stageRightFraction = snapshot.RightFraction;
        _stageBottomFraction = snapshot.BottomFraction;
        _activeProgramName = snapshot.ProgramName;
        PaneHost.Opacity = 0;
        PaneHost.IsHitTestVisible = false;
        StageHost.Visibility = Visibility.Visible;
        PaneToggleBar.Visibility = Visibility.Collapsed;
        StageHost.SizeChanged += OnStageHostSizeChanged;
        UpdateProgramButton();
    }

    /// <summary>タブ実体の復元後に、ステージの内容とフォーカスを確定する。</summary>
    private void CompleteStageSnapshotRestore()
    {
        if (!_stageActive)
            return;

        RebuildStage();
        // 組み直し直後は舞台の要素がまだレイアウト前（IsVisible=false）で Focus が失敗し得るため、
        // レイアウト確定後（Loaded 優先度）にフォーカスを入れる。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_stageActive)
                FocusPane(_stagePane);
        }));
    }

    private void ExitStageMode()
    {
        if (!_stageActive)
            return;
        _stageActive = false;
        _overviewActive = false;
        _stageSubs.Clear();
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
        UpdateProgramButton();
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
        MarkPaneActivitySeen(kind);   // 舞台に立った＝目に入ったので未確認バッジを流す
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>
    /// 舞台のものを切り替える（Ctrl+T）。ステージ未開始ならまず開始して現ペインを舞台へ立て、
    /// 開始済みなら並び順で前後のペインへ転換する＝1キーで舞台入り→歩いて回せる。
    /// </summary>
    private void CycleOrEnterStage(int direction)
    {
        if (_stageActive)
            CycleStage(direction);
        else
            EnterStageMode();
    }

    /// <summary>舞台を並び順で前後のペインへ転換する（ステージ中の Ctrl+T / Ctrl+W h/j/k/l）。
    /// 配置モード中は主役を固定したまま、末尾サブを舞台外ペインの輪で送る。</summary>
    private void CycleStage(int direction)
    {
        if (ProgramActive)
        {
            var before = _stageSubs[^1].Kind;
            SetStageState(StageProgramLogic.NextSubCycle(CurrentStage(), StageOrder, direction));
            var after = _stageSubs[^1].Kind;
            RebuildStage();
            if (after != before)
            {
                MarkPaneActivitySeen(after);
                FocusPane(after);
            }
            SaveActiveWorkspaceSnapshot();
            return;
        }

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

    /// <summary>ステージモードの画面を組み直す（舞台1枚＋袖カード、または俯瞰カード一覧）。</summary>
    private void RebuildStage()
    {
        if (!_stageActive)
            return;

        DetachPaneElements();
        StageArea.Children.Clear();
        StageSourceArea.Children.Clear();
        WingStrip.Children.Clear();
        OverviewPanel.Children.Clear();
        _stageThumbnailHosts.Clear();
        _stageActivityBadges.Clear();
        _mainSlotElement = null;
        _subSlotElements.Clear();
        _stageOuterGrid = _stageTopGrid = _stageRightGrid = _stageBottomGrid = null;
        _stageRightOrder.Clear();
        _stageBottomOrder.Clear();

        var virtualSize = StageVirtualSize();
        _stageBuiltSize = virtualSize;
        BuildStageThumbnailSources(virtualSize);

        if (_overviewActive)
        {
            OverviewLayer.Visibility = Visibility.Visible;
            foreach (var kind in StageOrder)
                OverviewPanel.Children.Add(BuildSessionCard(
                    kind, OverviewCardWidth, virtualSize, isOverview: true));
            ScheduleBrowserRealize(_activeBrowserTab);
            return;
        }

        OverviewLayer.Visibility = Visibility.Collapsed;

        // 舞台：単一ステージなら主役を全面に、配置モードなら主役＋サブを格子に立てる。
        if (ProgramActive)
            StageArea.Children.Add(BuildProgramStage());
        else
            StageArea.Children.Add(BuildLiveSlot(_stagePane, isMain: true, subIndex: -1));

        // 袖：舞台に立っていないペインをサムネイルカードとして並べる。
        foreach (var kind in StageOrder.Where(k => !OnStage(k)))
            WingStrip.Children.Add(BuildSessionCard(
                kind, WingCardWidth, virtualSize, isOverview: false));

        ScheduleBrowserRealize(_activeBrowserTab);

        // 一時診断：LOOMO_STAGE_DEBUG=1 でレイアウト確定後の実寸を %TEMP% へ書き出す
        if (Environment.GetEnvironmentVariable("LOOMO_STAGE_DEBUG") == "1")
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(DumpStageDiagnostics));
    }

    /// <summary>袖カードの描画元を舞台サイズでレイアウトする。</summary>
    private void BuildStageThumbnailSources(Size virtualSize)
    {
        foreach (var kind in StageOrder.Where(k => _overviewActive || !OnStage(k)))
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

    /// <summary>
    /// セッションカードを作る。カード枠は舞台比率で固定し、VisualBrush で元ペインを描く。
    /// 入りきらない部分はカードの Clip で切る。
    /// </summary>
    private Border BuildSessionCard(
        PaneKind kind, double width, Size virtualSize, bool isOverview)
    {
        var borderBrush = (Brush)FindResource("Border");
        var accent = (Brush)FindResource("Accent");
        var onStage = isOverview && OnStage(kind);

        var source = _stageThumbnailHosts.TryGetValue(kind, out var host)
            ? host
            : _paneElements[kind];
        var sourceWidth = Math.Max(virtualSize.Width, 1);
        var sourceHeight = Math.Max(virtualSize.Height, 1);
        var scale = width / sourceWidth;
        var height = Math.Round(virtualSize.Height * (width / virtualSize.Width));
        var scaledHeight = sourceHeight * scale;

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
            ToolTip = $"{PaneLabel(kind)} を舞台へ",
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
                Viewport = new Rect(0, 0, width, scaledHeight),
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
            e.Handled = true; // 俯瞰レイヤの背景クリック（＝俯瞰を閉じる）と区別する
            if (_suppressNextWingClick)   // 直前にドラッグが起きたクリックは無視
            {
                _suppressNextWingClick = false;
                return;
            }
            SetStagePane(kind);
            FocusPane(kind);
        };
        // 俯瞰でない袖カードは、舞台スロットへドラッグして入れ替え／配置化できるドラッグ元にする。
        if (!isOverview)
            ArmStageDrag(card, () => new WingSlot(kind));
        return card;
    }

    private void OnToggleOverview(object sender, RoutedEventArgs e) => ToggleOverview();

    /// <summary>俯瞰（全セッションのカード一覧）をトグルする。ステージ中の Ctrl+W z でも入れる。</summary>
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

    // ===== 配置モード：舞台の複数立て（主役＋サブ） =====

    /// <summary>配置モードの舞台を組む：主役（左・大）＋右ドックのサブ列、下ドックのサブ行。
    /// スロット間に GridSplitter を挟み、ドラッグで比率を変えられる（比率は永続化）。</summary>
    private FrameworkElement BuildProgramStage()
    {
        var bottomSubs = _stageSubs.Where(s => s.Dock == StageDock.Bottom).ToList();
        var rightSubs = _stageSubs.Where(s => s.Dock == StageDock.Right).ToList();
        _stageRightOrder.AddRange(rightSubs);
        _stageBottomOrder.AddRange(bottomSubs);

        var rightFrac = StageFraction(_stageRightFraction);
        var bottomFrac = StageFraction(_stageBottomFraction);

        // 上段：主役（左・大）＋右サブ列。列スプリッターで主役／右列を伸縮。
        var top = new Grid();
        _stageTopGrid = top;
        var mainSlot = BuildLiveSlot(_stagePane, isMain: true, subIndex: -1);
        if (rightSubs.Count > 0)
        {
            AddTrack(top, true, new GridLength(1 - rightFrac, GridUnitType.Star), 120);
            Grid.SetColumn(mainSlot, 0);
            top.Children.Add(mainSlot);

            AddTrack(top, true, new GridLength(SplitterThickness));
            var splitter = NewStageSplitter(cols: true);
            Grid.SetColumn(splitter, 1);
            top.Children.Add(splitter);

            AddTrack(top, true, new GridLength(rightFrac, GridUnitType.Star), 120);
            var rightCol = BuildStackedSubs(rightSubs, cols: false);
            Grid.SetColumn(rightCol, 2);
            top.Children.Add(rightCol);
        }
        else
        {
            AddTrack(top, true, new GridLength(1, GridUnitType.Star));
            Grid.SetColumn(mainSlot, 0);
            top.Children.Add(mainSlot);
        }

        if (bottomSubs.Count == 0)
            return top;

        // 縦：上段＋下サブ行。行スプリッターで上下を伸縮。
        var outer = new Grid();
        _stageOuterGrid = outer;
        AddTrack(outer, false, new GridLength(1 - bottomFrac, GridUnitType.Star), 100);
        Grid.SetRow(top, 0);
        outer.Children.Add(top);

        AddTrack(outer, false, new GridLength(SplitterThickness));
        var hsplitter = NewStageSplitter(cols: false);
        Grid.SetRow(hsplitter, 1);
        outer.Children.Add(hsplitter);

        AddTrack(outer, false, new GridLength(bottomFrac, GridUnitType.Star), 100);
        var bottomRow = BuildStackedSubs(bottomSubs, cols: true);
        Grid.SetRow(bottomRow, 2);
        outer.Children.Add(bottomRow);

        return outer;
    }

    /// <summary>同じドックの複数サブを軸（cols=横並び／false=縦積み）に並べ、間にスプリッターを挟む。</summary>
    private Grid BuildStackedSubs(List<StageSub> subs, bool cols)
    {
        var grid = new Grid();
        if (cols)
            _stageBottomGrid = grid;
        else
            _stageRightGrid = grid;

        var min = cols ? 120.0 : 80.0;
        for (var i = 0; i < subs.Count; i++)
        {
            if (i > 0)
            {
                AddTrack(grid, cols, new GridLength(SplitterThickness));
                var splitter = NewStageSplitter(cols);
                SetTrack(splitter, cols, i * 2 - 1);
                grid.Children.Add(splitter);
            }
            var weight = subs[i].Weight <= 0 ? 1 : subs[i].Weight;
            AddTrack(grid, cols, new GridLength(weight, GridUnitType.Star), min);
            var slot = BuildLiveSlot(subs[i].Kind, isMain: false, subIndex: _stageSubs.IndexOf(subs[i]));
            SetTrack(slot, cols, i * 2);
            grid.Children.Add(slot);
        }
        return grid;
    }

    /// <summary>配置スロット間のスプリッター。ドラッグ完了で実寸を比率へ取り込み永続化する。</summary>
    private GridSplitter NewStageSplitter(bool cols)
    {
        var border = (Brush)FindResource("Border");
        var accent = (Brush)FindResource("Accent");
        var splitter = new GridSplitter
        {
            Width = cols ? SplitterThickness : double.NaN,
            Height = cols ? double.NaN : SplitterThickness,
            ResizeDirection = cols ? GridResizeDirection.Columns : GridResizeDirection.Rows,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = border,
            Cursor = cols ? Cursors.SizeWE : Cursors.SizeNS,
            ToolTip = "ドラッグでリサイズ",
        };
        splitter.MouseEnter += (_, _) => splitter.Background = accent;
        splitter.MouseLeave += (_, _) => splitter.Background = border;
        splitter.DragCompleted += (_, _) => { CaptureStageSizes(); SaveActiveWorkspaceSnapshot(); };
        return splitter;
    }

    /// <summary>リサイズ後、各グリッドのトラック実寸を比率／サブ Weight へ取り込む（次の組み直しで保つ）。</summary>
    private void CaptureStageSizes()
    {
        if (_stageTopGrid is { ColumnDefinitions.Count: >= 3 } top)
        {
            var sum = top.ColumnDefinitions[0].ActualWidth + top.ColumnDefinitions[2].ActualWidth;
            if (sum > 0)
                _stageRightFraction = top.ColumnDefinitions[2].ActualWidth / sum;
        }
        if (_stageOuterGrid is { RowDefinitions.Count: >= 3 } outer)
        {
            var sum = outer.RowDefinitions[0].ActualHeight + outer.RowDefinitions[2].ActualHeight;
            if (sum > 0)
                _stageBottomFraction = outer.RowDefinitions[2].ActualHeight / sum;
        }
        CaptureStackedWeights(_stageRightGrid, _stageRightOrder, cols: false);
        CaptureStackedWeights(_stageBottomGrid, _stageBottomOrder, cols: true);
    }

    private void CaptureStackedWeights(Grid? grid, List<StageSub> order, bool cols)
    {
        if (grid is null || order.Count < 2)
            return;
        for (var i = 0; i < order.Count; i++)
        {
            var track = i * 2;
            var size = cols
                ? track < grid.ColumnDefinitions.Count ? grid.ColumnDefinitions[track].ActualWidth : 0
                : track < grid.RowDefinitions.Count ? grid.RowDefinitions[track].ActualHeight : 0;
            if (size <= 0)
                continue;
            var idx = _stageSubs.IndexOf(order[i]);
            if (idx >= 0)
                _stageSubs[idx] = _stageSubs[idx] with { Weight = size };
        }
    }

    /// <summary>永続化された割合を妥当な範囲へ（0／範囲外は既定へ寄せる）。</summary>
    private static double StageFraction(double value)
        => value <= 0 ? DefaultStageFraction : Math.Clamp(value, 0.12, 0.88);

    /// <summary>実体ペインを角丸枠に入れたスロットを作る。ペイン自身のタイトルバーを取手／降ろしに使うので
    /// 余計なヘッダは足さない（主役は枠をアクセント色にして区別）。</summary>
    private Border BuildLiveSlot(PaneKind kind, bool isMain, int subIndex)
    {
        var element = _paneElements[kind];
        element.Visibility = Visibility.Visible;

        var host = new Grid();
        host.SizeChanged += (_, e) => host.Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);
        host.Children.Add(element);

        var wrap = new Border
        {
            Background = (Brush)FindResource("Panel"),
            BorderBrush = (Brush)FindResource(isMain ? "Accent" : "Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = host,
        };

        if (isMain)
            _mainSlotElement = wrap;
        else
            _subSlotElements.Add((subIndex, wrap));
        return wrap;
    }

    /// <summary>ペイン種別から、その実体が今いる舞台スロットを返す（ドラッグ元の決定に使う）。</summary>
    private StageSlot SlotForKind(PaneKind kind)
    {
        if (kind == _stagePane)
            return new MainSlot();
        var index = _stageSubs.FindIndex(s => s.Kind == kind);
        return index >= 0 ? new SubSlot(index) : new WingSlot(kind);
    }

    /// <summary>袖（舞台外）ペインを新しいサブとして迎える（配置モードへの突入も兼ねる）。</summary>
    private void AddSub(PaneKind kind, StageDock dock)
    {
        if (!_stageActive)
            return;
        SetStageState(StageProgramLogic.AddSub(CurrentStage(), kind, dock));
        UpdateProgramButton();
        RebuildStage();
        MarkPaneActivitySeen(kind);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>サブを降ろす。0 件になれば配置モード終了＝主役だけの単一ステージへ戻る。</summary>
    private void RemoveSub(PaneKind kind)
    {
        if (!_stageActive)
            return;
        SetStageState(StageProgramLogic.RemoveSub(CurrentStage(), kind));
        if (!ProgramActive)
            _activeProgramName = null;   // Main 一人 → 配置終了
        UpdateProgramButton();
        RebuildStage();
        FocusPane(_stagePane);
        SaveActiveWorkspaceSnapshot();
    }

    // ===== ドラッグ＆ドロップ（袖／主役／サブの入れ替え） =====

    /// <summary>要素を舞台スロットのドラッグ元にする（クリックと区別する小さなしきい値ガード付き）。</summary>
    private void ArmStageDrag(UIElement source, Func<StageSlot> origin)
    {
        source.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _stageDragStart = e.GetPosition(null);
            _stageDragArmed = true;
            _suppressNextWingClick = false;
        };
        source.PreviewMouseMove += (_, e) =>
        {
            if (!_stageDragArmed || e.LeftButton != MouseButtonState.Pressed)
                return;
            var p = e.GetPosition(null);
            if (Math.Abs(p.X - _stageDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(p.Y - _stageDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            _stageDragArmed = false;
            _suppressNextWingClick = true;
            BeginStageDrag(source, origin());
        };
        source.PreviewMouseLeftButtonUp += (_, _) => _stageDragArmed = false;
    }

    private void BeginStageDrag(UIElement source, StageSlot origin)
    {
        _stageDragOrigin = origin;
        ShowStageDropOverlay();
        try
        {
            DragDrop.DoDragDrop(source, new DataObject(StagePaneDragFormat, true), DragDropEffects.Move);
        }
        finally
        {
            _stageDragOrigin = null;
            HideStageDropOverlay();
        }
    }

    /// <summary>ドラッグ中だけ、スロット格子に重なる透明ドロップゾーンを立てる（ライブ操作の邪魔をしない）。</summary>
    private void ShowStageDropOverlay()
    {
        StageDropOverlay.Children.Clear();
        var canvas = new Canvas();
        StageDropOverlay.Children.Add(canvas);

        if (_mainSlotElement is { } mainEl && BoundsIn(StageDropOverlay, mainEl) is { } mainRect)
        {
            AddDropZone(canvas, mainRect, new MainSlot());   // 中央＝主役入れ替え
            // 主役の右端／下端＝サブ追加（空きがあるときだけ）。
            if (_stageSubs.Count < StageProgramLogic.MaxSubs)
            {
                var edge = Math.Min(72, Math.Min(mainRect.Width, mainRect.Height) / 3);
                AddDropZone(canvas, new Rect(mainRect.Right - edge, mainRect.Top, edge, mainRect.Height),
                    new NewSubSlot(StageDock.Right));
                AddDropZone(canvas, new Rect(mainRect.Left, mainRect.Bottom - edge, mainRect.Width, edge),
                    new NewSubSlot(StageDock.Bottom));
            }
        }

        foreach (var (index, el) in _subSlotElements)
            if (BoundsIn(StageDropOverlay, el) is { } r)
                AddDropZone(canvas, r, new SubSlot(index));

        StageDropOverlay.Visibility = Visibility.Visible;
    }

    private void HideStageDropOverlay()
    {
        StageDropOverlay.Visibility = Visibility.Collapsed;
        StageDropOverlay.Children.Clear();
    }

    private static Rect? BoundsIn(Visual relativeTo, FrameworkElement element)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return null;
        var topLeft = element.TransformToVisual(relativeTo).Transform(new Point(0, 0));
        return new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight));
    }

    private void AddDropZone(Canvas canvas, Rect rect, StageSlot target)
    {
        var zone = new Border
        {
            Width = Math.Max(0, rect.Width),
            Height = Math.Max(0, rect.Height),
            Background = Brushes.Transparent,   // Transparent でもヒットする（null は不可）
            BorderBrush = (Brush)FindResource("Accent"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            AllowDrop = true,
        };
        Canvas.SetLeft(zone, rect.X);
        Canvas.SetTop(zone, rect.Y);

        zone.DragEnter += (_, e) =>
        {
            if (!e.Data.GetDataPresent(StagePaneDragFormat))
                return;
            zone.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x4F, 0x9D, 0xFF));
            zone.BorderThickness = new Thickness(2);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        };
        zone.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(StagePaneDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        zone.DragLeave += (_, _) =>
        {
            zone.Background = Brushes.Transparent;
            zone.BorderThickness = new Thickness(0);
        };
        zone.Drop += (_, e) =>
        {
            e.Handled = true;
            if (e.Data.GetDataPresent(StagePaneDragFormat))
                HandleStageDrop(target);
        };
        canvas.Children.Add(zone);
    }

    private void HandleStageDrop(StageSlot target)
    {
        if (_stageDragOrigin is not { } from)
            return;

        var state = CurrentStage();
        var next = target is NewSubSlot ns && from is WingSlot wing
            ? StageProgramLogic.AddSub(state, wing.Kind, ns.Dock)
            : StageProgramLogic.ApplySwap(state, from, target);

        SetStageState(next);
        if (!ProgramActive)
            _activeProgramName = null;
        UpdateProgramButton();
        HideStageDropOverlay();
        RebuildStage();
        FocusPane(_stagePane);
        SaveActiveWorkspaceSnapshot();
    }
}
