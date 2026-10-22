namespace WattLedger.Linux;

internal readonly record struct ChartTimeRange(DateTimeOffset Start, DateTimeOffset End)
{
    public double Seconds => (End - Start).TotalSeconds;

    public ChartTimeRange Zoom(double factor, double anchor, ChartTimeRange extent)
    {
        if (!double.IsFinite(factor) || factor <= 0) return this;
        anchor = Math.Clamp(anchor, 0, 1);
        var seconds = Math.Clamp(Seconds * factor, Math.Min(10, extent.Seconds), extent.Seconds);
        var start = Start.AddSeconds((Seconds - seconds) * anchor);
        return new ChartTimeRange(start, start.AddSeconds(seconds)).Clamp(extent);
    }

    public ChartTimeRange Pan(double seconds, ChartTimeRange extent) =>
        new ChartTimeRange(Start.AddSeconds(seconds), End.AddSeconds(seconds)).Clamp(extent);

    private ChartTimeRange Clamp(ChartTimeRange extent)
    {
        var seconds = Math.Min(Seconds, extent.Seconds);
        var start = Start < extent.Start ? extent.Start : Start;
        if (start.AddSeconds(seconds) > extent.End) start = extent.End.AddSeconds(-seconds);
        return new ChartTimeRange(start, start.AddSeconds(seconds));
    }
}

internal sealed record ChartSelectionStatistics(
    DateTimeOffset Start, DateTimeOffset End, int SampleCount,
    double CoveredSeconds, double NetEnergyWh, double DischargeEnergyWh, double ChargeEnergyWh,
    double? AverageWatts, double? MinimumWatts, double? MaximumWatts)
{
    public double DurationSeconds => (End - Start).TotalSeconds;
    public double Coverage => DurationSeconds > 0 ? CoveredSeconds / DurationSeconds : 0;
}

/// <summary>Original samples used for inspection and integration; display reduction never changes these.</summary>
internal static class ChartSeries
{
    public static readonly TimeSpan MaximumGap = TimeSpan.FromSeconds(60);

    public static PowerPoint[] Normalize(IReadOnlyList<PowerPoint> samples) => samples
        .Where(sample => sample.Timestamp != default)
        .OrderBy(sample => sample.Timestamp)
        .GroupBy(sample => sample.Timestamp)
        .Select(group => group.Last() with { BreakBefore = group.Any(sample => sample.BreakBefore) })
        .Select(sample => sample with
        {
            Watts = sample.Watts is { } watts && double.IsFinite(watts) ? watts : null,
            BatteryPercent = sample.BatteryPercent is >= 0 and <= 100 ? sample.BatteryPercent : null
        }).ToArray();

    public static ChartTimeRange Extent(IReadOnlyList<PowerPoint> samples)
    {
        var first = samples.Count == 0 ? DateTimeOffset.Now : samples[0].Timestamp;
        var last = samples.Count == 0 ? first : samples[^1].Timestamp;
        var padding = Math.Max(0, 60 - (last - first).TotalSeconds) / 2;
        return new ChartTimeRange(first.AddSeconds(-padding), last.AddSeconds(padding));
    }

    public static int LowerBound(IReadOnlyList<PowerPoint> samples, DateTimeOffset time)
    {
        var low = 0;
        var high = samples.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (samples[mid].Timestamp < time) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    public static PowerPoint? Nearest(IReadOnlyList<PowerPoint> samples, DateTimeOffset time, ChartTimeRange? range = null)
    {
        if (samples.Count == 0) return null;
        var first = range is { } bounds ? LowerBound(samples, bounds.Start) : 0;
        var end = range is { } boundsEnd ? LowerBound(samples, boundsEnd.End) : samples.Count;
        if (range is { } r && end < samples.Count && samples[end].Timestamp == r.End) end++;
        if (first >= end) return null;
        var index = LowerBound(samples, time);
        if (index >= end) return samples[end - 1];
        if (index <= first) return samples[first];
        return time - samples[index - 1].Timestamp <= samples[index].Timestamp - time
            ? samples[index - 1] : samples[index];
    }

    public static ChartSelectionStatistics Summarize(IReadOnlyList<PowerPoint> ordered, DateTimeOffset a, DateTimeOffset b)
    {
        var start = a <= b ? a : b;
        var end = a <= b ? b : a;
        var first = LowerBound(ordered, start);
        var last = LowerBound(ordered, end);
        var count = last - first + (last < ordered.Count && ordered[last].Timestamp == end ? 1 : 0);
        double covered = 0, discharge = 0, charge = 0;
        double? minimum = null, maximum = null;
        void Observe(double watts)
        {
            minimum = minimum.HasValue ? Math.Min(minimum.Value, watts) : watts;
            maximum = maximum.HasValue ? Math.Max(maximum.Value, watts) : watts;
        }
        for (var i = first; i < first + count; i++)
            if (ordered[i].Watts is { } watts && double.IsFinite(watts)) Observe(watts);

        for (var i = Math.Max(1, first); i < ordered.Count && ordered[i - 1].Timestamp < end; i++)
        {
            var previous = ordered[i - 1];
            var next = ordered[i];
            var gap = next.Timestamp - previous.Timestamp;
            if (next.BreakBefore || gap <= TimeSpan.Zero || gap > MaximumGap ||
                previous.Watts is not { } w0 || next.Watts is not { } w1 ||
                !double.IsFinite(w0) || !double.IsFinite(w1)) continue;
            var from = previous.Timestamp < start ? start : previous.Timestamp;
            var to = next.Timestamp > end ? end : next.Timestamp;
            if (to <= from) continue;
            var left = w0 + (w1 - w0) * (from - previous.Timestamp).TotalSeconds / gap.TotalSeconds;
            var right = w0 + (w1 - w0) * (to - previous.Timestamp).TotalSeconds / gap.TotalSeconds;
            Observe(left);
            Observe(right);
            var seconds = (to - from).TotalSeconds;
            covered += seconds;
            if (left >= 0 && right >= 0) discharge += (left + right) * .5 * seconds / 3600;
            else if (left <= 0 && right <= 0) charge -= (left + right) * .5 * seconds / 3600;
            else
            {
                var leftSeconds = seconds * Math.Abs(left) / (Math.Abs(left) + Math.Abs(right));
                var leftEnergy = left * .5 * leftSeconds / 3600;
                var rightEnergy = right * .5 * (seconds - leftSeconds) / 3600;
                discharge += Math.Max(0, leftEnergy) + Math.Max(0, rightEnergy);
                charge -= Math.Min(0, leftEnergy) + Math.Min(0, rightEnergy);
            }
        }
        var net = discharge - charge;
        return new(start, end, count, covered, net, discharge, charge,
            covered > 0 ? net * 3600 / covered : null, minimum, maximum);
    }
}
