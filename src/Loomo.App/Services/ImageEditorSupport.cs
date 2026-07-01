using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace sk0ya.Loomo.App.Services;

/// <summary>画像ファイル（.png / .ico など）のプレビュー。ビュー構築・読み込み・ツールバー・アクション
/// （コピー／保存）・キャプションを担い、ズーム／パン操作は <see cref="ImageZoomController"/> へ委譲する。</summary>
public sealed class ImageEditorSupport : IEditorSupportVisualProvider
{
    private static readonly string[] Extensions =
        [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tif", ".tiff"];

    private Grid? _view;
    private ScrollViewer? _scroll;
    private Grid? _imageSurface;
    private Image? _image;
    private TextBlock? _caption;
    private TextBlock? _emptyState;
    private TextBlock? _zoomLabel;
    private Slider? _zoomSlider;
    private Button? _copyButton;
    private Button? _saveButton;
    private Button? _copyPathButton;
    private ImageZoomController? _zoom;
    private string? _filePath;

    public event EventHandler<EditorSupportContentEdited>? ContentEdited { add { } remove { } }

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public string DescribeTitle(string filePath) => $"Image: {Path.GetFileName(filePath)}";

    public FrameworkElement GetOrCreateView()
    {
        if (_view is not null)
            return _view;

        _image = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        _emptyState = new TextBlock
        {
            Text = "画像を選択するとここにプレビューします。",
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Visibility = Visibility.Visible
        };
        _emptyState.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");

        _imageSurface = new Grid
        {
            MinWidth = 1,
            MinHeight = 1,
            ClipToBounds = true,
            Background = CreateCheckerBrush()
        };
        _imageSurface.Children.Add(_image);
        _imageSurface.Children.Add(_emptyState);

        _scroll = new ScrollViewer
        {
            Content = _imageSurface,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            CanContentScroll = false,
            Background = Brushes.Transparent
        };
        _scroll.SetResourceReference(Control.ForegroundProperty, "Fg");

        _caption = new TextBlock
        {
            Margin = new Thickness(10, 6, 10, 8),
            TextAlignment = TextAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");

        _view = new Grid();
        _view.SetResourceReference(Panel.BackgroundProperty, "Panel");
        _view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _view.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = CreateToolbar();
        _view.Children.Add(toolbar);
        Grid.SetRow(toolbar, 0);
        _view.Children.Add(_scroll);
        Grid.SetRow(_scroll, 1);
        _view.Children.Add(_caption);
        Grid.SetRow(_caption, 2);

        // ズーム／パンのマウス操作・フィット・スライダー連動はコントローラが担う（ツールバー生成後に配線）。
        _zoom = new ImageZoomController(_scroll, _image, _imageSurface, _zoomLabel!, _zoomSlider!);
        _zoom.ZoomChanged += UpdateCaptionDetails;

        SetActionsEnabled(false);
        SetCaption("未読み込み");
        return _view;
    }

    public Task UpdateAsync(string filePath, string text)
    {
        GetOrCreateView();
        _filePath = filePath;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            _zoom!.SetBitmap(bitmap);
            if (_emptyState is not null)
                _emptyState.Visibility = Visibility.Collapsed;

            if (_view?.IsLoaded == true)
            {
                _view.UpdateLayout();
                _zoom.UpdateSurfaceMinimumSize();
                _zoom.ApplyFitZoom();
            }
            _zoom.QueueFitZoom();
            SetActionsEnabled(true);
            UpdateCaptionDetails();
        }
        catch (Exception ex)
        {
            _zoom?.Clear();
            if (_emptyState is not null)
            {
                _emptyState.Text = "画像を読み込めませんでした。";
                _emptyState.Visibility = Visibility.Visible;
            }

            SetActionsEnabled(false);
            SetCaption($"{Path.GetFileName(filePath)}  読み込み失敗: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private FrameworkElement CreateToolbar()
    {
        _zoomLabel = new TextBlock
        {
            Width = 52,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            FontSize = 12
        };
        _zoomLabel.SetResourceReference(TextBlock.ForegroundProperty, "Fg");

        _zoomSlider = new Slider
        {
            Width = 160,
            Minimum = ImageZoomController.MinZoom * 100,
            Maximum = ImageZoomController.MaxZoom * 100,
            Value = 100,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0),
            SmallChange = 5,
            LargeChange = 25
        };

        var bar = new DockPanel
        {
            Margin = new Thickness(8, 6, 8, 4),
            LastChildFill = true
        };

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(MakeButton("−", () => _zoom?.ZoomOut(), "縮小 (Ctrl+ホイール)"));
        left.Children.Add(MakeButton("+", () => _zoom?.ZoomIn(), "拡大 (Ctrl+ホイール)"));
        left.Children.Add(MakeButton("1:1", () => _zoom?.ActualSize(), "等倍"));
        left.Children.Add(MakeButton("⛶", () => _zoom?.FitToWindow(), "全体表示"));
        left.Children.Add(_zoomSlider);
        left.Children.Add(_zoomLabel);
        left.Children.Add(CreateSeparator());
        _copyButton = MakeButton("⧉", CopyImage, "画像をコピー");
        _copyPathButton = MakeButton("⛓", CopyPath, "パスをコピー");
        _saveButton = MakeButton("⇩", SaveAs, "別名で保存");
        left.Children.Add(_copyButton);
        left.Children.Add(_copyPathButton);
        left.Children.Add(_saveButton);
        DockPanel.SetDock(left, Dock.Left);
        bar.Children.Add(left);

        var hint = new TextBlock
        {
            Text = "ドラッグで移動 / Ctrl+ホイールでズーム",
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            FontSize = 11,
            Margin = new Thickness(8, 0, 2, 0)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
        bar.Children.Add(hint);
        return bar;
    }

    private static Border CreateSeparator()
    {
        var border = new Border
        {
            Width = 1,
            Height = 18,
            Margin = new Thickness(8, 3, 8, 3)
        };
        border.SetResourceReference(Border.BackgroundProperty, "Border");
        return border;
    }

    private static Button MakeButton(string text, Action action, string tooltip)
    {
        var button = new Button
        {
            Content = text,
            ToolTip = tooltip,
            MinWidth = 34,
            Height = 26,
            Margin = new Thickness(2, 0, 2, 0),
            Padding = new Thickness(8, 0, 8, 0),
            FontSize = 12
        };
        button.SetResourceReference(Control.ForegroundProperty, "Fg");
        button.SetResourceReference(Control.BackgroundProperty, "BgAlt");
        button.SetResourceReference(Control.BorderBrushProperty, "Border");
        button.Click += (_, _) => action();
        return button;
    }

    private void CopyImage()
    {
        var bitmap = _zoom?.Bitmap;
        if (bitmap is null)
            return;

        try
        {
            Clipboard.SetImage(bitmap);
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
        var bitmap = _zoom?.Bitmap;
        if (bitmap is null || string.IsNullOrEmpty(_filePath))
            return;

        // ビットマップは読み込み時にメモリへ複製済み（CacheOption.OnLoad）なので、以降に元ファイルが
        // git のブランチ切り替え等で消えても表示自体は保てる。ただしファイルサイズはその場で
        // 都度読み直すため、消えていれば FileNotFoundException 等を投げうる。ここはズーム変更のたびに
        // 呼ばれる（リサイズ含む）ので、失敗してもキャプションからサイズを落とすだけで落とさない。
        long? size;
        try { size = new FileInfo(_filePath).Length; }
        catch { size = null; }

        SetCaption(
            $"{Path.GetFileName(_filePath)}  " +
            $"{bitmap.PixelWidth} x {bitmap.PixelHeight}px  " +
            (size is { } s ? $"{FormatBytes(s)}  " : "") +
            $"{Math.Round(_zoom!.Zoom * 100):0}%");
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

    private static Brush CreateCheckerBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(48, 49, 52)),
            null,
            new RectangleGeometry(new Rect(0, 0, 24, 24))));
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(37, 37, 38)),
            null,
            new GeometryGroup
            {
                Children =
                {
                    new RectangleGeometry(new Rect(0, 0, 12, 12)),
                    new RectangleGeometry(new Rect(12, 12, 12, 12))
                }
            }));

        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 24, 24),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
        brush.Freeze();
        return brush;
    }
}

public static class ImageFitMath
{
    public const double SafetyInset = 8.0;

    public static double CalculateFitZoom(
        double viewportWidth,
        double viewportHeight,
        double imageWidth,
        double imageHeight)
    {
        if (viewportWidth <= 1 || viewportHeight <= 1 || imageWidth <= 0 || imageHeight <= 0)
            return 1.0;

        var availableWidth = Math.Max(1, viewportWidth - SafetyInset);
        var availableHeight = Math.Max(1, viewportHeight - SafetyInset);
        return Math.Min(availableWidth / imageWidth, availableHeight / imageHeight);
    }
}
