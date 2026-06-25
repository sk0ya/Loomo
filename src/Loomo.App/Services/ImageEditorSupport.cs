using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace sk0ya.Loomo.App.Services;

/// <summary>画像ファイル（.png / .ico など）のプレビュー。</summary>
public sealed partial class ImageEditorSupport : IEditorSupportVisualProvider
{
    private const double MinZoom = 0.05;
    private const double MaxZoom = 16.0;
    private const double ZoomStep = 1.25;
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
    private BitmapSource? _bitmap;
    private string? _filePath;
    private double _zoom = 1.0;
    private bool _fitToView = true;
    private bool _updatingSlider;
    private Point? _panStart;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private int _fitRequestSeq;

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

        SetActionsEnabled(false);
        SetCaption("未読み込み");
        return _view;
    }

    public Task UpdateAsync(string filePath, string text)
    {
        GetOrCreateView();
        _filePath = filePath;
        _fitToView = true;
        _panStart = null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            _bitmap = bitmap;
            if (_image is not null)
                _image.Source = bitmap;
            if (_emptyState is not null)
                _emptyState.Visibility = Visibility.Collapsed;

            if (_view?.IsLoaded == true)
            {
                _view.UpdateLayout();
                UpdateSurfaceMinimumSize();
                ApplyFitZoom();
            }
            QueueFitZoom();
            SetActionsEnabled(true);
            UpdateCaptionDetails();
        }
        catch (Exception ex)
        {
            _bitmap = null;
            if (_image is not null)
                _image.Source = null;
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
            Minimum = MinZoom * 100,
            Maximum = MaxZoom * 100,
            Value = 100,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0),
            SmallChange = 5,
            LargeChange = 25
        };
        _zoomSlider.ValueChanged += (_, _) =>
        {
            if (_updatingSlider)
                return;
            SetZoom(_zoomSlider.Value / 100.0, fitToView: false);
        };

        var bar = new DockPanel
        {
            Margin = new Thickness(8, 6, 8, 4),
            LastChildFill = true
        };

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(MakeButton("−", () => SetZoom(_zoom / ZoomStep, fitToView: false), "縮小 (Ctrl+ホイール)"));
        left.Children.Add(MakeButton("+", () => SetZoom(_zoom * ZoomStep, fitToView: false), "拡大 (Ctrl+ホイール)"));
        left.Children.Add(MakeButton("1:1", () => SetZoom(1.0, fitToView: false), "等倍"));
        left.Children.Add(MakeButton("⛶", () => { _fitToView = true; ApplyFitZoom(); }, "全体表示"));
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
