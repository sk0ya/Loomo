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
    /// <summary>舞台に立っているペイン。</summary>
    private PaneKind _stagePane;
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
        PaneKind.Editor, PaneKind.Terminal, PaneKind.Ai, PaneKind.Diff,
        PaneKind.Git, PaneKind.Trace, PaneKind.Browser, PaneKind.EditorSupport,
    ];

    private void OnToggleStageMode(object sender, RoutedEventArgs e)
    {
        if (_stageActive)
            ExitStageMode();
        else
            EnterStageMode();
    }

    private void EnterStageMode()
    {
        if (_stageActive)
            return;
        _stageActive = true;
        _overviewActive = false;
        _zoomedPane = null;
        _stagePane = _focusedRegion?.Pane
            ?? AllLeaves().FirstOrDefault(l => !l.Hidden)?.Kind
            ?? PaneKind.Editor;
        PaneHost.Opacity = 0;
        PaneHost.IsHitTestVisible = false;
        StageHost.Visibility = Visibility.Visible;
        // ステージ中はタイトルバーの表示トグルを畳み、舞台＋サムネイルカードの表示へ寄せる。
        PaneToggleBar.Visibility = Visibility.Collapsed;
        StageModeToggle.IsChecked = true;
        StageHost.SizeChanged += OnStageHostSizeChanged;
        RebuildStage();
        FocusPane(_stagePane);
    }

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
        StageModeToggle.IsChecked = false;
        RebuildPaneLayout();
        FocusPane(_stagePane);
    }

    /// <summary>舞台のペインを差し替える（袖・俯瞰カードのクリック先）。</summary>
    private void SetStagePane(PaneKind kind)
    {
        if (!_stageActive)
            return;
        _overviewActive = false;
        _stagePane = kind;
        RebuildStage();
    }

    /// <summary>舞台を並び順で前後のペインへ転換する（ステージ中の Ctrl+W h/j/k/l）。</summary>
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

        // 舞台：選択ペインを角丸カードで全面に立てる
        var element = _paneElements[_stagePane];
        element.Visibility = Visibility.Visible;
        var host = new Grid();
        host.SizeChanged += (_, e) => host.Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);
        host.Children.Add(element);
        var wrap = new Border
        {
            Background = (Brush)FindResource("Panel"),
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = host,
        };
        StageArea.Children.Add(wrap);

        // 袖：舞台以外のペインをサムネイルカードとして並べる。
        foreach (var kind in StageOrder.Where(k => _overviewActive || k != _stagePane))
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
        foreach (var kind in StageOrder.Where(k => _overviewActive || k != _stagePane))
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
        var onStage = isOverview && kind == _stagePane;

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
            SetStagePane(kind);
            FocusPane(kind);
        };
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
}
