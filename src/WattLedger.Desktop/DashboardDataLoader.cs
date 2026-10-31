using WattLedger.Core.Engine;
using WattLedger.Core.Models;
using WattLedger.Core.Reporting;
using WattLedger.Core.Time;

namespace WattLedger.Desktop;

internal sealed record DashboardSnapshot(DayReport Report, IReadOnlyList<PowerPoint> Samples, IReadOnlyList<AppResourceRow> Apps);

/// <summary>One pre-formatted row of the desktop "apps today" panel. Energy text is empty
/// when no attribution exists (no powercap domains, or recording was off) — the UI renders
/// a placeholder, never a fabricated zero.</summary>
internal sealed record AppResourceRow(string Name, string Cpu, string Energy);

/// <summary>Used by one background refresh at a time; only reads new samples after the initial load.</summary>
internal sealed class DashboardDataLoader(WattLedgerService service)
{
    private const int MaxAppRows = 8;

    private readonly List<SampleRecord> _samples = [];
    private DateOnly? _date;
    private DateTimeOffset _lastRead;
    private int _invalidation;
    private int _loadedVersion;

    public void Invalidate() => Interlocked.Increment(ref _invalidation);

    public DashboardSnapshot Load(DateTimeOffset now)
    {
        var date = LocalDay.DateOf(now);
        var version = Volatile.Read(ref _invalidation);
        if (_date != date || now < _lastRead || version != _loadedVersion)
        {
            _samples.Clear();
            _date = date;
        }

        var start = LocalDay.Start(date);
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
        return new DashboardSnapshot(report, points, LoadAppRows(start, LocalDay.End(date)));
    }

    /// <summary>Top applications by recorded CPU time for the day, with attributed-energy
    /// text when estimates exist. Empty (not placeholders) when nothing was recorded.</summary>
    private IReadOnlyList<AppResourceRow> LoadAppRows(DateTimeOffset start, DateTimeOffset end)
    {
        var cpuTotals = service.Database.GetAppCpuTotals(start, end);
        if (cpuTotals.Count == 0)
        {
            return Array.Empty<AppResourceRow>();
        }

        var energy = service.Database.GetAppEnergyTotals(start, end)
            .GroupBy(total => total.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (Wh: group.Sum(total => total.EstimatedWh), Sources: string.Join("/", group.Select(total => total.EnergySource).Distinct())),
                StringComparer.OrdinalIgnoreCase);

        return cpuTotals.Take(MaxAppRows)
            .Select(total => energy.TryGetValue(total.Name, out var value)
                ? new AppResourceRow(
                    total.Name,
                    ReportBuilder.FormatDuration(total.CpuSeconds),
                    AppText.Format("{0:0.00} Wh · {1}", value.Wh, value.Sources))
                : new AppResourceRow(total.Name, ReportBuilder.FormatDuration(total.CpuSeconds), ""))
            .ToArray();
    }
}
