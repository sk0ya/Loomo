namespace sk0ya.Loomo.App.Services;

/// <summary>ペインドラッグ中のゴーストとドロップ矩形オーバーレイを生成・更新する。</summary>
internal sealed class PaneDragVisualPresenter(
    Grid overlayHost,
    Canvas ghostLayer,
    FrameworkElement resources,
    Func<PaneKind, string> paneLabel)
{
    private Canvas? _canvas;
    private Border? _preview;
    private Border? _targetOutline;
    private Border? _ghost;

    public Canvas? DragCanvas => _canvas;

    public Canvas EnsureOverlay(Action<Canvas> wireEvents)
    {
        if (_canvas is not null)
            return _canvas;

        var accent = (Brush)resources.FindResource("Accent");
        _targetOutline = new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            Background = MakeTranslucent(accent, 0.10),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _preview = new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(2),
            Background = MakeTranslucent(accent, 0.35),
            CornerRadius = new CornerRadius(2),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _canvas = new Canvas
        {
            Background = Brushes.Transparent,
            ClipToBounds = true,
            IsHitTestVisible = false,
        };
        _canvas.Children.Add(_targetOutline);
        _canvas.Children.Add(_preview);
        wireEvents(_canvas);
        overlayHost.Children.Add(_canvas);
        overlayHost.Visibility = Visibility.Visible;
        overlayHost.UpdateLayout();
        return _canvas;
    }

    public void ShowPreview(Rect target, Rect preview)
    {
        if (_targetOutline is null || _preview is null)
            return;
        Place(_targetOutline, target);
        Place(_preview, preview);
        _targetOutline.Visibility = Visibility.Visible;
        _preview.Visibility = Visibility.Visible;
    }

    public void HidePreview()
    {
        if (_preview is not null)
            _preview.Visibility = Visibility.Collapsed;
        if (_targetOutline is not null)
            _targetOutline.Visibility = Visibility.Collapsed;
    }

    public void ShowGhost(PaneKind kind)
    {
        _ghost ??= CreateGhost();
        SetGhostDestination(kind, toWing: false);
        _ghost.Visibility = Visibility.Visible;
        ghostLayer.Visibility = Visibility.Visible;
    }

    public void SetGhostDestination(PaneKind kind, bool toWing)
    {
        if (_ghost?.Child is TextBlock label)
            label.Text = toWing ? $"{paneLabel(kind)} → 袖へ" : paneLabel(kind);
    }

    public void MoveGhost(Point position)
    {
        if (_ghost is null)
            return;
        Canvas.SetLeft(_ghost, position.X + 14);
        Canvas.SetTop(_ghost, position.Y + 16);
    }

    public void HideGhost()
    {
        if (_ghost is not null)
            _ghost.Visibility = Visibility.Collapsed;
        ghostLayer.Visibility = Visibility.Collapsed;
    }

    private Border CreateGhost()
    {
        var accent = (Brush)resources.FindResource("Accent");
        var ghost = new Border
        {
            Background = MakeTranslucent(accent, 0.9),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 5, 10, 5),
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 8,
                ShadowDepth = 2,
                Opacity = 0.5,
                Color = Colors.Black,
            },
            Child = new TextBlock
            {
                Foreground = (Brush)resources.FindResource("AccentFg"),
                FontSize = UiFontManager.Scaled(12),
                FontWeight = FontWeights.SemiBold,
            },
        };
        ghostLayer.Children.Add(ghost);
        return ghost;
    }

    private static void Place(Border border, Rect rect)
    {
        Canvas.SetLeft(border, rect.X);
        Canvas.SetTop(border, rect.Y);
        border.Width = rect.Width;
        border.Height = rect.Height;
    }

    private static Brush MakeTranslucent(Brush source, double opacity)
    {
        if (source is SolidColorBrush solid)
        {
            var color = solid.Color;
            return new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), color.R, color.G, color.B));
        }
        var clone = source.Clone();
        clone.Opacity = opacity;
        return clone;
    }
}
