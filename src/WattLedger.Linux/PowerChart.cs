using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace WattLedger.Linux;

internal sealed record PowerPoint(DateTimeOffset Timestamp, double? Watts);

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
        if (bounds.Width < 30 || bounds.Height < 30) return;

        var left = 38d;
        var right = bounds.Width - 12;
        var top = 12d;
        var bottom = bounds.Height - 22;
        var chartHeight = bottom - top;
        var measured = _samples.Where(sample => sample.Watts is not null).Select(sample => sample.Watts!.Value).ToArray();
        var max = Math.Max(5, measured.Length == 0 ? 5 : measured.Max() * 1.2);
        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#263341")), 1);
        var labelBrush = new SolidColorBrush(Color.Parse("#778597"));
        var labelTypeface = new Typeface("sans-serif");

        for (var tick = 0; tick <= 2; tick++)
        {
            var y = top + chartHeight * tick / 2;
            context.DrawLine(gridPen, new Point(left, y), new Point(right, y));
            var watts = max * (2 - tick) / 2;
            var label = new FormattedText($"{watts:0} W", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, labelTypeface, 9, labelBrush);
            context.DrawText(label, new Point(2, y - 7));
        }

        if (_samples.Count == 0) return;
        var now = DateTimeOffset.Now;
        var todayStart = new DateTimeOffset(DateTime.Today);
        var span = Math.Max(1, (now - todayStart).TotalSeconds);
        var points = _samples.Select(sample => sample.Watts is { } watts
            ? (Point?)new Point(
                left + Math.Clamp((sample.Timestamp - todayStart).TotalSeconds / span, 0, 1) * (right - left),
                bottom - Math.Clamp(watts / max, 0, 1) * chartHeight)
            : null).ToArray();

        var validPoints = points.Where(point => point.HasValue).Select(point => point!.Value).ToArray();
        if (validPoints.Length == 1)
        {
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#3DDC97")), null, validPoints[0], 3.5, 3.5);
            return;
        }

        var pen = new Pen(new SolidColorBrush(Color.Parse("#3DDC97")), 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        for (var index = 1; index < points.Length; index++)
        {
            if (points[index - 1] is { } previous && points[index] is { } current &&
                _samples[index].Timestamp - _samples[index - 1].Timestamp <= TimeSpan.FromMinutes(10))
            {
                context.DrawLine(pen, previous, current);
            }
        }
    }
}
