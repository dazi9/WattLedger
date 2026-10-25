using WattLedger.Core.Engine;
using WattLedger.Core.Models;
using WattLedger.Core.Reporting;

namespace WattLedger.Desktop;

internal sealed record DashboardSnapshot(DayReport Report, IReadOnlyList<PowerPoint> Samples);

/// <summary>Used by one background refresh at a time; only reads new samples after the initial load.</summary>
internal sealed class DashboardDataLoader(WattLedgerService service)
{
    private readonly List<SampleRecord> _samples = [];
    private DateOnly? _date;
    private DateTimeOffset _lastRead;
    private int _invalidation;
    private int _loadedVersion;

    public void Invalidate() => Interlocked.Increment(ref _invalidation);

    public DashboardSnapshot Load(DateTimeOffset now)
    {
        var date = DateOnly.FromDateTime(now.LocalDateTime);
        var version = Volatile.Read(ref _invalidation);
        if (_date != date || now < _lastRead || version != _loadedVersion)
        {
            _samples.Clear();
            _date = date;
        }

        var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local));
        // Re-read the last timestamp: SQLite stores milliseconds and multiple rows may share one.
        var from = _samples.Count == 0 ? start : _samples[^1].Timestamp;
        var incoming = service.GetSamples(from, now);
        var report = service.GetReport(date);
        if (_samples.Count > 0)
        {
            var tail = _samples.Count;
            while (tail > 0 && _samples[tail - 1].Timestamp >= from) tail--;
            _samples.RemoveRange(tail, _samples.Count - tail);
        }
        _samples.AddRange(incoming);
        _lastRead = now;
        _loadedVersion = version;

        // Precondition: sessions never overlap and the OrderBy(Start) makes gaps strictly ordered, so the single
        // cursor below is correct — the first gap ending after the previous sample is the only candidate.
        var gaps = report.Sessions.Where(s => s.Type is "SleepGap" or "ModernStandby")
            .OrderBy(s => s.Start).ToArray();
        var points = new PowerPoint[_samples.Count];
        var gapIndex = 0;
        for (var i = 0; i < _samples.Count; i++)
        {
            var sample = _samples[i];
            var breakBefore = false;
            if (i > 0)
            {
                var previous = _samples[i - 1].Timestamp;
                while (gapIndex < gaps.Length && gaps[gapIndex].End <= previous) gapIndex++;
                breakBefore = gapIndex < gaps.Length && gaps[gapIndex].Start < sample.Timestamp &&
                              gaps[gapIndex].End > previous;
            }
            points[i] = new PowerPoint(sample.Timestamp, sample.NetDischargeWatts, sample.BatteryPercent, breakBefore);
        }
        return new DashboardSnapshot(report, points);
    }
}
