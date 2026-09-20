using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace sk0ya.Loomo.App.Services;

/// <summary>画像プレビューのズーム／パン操作エンジン。ScrollViewer 上のマウス操作（Ctrl+ホイールでズーム、
/// ドラッグで移動）・全体表示フィット・ズームスライダー連動を担う。表示する画像・ツールバー・キャプションは
/// <see cref="ImageEditorSupport"/> 側が持ち、ズーム変更は <see cref="ZoomChanged"/> で通知する。</summary>
internal sealed class ImageZoomController
{
    public const double MinZoom = 0.05;
    public const double MaxZoom = 16.0;
    public const double ZoomStep = 1.25;

    private readonly ScrollViewer _scroll;
    private readonly Image _image;
    private readonly Grid _imageSurface;
    private readonly TextBlock _zoomLabel;
    private readonly Slider _zoomSlider;

    private BitmapSource? _bitmap;
    private double _zoom = 1.0;
    private bool _fitToView = true;
    private bool _updatingSlider;
    private Point? _panStart;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private int _fitRequestSeq;

    /// <summary>ズーム率（1.0 = 等倍）が変わったときに発火（キャプションの寸法・%表示の更新用）。</summary>
    public event Action? ZoomChanged;

    public double Zoom => _zoom;
    public BitmapSource? Bitmap => _bitmap;

    public ImageZoomController(ScrollViewer scroll, Image image, Grid imageSurface, TextBlock zoomLabel, Slider zoomSlider)
    {
        _scroll = scroll;
        _image = image;
        _imageSurface = imageSurface;
        _zoomLabel = zoomLabel;
        _zoomSlider = zoomSlider;

        _scroll.SizeChanged += (_, _) =>
        {
            UpdateSurfaceMinimumSize();
            if (_fitToView)
                ApplyFitZoom();
        };
        _scroll.ScrollChanged += (_, e) =>
        {
            if (_fitToView && (Math.Abs(e.ViewportWidthChange) > 0.001 || Math.Abs(e.ViewportHeightChange) > 0.001))
                ApplyFitZoom();
        };
        _scroll.Loaded += (_, _) =>
        {
            if (_fitToView)
                QueueFitZoom();
        };
        _scroll.PreviewMouseWheel += OnPreviewMouseWheel;
        _scroll.PreviewMouseLeftButtonDown += OnPanStart;
        _scroll.PreviewMouseLeftButtonUp += OnPanEnd;
        _scroll.PreviewMouseMove += OnPanMove;
        _zoomSlider.ValueChanged += (_, _) =>
        {
            if (_updatingSlider)
                return;
            SetZoom(_zoomSlider.Value / 100.0, fitToView: false);
        };
    }

    /// <summary>新しい画像を表示対象にする（全体表示へリセット）。フィット適用は呼び出し側が続けて行う。</summary>
    public void SetBitmap(BitmapSource bitmap)
    {
        _bitmap = bitmap;
        _fitToView = true;
        _panStart = null;
        _image.Source = bitmap;
    }

    /// <summary>表示対象を空にする（読み込み失敗時）。</summary>
    public void Clear()
    {
        _bitmap = null;
        _image.Source = null;
    }

    // ツールバーのボタン用の薄い操作。
    public void ZoomIn() => SetZoom(_zoom * ZoomStep, fitToView: false);
    public void ZoomOut() => SetZoom(_zoom / ZoomStep, fitToView: false);
    public void ActualSize() => SetZoom(1.0, fitToView: false);
    public void FitToWindow() { _fitToView = true; ApplyFitZoom(); }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        SetZoom(e.Delta > 0 ? _zoom * 1.1 : _zoom / 1.1, fitToView: false);
        e.Handled = true;
    }

    private void OnPanStart(object sender, MouseButtonEventArgs e)
    {
        if (_bitmap is null)
            return;

        _panStart = e.GetPosition(_scroll);
        _panStartHorizontalOffset = _scroll.HorizontalOffset;
        _panStartVerticalOffset = _scroll.VerticalOffset;
        _scroll.Cursor = Cursors.SizeAll;
        _scroll.CaptureMouse();
    }

    private void OnPanMove(object sender, MouseEventArgs e)
    {
        if (_panStart is null)
            return;

        var current = e.GetPosition(_scroll);
        var delta = current - _panStart.Value;
        _scroll.ScrollToHorizontalOffset(_panStartHorizontalOffset - delta.X);
        _scroll.ScrollToVerticalOffset(_panStartVerticalOffset - delta.Y);
    }

    private void OnPanEnd(object sender, MouseButtonEventArgs e)
    {
        _panStart = null;
        _scroll.Cursor = Cursors.Arrow;
        _scroll.ReleaseMouseCapture();
    }

    public void ApplyFitZoom()
    {
        if (_bitmap is null)
            return;

        if (_scroll.ViewportWidth <= 1 || _scroll.ViewportHeight <= 1)
        {
            if (_scroll.IsVisible)
                QueueFitZoom();
            return;
        }

        var zoom = ImageFitMath.CalculateFitZoom(
            _scroll.ViewportWidth,
            _scroll.ViewportHeight,
            _bitmap.Width,
            _bitmap.Height);
        SetZoom(zoom, fitToView: true);
    }

    public void UpdateSurfaceMinimumSize()
    {
        _imageSurface.MinWidth = Math.Max(1, _scroll.ViewportWidth);
        _imageSurface.MinHeight = Math.Max(1, _scroll.ViewportHeight);
    }

    public void QueueFitZoom()
    {
        var seq = ++_fitRequestSeq;
        _ = _scroll.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (seq == _fitRequestSeq && _fitToView)
                ApplyFitZoom();
        }));
    }

    public void SetZoom(double zoom, bool fitToView)
    {
        _fitToView = fitToView;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);

        _scroll.HorizontalScrollBarVisibility = fitToView
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
        _scroll.VerticalScrollBarVisibility = fitToView
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;

        if (_bitmap is not null)
        {
            _image.Width = Math.Max(1, _bitmap.Width * _zoom);
            _image.Height = Math.Max(1, _bitmap.Height * _zoom);
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            _image.InvalidateMeasure();
            _image.InvalidateArrange();
            _image.UpdateLayout();
        }

        _zoomLabel.Text = $"{Math.Round(_zoom * 100):0}%";

        _updatingSlider = true;
        _zoomSlider.Value = Math.Clamp(_zoom * 100, _zoomSlider.Minimum, _zoomSlider.Maximum);
        _updatingSlider = false;

        ZoomChanged?.Invoke();

        if (fitToView)
        {
            _ = _scroll.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                CorrectFitAfterLayout();
                _scroll.ScrollToHorizontalOffset(0);
                _scroll.ScrollToVerticalOffset(0);
            }));
        }
    }

    private void CorrectFitAfterLayout()
    {
        if (!_fitToView || _image is null || _bitmap is null)
            return;

        var maxWidth = Math.Max(1, _scroll.ViewportWidth - ImageFitMath.SafetyInset);
        var maxHeight = Math.Max(1, _scroll.ViewportHeight - ImageFitMath.SafetyInset);
        if (_image.ActualWidth <= maxWidth && _image.ActualHeight <= maxHeight)
            return;

        var widthRatio = maxWidth / Math.Max(1, _image.ActualWidth);
        var heightRatio = maxHeight / Math.Max(1, _image.ActualHeight);
        var correction = Math.Min(widthRatio, heightRatio);
        if (correction >= 1.0)
            return;

        _zoom = Math.Clamp(_zoom * correction, MinZoom, MaxZoom);
        _image.Width = Math.Max(1, _bitmap.Width * _zoom);
        _image.Height = Math.Max(1, _bitmap.Height * _zoom);
        _zoomLabel.Text = $"{Math.Round(_zoom * 100):0}%";
        _updatingSlider = true;
        _zoomSlider.Value = Math.Clamp(_zoom * 100, _zoomSlider.Minimum, _zoomSlider.Maximum);
        _updatingSlider = false;

        ZoomChanged?.Invoke();
    }
}
