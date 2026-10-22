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
        TimeSpan? maximumGap = null)
    {
        const double left = 42;
        const double right = 42;
        const double top = 16;
        const double bottom = 38;
        const double minimumSpanSeconds = 60;

        var plotLeft = left;
        var plotRight = Math.Max(left + 1, width - right);
        var plotTop = top;
        var plotBottom = Math.Max(top + 1, height - bottom);
        var ordered = samples
            .Where(sample => sample.Timestamp != default)
            .OrderBy(sample => sample.Timestamp)
            .GroupBy(sample => sample.Timestamp)
            .Select(group => group.Last())
            .ToArray();

        var measuredPower = ordered.Where(sample => sample.Watts.HasValue)
            .Select(sample => sample.Watts!.Value).ToArray();
        var measuredMinimum = measuredPower.Length == 0 ? 0 : measuredPower.Min();
        var measuredMaximum = measuredPower.Length == 0 ? 0 : measuredPower.Max();
        var powerExtent = Math.Max(5, Math.Ceiling(Math.Max(Math.Abs(measuredMinimum), Math.Abs(measuredMaximum)) * 1.15 / 5) * 5);
        var powerMinimum = measuredMinimum >= 0 ? 0 : -powerExtent;
        var powerMaximum = measuredMaximum <= 0 ? 0 : powerExtent;
        if (powerMinimum == powerMaximum) powerMaximum = 5;

        var first = ordered.Length == 0 ? DateTimeOffset.Now : ordered[0].Timestamp;
        var last = ordered.Length == 0 ? first.AddSeconds(minimumSpanSeconds) : ordered[^1].Timestamp;
        var actualSpan = (last - first).TotalSeconds;
        var timeStart = first;
        var timeEnd = last;
        if (actualSpan < minimumSpanSeconds)
        {
            var padding = (minimumSpanSeconds - actualSpan) / 2;
            timeStart = first.AddSeconds(-padding);
            timeEnd = last.AddSeconds(padding);
        }

        var spanSeconds = Math.Max(minimumSpanSeconds, (timeEnd - timeStart).TotalSeconds);
        var plotWidth = plotRight - plotLeft;
        var plotHeight = plotBottom - plotTop;
        var powerRange = powerMaximum - powerMinimum;
        var zeroY = plotBottom - (0 - powerMinimum) / powerRange * plotHeight;
        // GetSamples decimates to maxPoints, so short intervals in the stored series may
        // appear much larger here. Keep this visual threshold conservative; true sleep
        // gap boundaries require session metadata from the caller.
        var gapLimit = maximumGap ?? TimeSpan.FromMinutes(10);
        var points = ordered.Select(sample => new PowerChartPoint(
            sample.Timestamp,
            sample.Watts ?? 0,
            sample.BatteryPercent,
            new ChartCoordinate(
                plotLeft + (sample.Timestamp - timeStart).TotalSeconds / spanSeconds * plotWidth,
                plotBottom - ((sample.Watts ?? 0) - powerMinimum) / powerRange * plotHeight))).ToArray();

        var powerRuns = BuildRuns(ordered, points, gapLimit, zeroY);
        var batteryRuns = BuildBatteryRuns(ordered, points, gapLimit, plotBottom, plotHeight);
        var ticks = BuildTimeTicks(timeStart, timeEnd, first, last, ordered.Length > 0, plotLeft, plotWidth, spanSeconds);

        return new PowerChartLayout(timeStart, timeEnd, ordered.Length == 0 ? null : first,
            ordered.Length == 0 ? null : last, plotLeft, plotRight, plotTop, plotBottom,
            zeroY, powerMinimum, powerMaximum, powerRuns, batteryRuns, ticks);
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

            if (i > 0 && samples[i].Timestamp - samples[i - 1].Timestamp > gapLimit) Flush();
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
            if (i > 0 && samples[i].Timestamp - samples[i - 1].Timestamp > gapLimit) Flush();
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
        if (hasSamples)
        {
            var actualStartX = left + (actualStart - start).TotalSeconds / spanSeconds * width;
            var actualEndX = left + (actualEnd - start).TotalSeconds / spanSeconds * width;
            ticks.Add(new PowerChartTimeTick(actualStart, actualStartX, FormatTime(actualStart, spanSeconds)));
            if (actualEnd != actualStart)
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
            if (ticks.Any(existing => Math.Abs(existing.X - x) < 42)) continue;
            var local = tick.ToLocalTime();
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
