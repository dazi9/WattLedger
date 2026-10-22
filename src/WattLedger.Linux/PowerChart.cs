using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace WattLedger.Linux;

internal sealed record PowerPoint(DateTimeOffset Timestamp, double? Watts, int? BatteryPercent = null);

internal sealed class PowerChart : Control
{
    private IReadOnlyList<PowerPoint> _samples = Array.Empty<PowerPoint>();

    public IReadOnlyList<PowerPoint> Samples
    {
        get => _samples;
        set
        {
            _samples = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        if (bounds.Width < 150 || bounds.Height < 30) return;

        var layout = PowerChartLayout.Calculate(_samples, bounds.Width, bounds.Height);
        var gridBrush = new SolidColorBrush(Color.Parse("#263341"));
        var labelBrush = new SolidColorBrush(Color.Parse("#778597"));
        var mutedTypeface = new Typeface("sans-serif");
        var normalGridPen = new Pen(gridBrush, 1);

        var hasZeroGridLine = false;
        for (var index = 0; index <= 5; index++)
        {
            var value = layout.PowerMaximum - (layout.PowerMaximum - layout.PowerMinimum) * index / 5;
            var y = layout.PlotTop + (layout.PlotBottom - layout.PlotTop) * index / 5;
            var isZero = Math.Abs(value) < 1e-9;
            hasZeroGridLine |= isZero;
            var pen = isZero ? new Pen(new SolidColorBrush(Color.Parse("#526174")), 1.2) : normalGridPen;
            context.DrawLine(pen, new Point(layout.PlotLeft, y), new Point(layout.PlotRight, y));
            DrawLabel(context, $"{value:0} W", 2, y - 7, mutedTypeface, labelBrush);
            DrawLabel(context, $"{100 - index * 20}%", layout.PlotRight + 6, y - 7, mutedTypeface, labelBrush);
        }

        if (!hasZeroGridLine)
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#526174")), 1.2),
                new Point(layout.PlotLeft, layout.ZeroY), new Point(layout.PlotRight, layout.ZeroY));

        foreach (var tick in layout.TimeTicks)
        {
            context.DrawLine(normalGridPen, new Point(tick.X, layout.PlotTop), new Point(tick.X, layout.PlotBottom));
            var text = new FormattedText(tick.Label, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, mutedTypeface, 9, labelBrush);
            var maximumLabelX = Math.Max(layout.PlotLeft, layout.PlotRight - text.Width);
            var labelX = Math.Clamp(tick.X - text.Width / 2, layout.PlotLeft, maximumLabelX);
            context.DrawText(text, new Point(labelX, layout.PlotBottom + 8));
        }

        DrawPowerRuns(context, layout);
        DrawBatteryCurve(context, layout);
    }

    private static void DrawPowerRuns(DrawingContext context, PowerChartLayout layout)
    {
        var green = Color.Parse("#3DDC97");
        foreach (var run in layout.PowerRuns)
        {
            var color = green;
            if (run.Points.Count == 1)
            {
                var point = run.Points[0].Position;
                context.DrawEllipse(new SolidColorBrush(color), null, new Point(point.X, point.Y), 3.5, 3.5);
                continue;
            }

            var area = new StreamGeometry();
            using (var geometryContext = area.Open())
            {
                var first = run.Points[0].Position;
                geometryContext.BeginFigure(new Point(first.X, layout.ZeroY), true);
                geometryContext.LineTo(new Point(first.X, first.Y));
                foreach (var item in run.Points.Skip(1))
                {
                    var point = item.Position;
                    geometryContext.LineTo(new Point(point.X, point.Y));
                }
                var last = run.Points[^1].Position;
                geometryContext.LineTo(new Point(last.X, layout.ZeroY));
                geometryContext.EndFigure(true);
            }
            context.DrawGeometry(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                [
                    new GradientStop(Color.FromArgb(65, color.R, color.G, color.B), 0),
                    new GradientStop(Color.FromArgb(5, color.R, color.G, color.B), 1)
                ]
            }, null, area);

            var line = new StreamGeometry();
            using (var geometryContext = line.Open())
            {
                var first = run.Points[0].Position;
                geometryContext.BeginFigure(new Point(first.X, first.Y), false);
                foreach (var item in run.Points.Skip(1))
                {
                    var point = item.Position;
                    geometryContext.LineTo(new Point(point.X, point.Y));
                }
                geometryContext.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(new SolidColorBrush(color), 2.2,
                lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
        }
    }

    private static void DrawBatteryCurve(DrawingContext context, PowerChartLayout layout)
    {
        var pen = new Pen(new SolidColorBrush(Color.Parse("#5AA9FF")), 1.6, dashStyle: DashStyle.Dash,
            lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        foreach (var points in layout.BatteryRuns)
        {
            if (points.Count == 1)
            {
                var point = points[0].Position;
                context.DrawEllipse(new SolidColorBrush(Color.Parse("#5AA9FF")), null, new Point(point.X, point.Y), 2.5, 2.5);
                continue;
            }

            var geometry = new StreamGeometry();
            using (var geometryContext = geometry.Open())
            {
                var first = points[0].Position;
                geometryContext.BeginFigure(new Point(first.X, first.Y), false);
                foreach (var item in points.Skip(1))
                {
                    var point = item.Position;
                    geometryContext.LineTo(new Point(point.X, point.Y));
                }
                geometryContext.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }

    private static void DrawLabel(DrawingContext context, string value, double x, double y, Typeface typeface, IBrush brush)
    {
        var text = new FormattedText(value, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, 9, brush);
        context.DrawText(text, new Point(x, y));
    }
}
