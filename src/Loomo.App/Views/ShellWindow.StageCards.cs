using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: ソロモード（舞台＋袖＋俯瞰）のカード／ミニチュア描画。袖・俯瞰カードの描画元の
/// アレンジ、ライブ縮小カード（VisualBrush）、舞台スロットの生成。モード制御は ShellWindow.Stage.cs。</summary>
public partial class ShellWindow
{
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
}

