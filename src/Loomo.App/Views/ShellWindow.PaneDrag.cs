using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペインのドラッグ＆ドロップ操作（タイトルバーからの掴み・袖/舞台からのドラッグ・
/// オーバーレイ上のプレビュー描画・ドロップ確定）。レイアウトツリーの構築は <c>ShellWindow.PaneLayout.cs</c>。</summary>
public partial class ShellWindow
{
    private void OnPaneTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        // ソロモード中はタイル前提の操作（ドラッグ移動・ダブルクリックズーム）を無効化する（舞台は1枚）。
        if (_stageActive)
            return;
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<PaneKind>(tag, out var kind))
            return;

        // ダブルクリックでそのペインをズーム／復元する（tmux の zoom 相当）。
        // ただしタブや操作ボタンの上では割り込まない（タブの2連クリックやボタンの
        // ダブルクリックを横取りしないため）。
        if (e.ClickCount == 2)
        {
            if (IsWithinButton(e.OriginalSource))
                return;
            ToggleZoomFor(kind);
            e.Handled = true;
            return;
        }

        // ここでは捕捉しない（下にあるタブ／ボタンのクリックを殺さないため）。開始位置だけ控え、
        // しきい値を超えて動いたときに初めて OnPaneTitleMouseMove がドラッグを開始する。
        // Preview（トンネル）で拾うので、タブ・ボタンの上から掴んでもヘッダー全域でドラッグできる。
        _paneDragStart = e.GetPosition(null);
        _paneDragArmed = true;
    }

    private void OnPaneTitleMouseMove(object sender, MouseEventArgs e)
    {
        // ソロモード中はタイル前提の並べ替えドラッグを無効化する。
        if (_stageActive)
            return;
        if (_paneDragging || !_paneDragArmed)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            DisarmTitleDrag();
            return;
        }

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _paneDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _paneDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<PaneKind>(tag, out var kind))
        {
            // しきい値超え。BeginPaneDrag がオーバーレイへ捕捉を移すので、タブ／ボタンが
            // 押下時に握った捕捉も奪われ、ドラッグへ切り替わる（＝そのクリックは成立しない）。
            DisarmTitleDrag();
            BeginPaneDrag(kind);
        }
    }

    private void OnPaneTitleMouseUp(object sender, MouseButtonEventArgs e)
    {
        DisarmTitleDrag();
    }

    /// <summary>ドラッグ判定を解除する。</summary>
    private void DisarmTitleDrag()
    {
        _paneDragArmed = false;
        if (_dragHandle is not null)
        {
            if (ReferenceEquals(Mouse.Captured, _dragHandle))
                _dragHandle.ReleaseMouseCapture();
            _dragHandle = null;
        }
    }

    /// <summary>ヒットテストの起点要素が（ツリーを遡って）ボタンの内側にあるか。</summary>
    private static bool IsWithinButton(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase)
                return true;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    /// <summary>
    /// スナップ風のレイアウト・ドラッグを開始する。ドラッグ中は PaneHost と同セルの透明オーバーレイ
    /// （<c>PaneDragOverlay</c>）を被せ、その上でマウスを追跡＆プレビューを描画する。
    /// Popup ではなくウィンドウ内レイヤなのは、Popup が1モニタへクリップされて
    /// マルチモニタ跨ぎ最大化中に隣のモニタでプレビューが見えなくなるため。
    /// </summary>
    private void BeginPaneDrag(PaneKind source)
    {
        if (_zoomedPane is not null)
            return; // ズーム中は移動先が1枚しか見えないので並べ替えしない
        if (VisibleLeafCount() <= 1)
            return; // 1枚だけなら移動先がない

        EnsureDragOverlay();
        _dragSource = source;
        _dragTarget = null;
        _dragZone = null;
        _dragFromWing = false;
        _dragCenter = false;
        _dragSpan = false;
        _paneDragging = true;

        _dragPreview!.Visibility = Visibility.Collapsed;
        _dragTargetOutline!.Visibility = Visibility.Collapsed;
        ShowDragGhost(source);
        MoveDragGhost(Mouse.GetPosition(DragGhostLayer));

        BeginDragCapture();
    }

    /// <summary>袖（ミニチュア）からのドラッグを開始する。ドロップ先のタイル上では既存のゾーン
    /// プレビューを出し、中央なら入れ替え・端なら分割挿入する（<see cref="PlaceWingPane"/>）。
    /// レイアウトモード専用（ステージ中はタイルが無いので無効）。</summary>
    private void BeginWingDrag(PaneKind source)
    {
        if (_stageActive || VisibleLeafCount() < 1)
            return;

        EnsureDragOverlay();
        _dragSource = source;
        _dragTarget = null;
        _dragZone = null;
        _dragFromWing = true;
        _dragCenter = false;
        _dragSpan = false;
        _paneDragging = true;

        _dragPreview!.Visibility = Visibility.Collapsed;
        _dragTargetOutline!.Visibility = Visibility.Collapsed;
        ShowDragGhost(source);
        MoveDragGhost(Mouse.GetPosition(DragGhostLayer));

        BeginDragCapture();
    }

    /// <summary>ソロモードのミニチュアからのドラッグを開始する。ドロップ先は舞台1枚で、中央なら
    /// 舞台のペインを入れ替え（クリックと同じ）、端ならレイアウトモードへ切り替えて舞台のペインの
    /// 当該辺へ分割挿入する（<see cref="HandleStageDrop"/>）。ソロモード専用。</summary>
    private void BeginStageDrag(PaneKind source)
    {
        if (!_stageActive || _overviewActive)
            return;

        EnsureDragOverlay();
        _dragSource = source;
        _dragTarget = null;
        _dragZone = null;
        _dragFromWing = true;
        _dragCenter = false;
        _dragSpan = false;
        _stageDrag = true;
        _paneDragging = true;

        _dragPreview!.Visibility = Visibility.Collapsed;
        _dragTargetOutline!.Visibility = Visibility.Collapsed;
        ShowDragGhost(source);
        MoveDragGhost(Mouse.GetPosition(DragGhostLayer));

        BeginDragCapture();
    }

    /// <summary>ドラッグ用オーバーレイ（<see cref="_dragCanvas"/>）のヒットテストを有効化してから
    /// マウスキャプチャを移す。オーバーレイは <see cref="EnsureDragOverlay"/> で常時実体化済み
    /// （IsVisible=true）なので、ここで掴めば「表示直後で IsVisible=false → Mouse.Capture 失敗」という
    /// 競合（＝ミニチュアからのドラッグが初回／ときどき不発になる原因）は起きない。万一掴み損ねたとき
    /// （HWND エアスペース等）はボタン押下中だけ数フレーム再試行する。</summary>
    private void BeginDragCapture()
    {
        _dragCanvas!.IsHitTestVisible = true;   // 素通し→掴める状態へ（EndPaneDrag で false へ戻す）
        if (TryCaptureDragCanvas())
            return;

        var attempts = 0;
        void Retry()
        {
            if (!_paneDragging || Mouse.LeftButton != MouseButtonState.Pressed)
                return;                                       // ドラッグ終了／ボタンが離れた＝もう不要
            if (TryCaptureDragCanvas() || ++attempts >= 5)
                return;
            Dispatcher.BeginInvoke(new Action(Retry), System.Windows.Threading.DispatcherPriority.Input);
        }
        Dispatcher.BeginInvoke(new Action(Retry), System.Windows.Threading.DispatcherPriority.Input);
    }

    private bool TryCaptureDragCanvas()
        => ReferenceEquals(Mouse.Captured, _dragCanvas)
           || Mouse.Capture(_dragCanvas, CaptureMode.SubTree);

    private void EnsureDragOverlay()
    {
        if (_dragCanvas is not null)
            return;

        var accent = (Brush)FindResource("Accent");
        _dragTargetOutline = new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            Background = MakeTranslucent(accent, 0.10),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _dragPreview = new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(2),
            Background = MakeTranslucent(accent, 0.35),
            CornerRadius = new CornerRadius(2),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        // PaneDragOverlay は PaneHost と同セルなので、Canvas 上の座標＝PaneHost 座標になる。
        // ClipToBounds でタイル領域外（右の袖＝ミニチュア列など）へプレビューがはみ出さないようにする。
        // 既定は IsHitTestVisible=false ＝素通し（ペインのクリックを邪魔しない）。ドラッグ中だけ
        // BeginDragCapture が true にして掴む。オーバーレイ自体は常時 Visible にして「表示直後は
        // IsVisible=false で Mouse.Capture が失敗する」競合（＝初回ドラッグが不発になる原因）を断つ。
        _dragCanvas = new Canvas
        {
            Background = Brushes.Transparent,
            ClipToBounds = true,
            IsHitTestVisible = false,
        };
        _dragCanvas.Children.Add(_dragTargetOutline);
        _dragCanvas.Children.Add(_dragPreview);
        _dragCanvas.MouseMove += OnDragCanvasMouseMove;
        _dragCanvas.MouseLeftButtonUp += OnDragCanvasMouseUp;
        _dragCanvas.LostMouseCapture += OnDragCanvasLostCapture;
        PaneDragOverlay.Children.Add(_dragCanvas);
        // 実体化させて IsVisible を確定させておく（初回ドラッグ時にここで初めて生成・追加されると、
        // 同フレームで掴もうとして失敗するため、生成時点でレイアウトを通しておく）。
        PaneDragOverlay.Visibility = Visibility.Visible;
        PaneDragOverlay.UpdateLayout();
    }
}
