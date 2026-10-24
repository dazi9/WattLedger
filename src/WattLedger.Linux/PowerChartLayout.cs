namespace WattLedger.Linux;

internal readonly record struct ChartCoordinate(double X, double Y);

internal sealed record PowerChartPoint(DateTimeOffset Timestamp, double Watts, int? BatteryPercent, ChartCoordinate Position);

internal sealed record PowerChartRun(bool IsCharging, IReadOnlyList<PowerChartPoint> Points);

internal sealed record PowerChartTimeTick(DateTimeOffset Timestamp, double X, string Label);

internal sealed record PowerChartLayout(
    DateTimeOffset TimeStart,
    DateTimeOffset TimeEnd,
    DateTimeOffset? ActualStart,
    DateTimeOffset? ActualEnd,
    double PlotLeft,
    double PlotRight,
    double PlotTop,
    double PlotBottom,
    double ZeroY,
    double PowerMinimum,
    double PowerMaximum,
    IReadOnlyList<PowerChartRun> PowerRuns,
    IReadOnlyList<IReadOnlyList<PowerChartPoint>> BatteryRuns,
    IReadOnlyList<PowerChartTimeTick> TimeTicks)
{
    private static readonly double[] TimeStepsSeconds =
    [
        1, 2, 5, 10, 15, 30,
        60, 120, 300, 600, 900, 1800,
        3600, 7200, 10800, 21600, 43200, 86400
    ];

    public static PowerChartLayout Calculate(
        IReadOnlyList<PowerPoint> samples,
        double width,
        double height,
        TimeSpan? maximumGap = null,
        ChartTimeRange? viewport = null) =>
        CalculatePrepared(ChartSeries.Normalize(samples), width, height, maximumGap, viewport);

    public static PowerChartLayout CalculatePrepared(
        IReadOnlyList<PowerPoint> samples, double width, double height,
        TimeSpan? maximumGap = null, ChartTimeRange? viewport = null)
    {
        const double left = 42;
        const double right = 42;
        const double top = 16;
        const double bottom = 38;

        var plotLeft = left;
        var plotRight = Math.Max(left + 1, width - right);
        var plotTop = top;
        var plotBottom = Math.Max(top + 1, height - bottom);
        var extent = ChartSeries.Extent(samples);
        var range = viewport is { Seconds: > 0 } requested ? requested : extent;
        var gapLimit = maximumGap ?? ChartSeries.MaximumGap;
        var ordered = ClipToRange(samples, range, gapLimit);

        var measuredPower = ordered.Where(sample => sample.Watts.HasValue)
            .Select(sample => sample.Watts!.Value).ToArray();
        var measuredMinimum = measuredPower.Length == 0 ? 0 : measuredPower.Min();
        var measuredMaximum = measuredPower.Length == 0 ? 0 : measuredPower.Max();
        var powerExtent = Math.Max(5, Math.Ceiling(Math.Max(Math.Abs(measuredMinimum), Math.Abs(measuredMaximum)) * 1.15 / 5) * 5);
        var powerMinimum = measuredMinimum >= 0 ? 0 : -powerExtent;
        var powerMaximum = measuredMaximum <= 0 ? 0 : powerExtent;
        if (powerMinimum == powerMaximum) powerMaximum = 5;

        var first = ordered.Length == 0 ? range.Start : ordered[0].Timestamp;
        var last = ordered.Length == 0 ? range.End : ordered[^1].Timestamp;
        var timeStart = range.Start;
        var timeEnd = range.End;
        var spanSeconds = range.Seconds;
        var plotWidth = plotRight - plotLeft;
        var plotHeight = plotBottom - plotTop;
        var powerRange = powerMaximum - powerMinimum;
        var zeroY = plotBottom - (0 - powerMinimum) / powerRange * plotHeight;
        var points = ordered.Select(sample => new PowerChartPoint(
            sample.Timestamp,
            sample.Watts ?? 0,
            sample.BatteryPercent,
            new ChartCoordinate(
                plotLeft + (sample.Timestamp - timeStart).TotalSeconds / spanSeconds * plotWidth,
                plotBottom - ((sample.Watts ?? 0) - powerMinimum) / powerRange * plotHeight))).ToArray();

        var powerRuns = BuildRuns(ordered, points, gapLimit, zeroY)
            .Select(run => run with { Points = ReduceToPixels(run.Points) }).ToArray();
        var batteryRuns = BuildBatteryRuns(ordered, points, gapLimit, plotBottom, plotHeight)
            .Select(ReduceToPixels).ToArray();
        var ticks = BuildTimeTicks(timeStart, timeEnd, first, last, ordered.Length > 0, plotLeft, plotWidth, spanSeconds);

        return new PowerChartLayout(timeStart, timeEnd, ordered.Length == 0 ? null : first,
            ordered.Length == 0 ? null : last, plotLeft, plotRight, plotTop, plotBottom,
            zeroY, powerMinimum, powerMaximum, powerRuns, batteryRuns, ticks);
    }

    private static PowerPoint[] ClipToRange(IReadOnlyList<PowerPoint> samples, ChartTimeRange range, TimeSpan maximumGap)
    {
        var first = ChartSeries.LowerBound(samples, range.Start);
        var last = ChartSeries.LowerBound(samples, range.End);
        var result = new List<PowerPoint>();
        PowerPoint? Boundary(DateTimeOffset time, int index)
        {
            if (index == 0 || index == samples.Count || samples[index].Timestamp == time) return null;
            var a = samples[index - 1];
            var b = samples[index];
            if (b.BreakBefore || b.Timestamp - a.Timestamp > maximumGap) return null;
            var ratio = (time - a.Timestamp).TotalSeconds / (b.Timestamp - a.Timestamp).TotalSeconds;
            return new PowerPoint(time,
                a.Watts is { } aw && b.Watts is { } bw ? aw + (bw - aw) * ratio : null,
                a.BatteryPercent is { } ap && b.BatteryPercent is { } bp ? (int)Math.Round(ap + (bp - ap) * ratio) : null);
        }
        if (Boundary(range.Start, first) is { } left) result.Add(left);
        for (var i = first; i < samples.Count && samples[i].Timestamp <= range.End; i++) result.Add(samples[i]);
        if (Boundary(range.End, last) is { } right) result.Add(right);
        return result.ToArray();
    }

    // Keep temporal endpoints and both extrema per pixel column, including narrow spikes.
    // This operates on each continuous run only: missing readings and sleep gaps stay broken.
    private static IReadOnlyList<PowerChartPoint> ReduceToPixels(IReadOnlyList<PowerChartPoint> points)
    {
        if (points.Count < 4) return points;
        var result = new List<PowerChartPoint>();
        for (var first = 0; first < points.Count;)
        {
            var last = first;
            var min = first;
            var max = first;
            var pixel = Math.Floor(points[first].Position.X);
            while (last + 1 < points.Count && Math.Floor(points[last + 1].Position.X) == pixel)
            {
                last++;
                if (points[last].Position.Y < points[min].Position.Y) min = last;
                if (points[last].Position.Y > points[max].Position.Y) max = last;
            }
            foreach (var index in new[] { first, min, max, last }.Distinct().Order()) result.Add(points[index]);
            first = last + 1;
        }
        return result;
    }

    private static IReadOnlyList<PowerChartRun> BuildRuns(
        IReadOnlyList<PowerPoint> samples,
        IReadOnlyList<PowerChartPoint> points,
        TimeSpan gapLimit,
        double zeroY)
    {
        var result = new List<PowerChartRun>();
        var current = new List<PowerChartPoint>();
        bool? charging = null;

        void Flush()
        {
            if (current.Count > 0) result.Add(new PowerChartRun(charging ?? false, current.ToArray()));
            current.Clear();
            charging = null;
        }

        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i].Watts is not { } watts)
            {
                Flush();
                continue;
            }

            if (samples[i].BreakBefore || (i > 0 && samples[i].Timestamp - samples[i - 1].Timestamp > gapLimit)) Flush();
            var isCharging = watts < 0;
            var point = points[i];
            if (current.Count == 0)
            {
                charging = isCharging;
                current.Add(point);
                continue;
            }

            if (charging != isCharging)
            {
                var previous = current[^1];
                var ratio = Math.Abs(previous.Watts) / (Math.Abs(previous.Watts) + Math.Abs(watts));
                var crossing = new PowerChartPoint(
                    previous.Timestamp.AddTicks((long)((point.Timestamp - previous.Timestamp).Ticks * ratio)),
                    0,
                    null,
                    new ChartCoordinate(
                        previous.Position.X + (point.Position.X - previous.Position.X) * ratio,
                        zeroY));
                current.Add(crossing);
                Flush();
                charging = isCharging;
                current.Add(crossing);
            }

            current.Add(point);
        }

        Flush();
        return result;
    }

    private static IReadOnlyList<IReadOnlyList<PowerChartPoint>> BuildBatteryRuns(
        IReadOnlyList<PowerPoint> samples,
        IReadOnlyList<PowerChartPoint> points,
        TimeSpan gapLimit,
        double plotBottom,
        double plotHeight)
    {
        var result = new List<IReadOnlyList<PowerChartPoint>>();
        var current = new List<PowerChartPoint>();
        void Flush()
        {
            if (current.Count > 0) result.Add(current.ToArray());
            current.Clear();
        }

        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i].BatteryPercent is not { } percent)
            {
                Flush();
                continue;
            }
            if (samples[i].BreakBefore || (i > 0 && samples[i].Timestamp - samples[i - 1].Timestamp > gapLimit)) Flush();
            var point = points[i] with
            {
                Position = points[i].Position with
                {
                    Y = plotBottom - Math.Clamp(percent, 0, 100) / 100d * plotHeight
                }
            };
            current.Add(point);
        }
        Flush();
        return result;
    }

    private static IReadOnlyList<PowerChartTimeTick> BuildTimeTicks(
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset actualStart,
        DateTimeOffset actualEnd,
        bool hasSamples,
        double left,
        double width,
        double spanSeconds)
    {
        var targetStep = spanSeconds / 4;
        var step = TimeStepsSeconds.FirstOrDefault(candidate => candidate >= targetStep);
        if (step == 0) step = Math.Ceiling(targetStep / 86400) * 86400;

        var ticks = new List<PowerChartTimeTick>();
        var labelSpacing = spanSeconds >= 86400 ? 82 : spanSeconds < 300 ? 54 : 42;
        if (hasSamples)
        {
            var actualStartX = left + (actualStart - start).TotalSeconds / spanSeconds * width;
            var actualEndX = left + (actualEnd - start).TotalSeconds / spanSeconds * width;
            ticks.Add(new PowerChartTimeTick(actualStart, actualStartX, FormatTime(actualStart, spanSeconds)));
            if (actualEnd != actualStart && actualEndX - actualStartX >= labelSpacing)
                ticks.Add(new PowerChartTimeTick(actualEnd, actualEndX, FormatTime(actualEnd, spanSeconds)));
        }
        var localStart = start.ToLocalTime();
        var firstTickSeconds = Math.Ceiling(localStart.DateTime.Ticks / (step * TimeSpan.TicksPerSecond)) * step;
        var firstTick = new DateTimeOffset(new DateTime((long)(firstTickSeconds * TimeSpan.TicksPerSecond), DateTimeKind.Unspecified),
            localStart.Offset).ToUniversalTime();
        for (var tick = firstTick; tick <= end && ticks.Count < 8; tick = tick.AddSeconds(step))
        {
            if (tick < start) continue;
            var x = left + (tick - start).TotalSeconds / spanSeconds * width;
            if (ticks.Any(existing => Math.Abs(existing.X - x) < labelSpacing)) continue;
            ticks.Add(new PowerChartTimeTick(tick, x, FormatTime(tick, spanSeconds)));
        }

        if (ticks.Count == 0)
        {
            ticks.Add(new PowerChartTimeTick(start, left, start.ToLocalTime().ToString("HH:mm")));
            ticks.Add(new PowerChartTimeTick(end, left + width, end.ToLocalTime().ToString("HH:mm")));
        }

        return ticks.OrderBy(tick => tick.X).ToArray();
    }

    private static string FormatTime(DateTimeOffset timestamp, double spanSeconds)
    {
        var local = timestamp.ToLocalTime();
        return spanSeconds < 300
            ? local.ToString("HH:mm:ss")
            : spanSeconds >= 86400 ? local.ToString("MM-dd HH:mm") : local.ToString("HH:mm");
    }
}
