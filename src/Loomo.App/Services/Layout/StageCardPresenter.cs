namespace sk0ya.Loomo.App.Services;

/// <summary>袖／俯瞰カードのサムネイル、見た目、hover・click・drag入力を組み立てる。</summary>
internal sealed class StageCardPresenter(
    FrameworkElement resources,
    StageThumbnailSourceController thumbnails,
    IReadOnlyDictionary<PaneKind, FrameworkElement> paneElements,
    Action<Grid, PaneKind, bool> attachActivityBadge,
    Action<PaneKind> beginDrag)
{
    private const double CardAspect = 3.0 / 2.0;
    private const double RestOpacity = 0.90;

    public Border BuildSessionCard(
        PaneKind kind, double width, bool isOverview, bool iconOnly, bool onStage, Action onClick)
        => BuildCard(kind, width, Thumbnail(kind), isOverview, iconOnly, onStage, onClick);

    public Border BuildLayoutWingCard(
        PaneKind kind, double width, bool iconOnly, bool onStage, Action onClick)
        => BuildCard(kind, width, Thumbnail(kind), isOverview: false, iconOnly, onStage, onClick);

    public static IEnumerable<UIElement> ArrangeWingRows(
        IReadOnlyList<Border> cards, int columns, double gap)
    {
        if (columns <= 1)
            return cards;
        var rows = new List<UIElement>();
        for (var i = 0; i < cards.Count; i += columns)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (var j = i; j < Math.Min(i + columns, cards.Count); j++)
            {
                if (j > i)
                    cards[j].Margin = new Thickness(gap, 4, 0, 4);
                row.Children.Add(cards[j]);
            }
            rows.Add(row);
        }
        return rows;
    }

    private Brush Thumbnail(PaneKind kind)
    {
        if (StageThumbnailPlanner.UsesSnapshotThumbnail(kind))
            return thumbnails.SnapshotThumbnailBrush(kind);
        var source = thumbnails.TryGetHost(kind, out var host) ? host : paneElements[kind];
        return StageThumbnailSourceController.VisualThumbnailBrush(source);
    }

    private Border BuildCard(
        PaneKind kind, double width, Brush thumbnail, bool isOverview, bool iconOnly,
        bool onStage, Action onClick)
    {
        var borderBrush = (Brush)resources.FindResource("Border");
        var accent = (Brush)resources.FindResource("Accent");
        var height = iconOnly ? width : Math.Round(width / CardAspect);
        var card = new Border
        {
            Width = width,
            Height = height,
            Margin = isOverview ? new Thickness(10) : iconOnly ? new Thickness(0) : new Thickness(0, 4, 0, 4),
            CornerRadius = new CornerRadius(iconOnly ? 0 : 6),
            Background = iconOnly ? Brushes.Transparent : (Brush)resources.FindResource("Panel"),
            BorderBrush = iconOnly ? Brushes.Transparent : onStage ? accent : borderBrush,
            BorderThickness = iconOnly ? new Thickness(0) : new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = isOverview ? PaneVisibilityPresentation.Label(kind)
                : $"{PaneVisibilityPresentation.Label(kind)} — クリックで舞台へ",
            Clip = new RectangleGeometry(new Rect(0, 0, width, height), iconOnly ? 0 : 6, iconOnly ? 0 : 6),
        };
        var root = new Grid { ClipToBounds = true };
        if (iconOnly)
        {
            root.Children.Add(new System.Windows.Shapes.Path
            {
                Data = resources.TryFindResource($"PaneIcon.{kind}") as Geometry,
                Width = 16,
                Height = 16,
                Stretch = Stretch.None,
                Stroke = (Brush)resources.FindResource("FgDim"),
                StrokeThickness = 1.3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else
        {
            root.Children.Add(new Border { IsHitTestVisible = false, Background = thumbnail });
            root.Children.Add(new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromArgb(0xB4, 0x10, 0x10, 0x10)),
                Child = new TextBlock
                {
                    Text = PaneVisibilityPresentation.Label(kind),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = UiFontManager.Scaled(isOverview ? 12 : 11),
                    Margin = new Thickness(8, 3, 8, 3),
                    Foreground = Brushes.White,
                },
            });
        }
        card.Child = root;
        attachActivityBadge(root, kind, isOverview);
        var rest = isOverview ? 1.0 : RestOpacity;
        card.Opacity = rest;
        card.MouseEnter += (_, _) =>
        {
            if (iconOnly)
                card.Background = (Brush)resources.FindResource("BgAlt");
            else
                card.BorderBrush = accent;
            card.Opacity = 1;
        };
        card.MouseLeave += (_, _) =>
        {
            if (iconOnly)
            {
                card.Background = Brushes.Transparent;
                card.BorderBrush = Brushes.Transparent;
            }
            else
            {
                card.BorderBrush = onStage ? accent : borderBrush;
            }
            card.Opacity = rest;
        };
        var dragArmed = false;
        var dragStart = default(Point);
        card.MouseLeftButtonUp += (_, e) =>
        {
            dragArmed = false;
            e.Handled = true;
            onClick();
        };

        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            dragStart = e.GetPosition(resources);
            dragArmed = true;
        };
        card.PreviewMouseMove += (_, e) =>
        {
            if (isOverview || !dragArmed || e.LeftButton != MouseButtonState.Pressed)
                return;
            var position = e.GetPosition(resources);
            if (Math.Abs(position.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(position.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            dragArmed = false;
            beginDrag(kind);
        };
        return card;
    }
}
