using System.Windows;
using System.Windows.Media;

namespace OmniSpec.App.Controls;

/// <summary>A 270° ring gauge. When <see cref="Value"/> is NaN only the empty track is drawn (no fake value).</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush? Brush { get => (Brush?)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 10) return;
        var t = Thickness;
        var radius = (size - t) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        const double startAngle = 135, sweep = 270;

        var track = new Pen(TryFindResource("Brush.Track") as Brush ?? Brushes.DimGray, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        track.Freeze();
        dc.DrawGeometry(null, track, Arc(center, radius, startAngle, sweep));

        if (double.IsNaN(Value) || Maximum <= 0) return;
        var fraction = Math.Clamp(Value / Maximum, 0, 1);
        if (fraction <= 0.001) return;
        var pen = new Pen(Brush ?? TryFindResource("Brush.Accent") as Brush ?? Brushes.DodgerBlue, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, Arc(center, radius, startAngle, sweep * fraction));
    }

    private static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        static Point P(Point c, double r, double deg)
        {
            var rad = deg * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(c, r, startDeg), false, false);
            ctx.ArcTo(P(c, r, startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}
