using System.Globalization;
using System.Windows;
using System.Windows.Media;
using OmniSpec.Core.History;

namespace OmniSpec.App.Controls;

/// <summary>A data series for <see cref="LineChart"/>.</summary>
public sealed class ChartSeries
{
    public ChartSeries(string name, HistoryPoint[] points, Brush brush)
    {
        Name = name;
        Points = points;
        Brush = brush;
    }

    public string Name { get; }
    public HistoryPoint[] Points { get; }
    public Brush Brush { get; }
}

/// <summary>
/// Lightweight time-series line chart drawn directly with <see cref="DrawingContext"/>. It allocates almost nothing per frame
/// and avoids a heavyweight charting dependency, keeping OmniSpec's idle CPU and memory use low.
/// The x-axis ends at the newest sample; gaps longer than 3× the median sample interval are drawn as breaks
/// (no interpolation across missing data).
/// </summary>
public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<ChartSeries>), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(LineChart), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(LineChart), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(
        nameof(WindowSeconds), typeof(double), typeof(LineChart), new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(LineChart), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowAxisProperty = DependencyProperty.Register(
        nameof(ShowAxis), typeof(bool), typeof(LineChart), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(bool), typeof(LineChart), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(LineChart), new FrameworkPropertyMetadata("No data", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueFormatProperty = DependencyProperty.Register(
        nameof(ValueFormat), typeof(string), typeof(LineChart), new FrameworkPropertyMetadata("0", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LimitProperty = DependencyProperty.Register(
        nameof(Limit), typeof(double), typeof(LineChart), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartSeries>? Series { get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double WindowSeconds { get => (double)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public bool ShowAxis { get => (bool)GetValue(ShowAxisProperty); set => SetValue(ShowAxisProperty, value); }
    public bool Fill { get => (bool)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }
    public string ValueFormat { get => (string)GetValue(ValueFormatProperty); set => SetValue(ValueFormatProperty, value); }
    /// <summary>Optional horizontal limit line (e.g. a temperature warning threshold).</summary>
    public double Limit { get => (double)GetValue(LimitProperty); set => SetValue(LimitProperty, value); }

    private Brush Resource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 20 || h < 20) return;

        var gridBrush = Resource("Brush.ChartGrid", Brushes.DimGray);
        var textBrush = Resource("Brush.TextMuted", Brushes.Gray);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testable

        var series = Series?.Where(s => s.Points.Length > 0).ToList();
        if (series is null || series.Count == 0)
        {
            var ft = new FormattedText(EmptyText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, textBrush, dpi);
            dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
            return;
        }

        var end = series.Max(s => s.Points[^1].UnixMs);
        var windowMs = WindowSeconds * 1000;
        var start = end - windowMs;

        double min = double.IsNaN(Minimum) ? double.MaxValue : Minimum;
        double max = double.IsNaN(Maximum) ? double.MinValue : Maximum;
        if (double.IsNaN(Minimum) || double.IsNaN(Maximum))
        {
            foreach (var s in series)
                foreach (var p in s.Points)
                {
                    if (p.UnixMs < start) continue;
                    if (double.IsNaN(Minimum)) min = Math.Min(min, p.Value);
                    if (double.IsNaN(Maximum)) max = Math.Max(max, p.Value);
                }
            if (!double.IsNaN(Limit) && double.IsNaN(Maximum)) max = Math.Max(max, Limit);
            if (min == double.MaxValue) { min = 0; max = 1; }
            if (double.IsNaN(Minimum)) min = Math.Min(0, min);
            if (max - min < 1e-6) max = min + 1;
            if (double.IsNaN(Maximum)) max = NiceCeiling(max);
        }

        var left = ShowAxis ? 40.0 : 0;
        var bottom = ShowAxis ? 16.0 : 0;
        var plot = new Rect(left, 4, Math.Max(1, w - left - 4), Math.Max(1, h - bottom - 8));

        if (ShowAxis)
        {
            var pen = new Pen(gridBrush, 1);
            pen.Freeze();
            for (var i = 0; i <= 4; i++)
            {
                var y = Math.Round(plot.Top + plot.Height * i / 4) + 0.5;
                dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
                var value = max - (max - min) * i / 4;
                var label = new FormattedText(Format(value), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, textBrush, dpi);
                dc.DrawText(label, new Point(left - label.Width - 6, y - label.Height / 2));
            }
            var span = TimeSpan.FromSeconds(WindowSeconds);
            var spanText = span.TotalMinutes >= 1 ? $"−{span.TotalMinutes:0.#} min" : $"−{span.TotalSeconds:0} s";
            var startLabel = new FormattedText(spanText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, textBrush, dpi);
            dc.DrawText(startLabel, new Point(plot.Left, plot.Bottom + 3));
            var nowLabel = new FormattedText("now", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, textBrush, dpi);
            dc.DrawText(nowLabel, new Point(plot.Right - nowLabel.Width, plot.Bottom + 3));
        }

        if (!double.IsNaN(Limit) && Limit >= min && Limit <= max)
        {
            var y = plot.Bottom - (Limit - min) / (max - min) * plot.Height;
            var limitPen = new Pen(Resource("Brush.Critical", Brushes.IndianRed), 1) { DashStyle = DashStyles.Dash };
            limitPen.Freeze();
            dc.DrawLine(limitPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        dc.PushClip(new RectangleGeometry(plot));
        foreach (var s in series)
        {
            DrawSeries(dc, s, plot, start, windowMs, min, max);
        }
        dc.Pop();
    }

    private void DrawSeries(DrawingContext dc, ChartSeries s, Rect plot, double start, double windowMs, double min, double max)
    {
        var pts = s.Points;
        var first = Array.FindIndex(pts, p => p.UnixMs >= start);
        if (first < 0) return;
        if (first > 0) first--; // include one point before the window for continuity

        // Gap detection threshold: 3× median interval.
        var gap = MedianInterval(pts, first) * 3;

        Point Map(HistoryPoint p) => new(
            plot.Left + (p.UnixMs - start) / windowMs * plot.Width,
            plot.Bottom - (Math.Clamp(p.Value, min, max) - min) / (max - min) * plot.Height);

        var line = new StreamGeometry();
        var area = Fill ? new StreamGeometry() : null;
        using (var ctx = line.Open())
        using (var actx = area?.Open())
        {
            var started = false;
            Point segStart = default, last = default;
            for (var i = first; i < pts.Length; i++)
            {
                var pt = Map(pts[i]);
                var broken = started && gap > 0 && pts[i].UnixMs - pts[i - 1].UnixMs > gap;
                if (!started || broken)
                {
                    if (started && actx is not null) CloseArea(actx, segStart, last, plot);
                    ctx.BeginFigure(pt, false, false);
                    actx?.BeginFigure(new Point(pt.X, plot.Bottom), true, true);
                    actx?.LineTo(pt, false, false);
                    segStart = pt;
                    started = true;
                }
                else
                {
                    ctx.LineTo(pt, true, true);
                    actx?.LineTo(pt, false, false);
                }
                last = pt;
            }
            if (started && actx is not null) actx.LineTo(new Point(last.X, plot.Bottom), false, false);
        }
        line.Freeze();

        if (area is not null && s.Brush is SolidColorBrush solid)
        {
            area.Freeze();
            var c = solid.Color;
            var gradient = new LinearGradientBrush(Color.FromArgb(70, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
            gradient.Freeze();
            dc.DrawGeometry(gradient, null, area);
        }
        var pen = new Pen(s.Brush, 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }

    private static void CloseArea(StreamGeometryContext actx, Point segStart, Point last, Rect plot) =>
        actx.LineTo(new Point(last.X, plot.Bottom), false, false);

    private static double MedianInterval(HistoryPoint[] pts, int from)
    {
        var n = pts.Length - from - 1;
        if (n < 2) return 0;
        var diffs = new double[Math.Min(n, 64)];
        for (var i = 0; i < diffs.Length; i++)
        {
            var idx = pts.Length - 1 - i;
            diffs[i] = pts[idx].UnixMs - pts[idx - 1].UnixMs;
        }
        Array.Sort(diffs);
        return diffs[diffs.Length / 2];
    }

    private string Format(double v)
    {
        var text = Math.Abs(v) >= 10_000 ? (v / 1000).ToString("0.#", CultureInfo.CurrentCulture) + "k" : v.ToString(ValueFormat, CultureInfo.CurrentCulture);
        return string.IsNullOrEmpty(Unit) ? text : text + Unit;
    }

    private static double NiceCeiling(double v)
    {
        if (v <= 0) return 1;
        var exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        var f = v / exp;
        var nice = f <= 1 ? 1 : f <= 1.2 ? 1.2 : f <= 1.6 ? 1.6 : f <= 2 ? 2 : f <= 2.4 ? 2.4 : f <= 3.2 ? 3.2 : f <= 4 ? 4 : f <= 6 ? 6 : f <= 8 ? 8 : 10;
        return nice * exp;
    }
}
