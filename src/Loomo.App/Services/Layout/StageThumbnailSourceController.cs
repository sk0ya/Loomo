namespace sk0ya.Loomo.App.Services;

/// <summary>ペインを袖・俯瞰カード用の縮小描画ホストへ差分で配置する。</summary>
internal sealed class StageThumbnailSourceController(
    Grid sourceArea,
    IReadOnlyDictionary<PaneKind, FrameworkElement> paneElements)
{
    private readonly Dictionary<PaneKind, Grid> _hosts = new();
    private readonly Dictionary<PaneKind, ImageBrush> _snapshotBrushes = new();
    private readonly Dictionary<PaneKind, int> _captureSequences = new();
    private double _sourceWidth;

    public IReadOnlyCollection<PaneKind> Kinds => _hosts.Keys.ToArray();

    public bool TryGetHost(PaneKind kind, out Grid host) => _hosts.TryGetValue(kind, out host!);

    /// <summary>必要な描画元だけを残し、再利用できる要素は親を付け替えず寸法だけ整える。</summary>
    public void Sync(IReadOnlyCollection<PaneKind> required, Size sourceSize)
    {
        foreach (var kind in required.Where(kind =>
                     StageThumbnailPlanner.UsesSnapshotThumbnail(kind) && SnapshotThumbnailNeedsSeed(kind)))
            CaptureComposedPaneThumbnail(kind);

        var liveRequired = required.Where(kind => !StageThumbnailPlanner.UsesSnapshotThumbnail(kind)).ToArray();
        var sizeChanged = StageThumbnailPlanner.SourceSizeChanged(_sourceWidth, sourceSize.Width);
        var reusable = _hosts.Keys.Where(IsIntact).ToArray();
        var plan = StageThumbnailPlanner.PlanSources(_hosts.Keys.ToArray(), reusable, liveRequired);
        PaneLayoutDebugLog.Log(
            $"SyncThumbnailSources keep={plan.Keep.Count} add={plan.Add.Count} remove={plan.Remove.Count}"
            + $" sizeChanged={sizeChanged}");

        foreach (var kind in plan.Remove)
            Release(kind);
        foreach (var kind in plan.Add)
            Arrange(kind, sourceSize);
        if (sizeChanged)
            foreach (var kind in plan.Keep)
                Resize(kind, sourceSize);
        _sourceWidth = sourceSize.Width;
    }

    public static VisualBrush VisualThumbnailBrush(Visual source)
    {
        var sourceWidth = source is FrameworkElement sourceElement
            ? double.IsFinite(sourceElement.Width) && sourceElement.Width > 0
                ? sourceElement.Width
                : sourceElement.ActualWidth
            : 1;
        var sourceHeight = source is FrameworkElement sourceElement2
            ? double.IsFinite(sourceElement2.Height) && sourceElement2.Height > 0
                ? sourceElement2.Height
                : sourceElement2.ActualHeight
            : 1;
        return new VisualBrush(source)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, Math.Max(sourceWidth, 1), Math.Max(sourceHeight, 1)),
            Stretch = Stretch.Uniform,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
    }

    public ImageBrush SnapshotThumbnailBrush(PaneKind kind)
    {
        if (_snapshotBrushes.TryGetValue(kind, out var existing))
            return existing;
        var brush = new ImageBrush
        {
            Stretch = Stretch.Uniform,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
        _snapshotBrushes[kind] = brush;
        return brush;
    }

    private bool SnapshotThumbnailNeedsSeed(PaneKind kind)
        => !_snapshotBrushes.TryGetValue(kind, out var brush) || brush.ImageSource is null;

    /// <summary>現在合成済みのペインを同期キャプチャし、非同期WebViewキャプチャまでの空白を防ぐ。</summary>
    private void CaptureComposedPaneThumbnail(PaneKind kind)
    {
        if (!paneElements.TryGetValue(kind, out var element)
            || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return;
        try
        {
            const double maxWidth = StageThumbnailPlanner.VirtualWidth * 2;
            var scale = Math.Min(1, maxWidth / element.ActualWidth);
            var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * scale));
            var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * scale));
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawRectangle(new VisualBrush(element), null,
                    new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            }
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            bitmap.Freeze();
            SnapshotThumbnailBrush(kind).ImageSource = bitmap;
        }
        catch
        {
            // 合成面の取得に失敗しても、前回画像または後続のCapturePreviewAsyncを使用する。
        }
    }

    /// <summary>WebViewのプレビューを非同期で更新する。同じペインへの連続要求は最新だけを採用する。</summary>
    public async Task CaptureWebThumbnailAsync(
        PaneKind kind, Func<PaneKind, CoreWebView2?> resolveCore)
    {
        if (!StageThumbnailPlanner.UsesSnapshotThumbnail(kind))
            return;
        var sequence = _captureSequences.TryGetValue(kind, out var previous) ? previous + 1 : 1;
        _captureSequences[kind] = sequence;
        await Task.Delay(80);
        if (!_captureSequences.TryGetValue(kind, out var current) || current != sequence)
            return;
        var core = resolveCore(kind);
        if (core is null)
            return;
        try
        {
            using var stream = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            if (!_captureSequences.TryGetValue(kind, out current) || current != sequence)
                return;
            stream.Position = 0;
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            SnapshotThumbnailBrush(kind).ImageSource = image;
        }
        catch
        {
            // 非表示化・ナビゲーション競合時は取得できないことがある。前回画像を維持する。
        }
    }

    /// <summary>描画元を全部捨て、ペインを親なしへ戻す。</summary>
    public void Clear()
    {
        foreach (var host in _hosts.Values)
            host.Children.Clear();
        sourceArea.Children.Clear();
        _hosts.Clear();
        _sourceWidth = 0;
    }

    private void Arrange(PaneKind kind, Size sourceSize)
        => PaneLayoutDebugLog.Time($"  ArrangeThumbnailSource({kind}) {sourceSize.Width:0}x{sourceSize.Height:0}",
            () => ArrangeCore(kind, sourceSize));

    private void ArrangeCore(PaneKind kind, Size sourceSize)
    {
        var element = paneElements[kind];
        var width = Math.Max(sourceSize.Width, 1);
        var height = Math.Max(sourceSize.Height, 1);
        var host = new Grid
        {
            Width = width,
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Clip = new RectangleGeometry(new Rect(0, 0, width, height)),
        };
        if (element.Parent is Panel parent)
            parent.Children.Remove(element);
        element.Visibility = Visibility.Visible;
        host.Children.Add(element);
        sourceArea.Children.Add(host);
        var clamped = new Size(width, height);
        host.Measure(clamped);
        host.Arrange(new Rect(clamped));
        host.UpdateLayout();
        _hosts[kind] = host;
    }

    private bool IsIntact(PaneKind kind)
        => _hosts.TryGetValue(kind, out var host)
           && host.Parent is not null
           && host.Children.Count == 1
           && ReferenceEquals(host.Children[0], paneElements[kind]);

    private void Resize(PaneKind kind, Size sourceSize)
    {
        if (!_hosts.TryGetValue(kind, out var host))
            return;
        var width = Math.Max(sourceSize.Width, 1);
        var height = Math.Max(sourceSize.Height, 1);
        if (Math.Abs(host.Width - width) < 0.5 && Math.Abs(host.Height - height) < 0.5)
            return;
        host.Width = width;
        host.Height = height;
        host.Clip = new RectangleGeometry(new Rect(0, 0, width, height));
        var clamped = new Size(width, height);
        host.Measure(clamped);
        host.Arrange(new Rect(clamped));
        host.UpdateLayout();
    }

    private void Release(PaneKind kind)
    {
        if (!_hosts.Remove(kind, out var host))
            return;
        host.Children.Clear();
        sourceArea.Children.Remove(host);
    }
}
