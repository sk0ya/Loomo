using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace sk0ya.Loomo.App.Services;

/// <summary>ImageEditorSupport のズーム/パン操作とアクション（マウスホイール・ドラッグ移動・フィット/等倍、
/// 画像/パスのコピー・別名保存・キャプション更新）。ビュー構築・読み込みは ImageEditorSupport.cs。</summary>
public sealed partial class ImageEditorSupport
{
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        SetZoom(e.Delta > 0 ? _zoom * 1.1 : _zoom / 1.1, fitToView: false);
        e.Handled = true;
    }

    private void OnPanStart(object sender, MouseButtonEventArgs e)
    {
        if (_bitmap is null || _scroll is null)
            return;

        _panStart = e.GetPosition(_scroll);
        _panStartHorizontalOffset = _scroll.HorizontalOffset;
        _panStartVerticalOffset = _scroll.VerticalOffset;
        _scroll.Cursor = Cursors.SizeAll;
        _scroll.CaptureMouse();
    }

    private void OnPanMove(object sender, MouseEventArgs e)
    {
        if (_panStart is null || _scroll is null)
            return;

        var current = e.GetPosition(_scroll);
        var delta = current - _panStart.Value;
        _scroll.ScrollToHorizontalOffset(_panStartHorizontalOffset - delta.X);
        _scroll.ScrollToVerticalOffset(_panStartVerticalOffset - delta.Y);
    }

    private void OnPanEnd(object sender, MouseButtonEventArgs e)
    {
        if (_scroll is null)
            return;

        _panStart = null;
        _scroll.Cursor = Cursors.Arrow;
        _scroll.ReleaseMouseCapture();
    }

    private void ApplyFitZoom()
    {
        if (_bitmap is null || _scroll is null)
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

    private void UpdateSurfaceMinimumSize()
    {
        if (_scroll is null || _imageSurface is null)
            return;

        _imageSurface.MinWidth = Math.Max(1, _scroll.ViewportWidth);
        _imageSurface.MinHeight = Math.Max(1, _scroll.ViewportHeight);
    }

    private void QueueFitZoom()
    {
        if (_scroll is null)
            return;

        var seq = ++_fitRequestSeq;
        _ = _scroll.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (seq == _fitRequestSeq && _fitToView)
                ApplyFitZoom();
        }));
    }

    private void SetZoom(double zoom, bool fitToView)
    {
        _fitToView = fitToView;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);

        if (_scroll is not null)
        {
            _scroll.HorizontalScrollBarVisibility = fitToView
                ? ScrollBarVisibility.Disabled
                : ScrollBarVisibility.Auto;
            _scroll.VerticalScrollBarVisibility = fitToView
                ? ScrollBarVisibility.Disabled
                : ScrollBarVisibility.Auto;
        }

        if (_image is not null && _bitmap is not null)
        {
            _image.Width = Math.Max(1, _bitmap.Width * _zoom);
            _image.Height = Math.Max(1, _bitmap.Height * _zoom);
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            _image.InvalidateMeasure();
            _image.InvalidateArrange();
            _image.UpdateLayout();
        }

        if (_zoomLabel is not null)
            _zoomLabel.Text = $"{Math.Round(_zoom * 100):0}%";

        if (_zoomSlider is not null)
        {
            _updatingSlider = true;
            _zoomSlider.Value = Math.Clamp(_zoom * 100, _zoomSlider.Minimum, _zoomSlider.Maximum);
            _updatingSlider = false;
        }

        UpdateCaptionDetails();

        if (fitToView && _scroll is not null)
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
        if (!_fitToView || _scroll is null || _image is null || _bitmap is null)
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
        if (_zoomLabel is not null)
            _zoomLabel.Text = $"{Math.Round(_zoom * 100):0}%";
        if (_zoomSlider is not null)
        {
            _updatingSlider = true;
            _zoomSlider.Value = Math.Clamp(_zoom * 100, _zoomSlider.Minimum, _zoomSlider.Maximum);
            _updatingSlider = false;
        }

        UpdateCaptionDetails();
    }

    private void CopyImage()
    {
        if (_bitmap is null)
            return;

        try
        {
            Clipboard.SetImage(_bitmap);
            SetCaption($"{Path.GetFileName(_filePath)}\n画像をコピーしました。");
        }
        catch
        {
            SetCaption($"{Path.GetFileName(_filePath)}\n画像をコピーできませんでした。");
        }
    }

    private void CopyPath()
    {
        if (string.IsNullOrEmpty(_filePath))
            return;

        try
        {
            Clipboard.SetText(_filePath);
            SetCaption($"{Path.GetFileName(_filePath)}\nパスをコピーしました。");
        }
        catch
        {
            SetCaption($"{Path.GetFileName(_filePath)}\nパスをコピーできませんでした。");
        }
    }

    private void SaveAs()
    {
        if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
            return;

        var ext = Path.GetExtension(_filePath);
        var dialog = new SaveFileDialog
        {
            FileName = Path.GetFileName(_filePath),
            DefaultExt = ext,
            Filter = $"{ext.TrimStart('.').ToUpperInvariant()} image|*{ext}|All files|*.*",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            File.Copy(_filePath, dialog.FileName, overwrite: true);
            SetCaption($"{Path.GetFileName(_filePath)}\n保存しました: {dialog.FileName}");
        }
        catch
        {
            SetCaption($"{Path.GetFileName(_filePath)}\n保存できませんでした。");
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        if (_copyButton is not null) _copyButton.IsEnabled = enabled;
        if (_copyPathButton is not null) _copyPathButton.IsEnabled = enabled && !string.IsNullOrEmpty(_filePath);
        if (_saveButton is not null) _saveButton.IsEnabled = enabled && !string.IsNullOrEmpty(_filePath);
        if (_zoomSlider is not null) _zoomSlider.IsEnabled = enabled;
    }

    private void SetCaption(string text)
    {
        if (_caption is not null)
            _caption.Text = text;
    }

    private void UpdateCaptionDetails()
    {
        if (_bitmap is null || string.IsNullOrEmpty(_filePath))
            return;

        SetCaption(
            $"{Path.GetFileName(_filePath)}  " +
            $"{_bitmap.PixelWidth} x {_bitmap.PixelHeight}px  " +
            $"{FormatBytes(new FileInfo(_filePath).Length)}  " +
            $"{Math.Round(_zoom * 100):0}%");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

