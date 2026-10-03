using System.Windows;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaPen = System.Windows.Media.Pen;
using MediaPoint = System.Windows.Point;

namespace MemGuardian.Next.Desktop;

/// <summary>Small custom vector chart with no charting dependency or per-sample UI controls.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(Array.Empty<double>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(MediaBrush), typeof(Sparkline),
        new FrameworkPropertyMetadata(MediaBrushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FixedRatioScaleProperty = DependencyProperty.Register(
        nameof(FixedRatioScale), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double> Values
    {
        get => (IReadOnlyList<double>)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public MediaBrush Stroke
    {
        get => (MediaBrush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public bool FixedRatioScale
    {
        get => (bool)GetValue(FixedRatioScaleProperty);
        set => SetValue(FixedRatioScaleProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width < 4 || bounds.Height < 4) return;
        var gridPen = new MediaPen(new SolidColorBrush(MediaColor.FromRgb(235, 239, 245)), 1);
        gridPen.Freeze();
        for (var line = 1; line <= 3; line++)
        {
            var y = bounds.Height * line / 4;
            drawingContext.DrawLine(gridPen, new MediaPoint(0, y), new MediaPoint(bounds.Width, y));
        }

        if (Values.Count < 2) return;
        var finite = Values.Where(double.IsFinite).ToArray();
        if (finite.Length < 2) return;
        var minimum = FixedRatioScale ? 0 : finite.Min();
        var maximum = FixedRatioScale ? 1 : finite.Max();
        var spread = Math.Max(maximum - minimum, Math.Max(Math.Abs(maximum) * 0.02, 0.01));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var first = true;
            var visibleIndex = 0;
            foreach (var value in finite)
            {
                var x = visibleIndex++ * bounds.Width / (finite.Length - 1);
                var y = bounds.Height - 4 - (value - minimum) / spread * (bounds.Height - 8);
                var point = new MediaPoint(x, y);
                if (first) { context.BeginFigure(point, false, false); first = false; }
                else context.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        var pen = new MediaPen(Stroke, 2.5);
        pen.StartLineCap = PenLineCap.Round;
        pen.EndLineCap = PenLineCap.Round;
        pen.LineJoin = PenLineJoin.Round;
        drawingContext.DrawGeometry(null, pen, geometry);
    }
}
