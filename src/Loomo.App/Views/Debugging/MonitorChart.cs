using System.Globalization;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>モニター（§28.15）の折れ線 1 本。CPU（0〜100%）かメモリ（0〜最大値）を描き、止まっていた区間を薄く塗り、
/// 印（停止・再開・終了）を縦線で出す。点は <see cref="Samples"/> の参照が変わるたびに描き直す。</summary>
public sealed class MonitorChart : FrameworkElement
{
    public enum ChartMetric { Cpu, Memory }

    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IReadOnlyList<ProcessMonitorSample>), typeof(MonitorChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(
        nameof(Markers), typeof(IReadOnlyList<ProcessMonitorMarker>), typeof(MonitorChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(
        nameof(Metric), typeof(ChartMetric), typeof(MonitorChart),
        new FrameworkPropertyMetadata(ChartMetric.Cpu, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(MonitorChart),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(MonitorChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ProcessMonitorSample>? Samples
    {
        get => (IReadOnlyList<ProcessMonitorSample>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public IReadOnlyList<ProcessMonitorMarker>? Markers
    {
        get => (IReadOnlyList<ProcessMonitorMarker>?)GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public ChartMetric Metric
    {
        get => (ChartMetric)GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>目盛りの文字と枠の色。</summary>
    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 20 || height < 20)
            return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        var gridPen = new Pen(Foreground, 0.5) { DashStyle = DashStyles.Dot };
        gridPen.Freeze();
        dc.DrawLine(gridPen, new Point(0, height / 2), new Point(width, height / 2));
        dc.DrawLine(gridPen, new Point(0, height - 0.5), new Point(width, height - 0.5));

        var samples = Samples;
        if (samples is not { Count: > 1 })
            return;

        double Value(ProcessMonitorSample s) => Metric == ChartMetric.Cpu ? s.CpuPercent : s.WorkingSetBytes;
        var max = Metric == ChartMetric.Cpu ? 100.0 : Math.Max(1, samples.Max(Value) * 1.15);
        var start = samples[0].Utc;
        var span = Math.Max(1, (samples[^1].Utc - start).TotalSeconds);
        double X(DateTime t) => (t - start).TotalSeconds / span * (width - 1);
        double Y(double v) => height - 1 - v / max * (height - 2);

        // 止まっていた区間（ブレークポイント）は薄く塗る。
        var stoppedBrush = Foreground.Clone();
        stoppedBrush.Opacity = 0.12;
        stoppedBrush.Freeze();
        for (var i = 0; i < samples.Count; i++)
        {
            if (!samples[i].IsStopped) continue;
            var j = i;
            while (j + 1 < samples.Count && samples[j + 1].IsStopped) j++;
            var left = X(samples[i].Utc);
            var right = Math.Max(left + 2, X(samples[j].Utc));
            dc.DrawRectangle(stoppedBrush, null, new Rect(left, 0, right - left, height));
            i = j;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(X(samples[0].Utc), Y(Value(samples[0]))), false, false);
            for (var i = 1; i < samples.Count; i++)
                ctx.LineTo(new Point(X(samples[i].Utc), Y(Value(samples[i]))), true, false);
        }
        geometry.Freeze();
        var pen = new Pen(Stroke, 1.5);
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);

        // 目盛り（右上に最大値）。
        var scale = Metric == ChartMetric.Cpu ? "100%" : ProcessMonitorMath.Bytes((long)max);
        DrawText(dc, scale, new Point(width - 4, 2), alignRight: true);

        if (Markers is { Count: > 0 } markers)
        {
            var markerPen = new Pen(Foreground, 1) { DashStyle = DashStyles.Dash };
            markerPen.Freeze();
            foreach (var marker in markers)
            {
                if (marker.Utc < start) continue;
                var x = Math.Min(width - 1, X(marker.Utc));
                dc.DrawLine(markerPen, new Point(x, 0), new Point(x, height));
                DrawText(dc, marker.Label, new Point(x + 3, height - 16), alignRight: false);
            }
        }
    }

    private void DrawText(DrawingContext dc, string text, Point at, bool alignRight)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, alignRight ? new Point(at.X - formatted.Width, at.Y) : at);
    }
}
