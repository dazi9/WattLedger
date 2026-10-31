using System.Diagnostics;
using WattLedger.Core.Native;
using WattLedger.Core.Storage;

namespace WattLedger.Core.Processes;

/// <summary>Bridges the live process CPU layer to storage: each <see cref="Collect"/> is one
/// sampling window (diff → group), windows are aggregated in memory, and one row per
/// application is written when the wall clock rolls into a new minute. Single-sampler; the
/// caller owns the cadence (the roadmap starts at the sampler's 5-second rhythm).
/// With an optional energy-domain reader, the same windows are attributed through
/// <see cref="AppEnergyAttribution"/> and persisted as labelled estimates — never as
/// measured ledger totals.</summary>
public sealed class ProcessResourceRecorder
{
    private readonly LedgerDatabase _database;
    private readonly IProcessResourceProvider _provider;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<IReadOnlyList<EnergyDomainSnapshot>>? _readEnergyDomains;
    private readonly PowercapEnergyTracker? _energyTracker;
    private readonly ProcessCpuTracker _tracker = new();

    private DateTimeOffset? _minute;
    private readonly Dictionary<string, (double CpuSeconds, int MaxProcessCount, int Windows)> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (double Wh, double CpuSeconds, string Source, int Windows)> _pendingEnergy = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after a minute's aggregated rows were written.</summary>
    public event Action<DateTimeOffset, IReadOnlyList<AppCpuSample>>? MinuteRecorded;

    /// <summary>Raised after a minute's attribution rows were written (only when an
    /// energy-domain reader was supplied and the window produced estimates).</summary>
    public event Action<DateTimeOffset, IReadOnlyList<AppEnergySample>>? EnergyRecorded;

    public ProcessResourceRecorder(
        LedgerDatabase database,
        IProcessResourceProvider provider,
        Func<DateTimeOffset>? clock = null,
        Func<IReadOnlyList<EnergyDomainSnapshot>>? readEnergyDomains = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _clock = clock ?? (() => DateTimeOffset.Now);
        _readEnergyDomains = readEnergyDomains;
        _energyTracker = readEnergyDomains is null ? null : new PowercapEnergyTracker();
    }

    /// <summary>Performs one sampling window and returns the live per-application usage.
    /// Zero-usage instances (new processes) are returned for coverage display but not stored.</summary>
    public IReadOnlyList<AppCpuUsage> Collect()
    {
        var window = Stopwatch.StartNew();
        var usages = _tracker.Collect(_provider.ReadCpuSnapshots());
        window.Stop();

        var apps = ProcessAppGrouper.Group(usages);
        var windowEnd = _clock();
        var minute = MinuteKey(windowEnd);
        if (_minute is { } openMinute && openMinute != minute)
        {
            WriteMinute(openMinute);
        }

        _minute ??= minute;
        foreach (var app in apps)
        {
            if (app.CpuSeconds <= 0)
            {
                continue;
            }

            var (cpu, maxProcesses, windows) = _pending.TryGetValue(app.Name, out var existing)
                ? existing
                : (0.0, 0, 0);
            _pending[app.Name] = (cpu + app.CpuSeconds, Math.Max(maxProcesses, app.ProcessCount), windows + 1);
        }

        if (_readEnergyDomains is { } readDomains && _energyTracker is { } energyTracker)
        {
            var attribution = AppEnergyAttribution.Attribute(apps, energyTracker.Collect(readDomains()));
            if (attribution is not null)
            {
                foreach (var estimate in attribution.Estimates)
                {
                    var (wh, cpu, source, windows) = _pendingEnergy.TryGetValue(estimate.AppName, out var existing)
                        ? existing
                        : (0.0, 0.0, attribution.EnergySource, 0);
                    _pendingEnergy[estimate.AppName] =
                        (wh + estimate.EstimatedWh, cpu + estimate.CpuSeconds, source, windows + 1);
                }
            }
        }

        return apps;
    }

    /// <summary>Writes the open minute's buffers; call once when sampling stops.</summary>
    public void Flush()
    {
        if (_minute is { } openMinute)
        {
            WriteMinute(openMinute);
        }
    }

    private void WriteMinute(DateTimeOffset minute)
    {
        if (_pending.Count > 0)
        {
            var samples = _pending
                .Select(entry => new AppCpuSample(minute, entry.Key, entry.Value.CpuSeconds, entry.Value.MaxProcessCount, entry.Value.Windows))
                .OrderByDescending(sample => sample.CpuSeconds)
                .ToArray();
            _database.InsertAppCpuSamples(samples);
            MinuteRecorded?.Invoke(minute, samples);
            _pending.Clear();
        }

        if (_pendingEnergy.Count > 0)
        {
            var estimates = _pendingEnergy
                .Select(entry => new AppEnergySample(minute, entry.Key, entry.Value.Wh, entry.Value.CpuSeconds, entry.Value.Source, AppEnergyAttribution.ModelVersion, entry.Value.Windows))
                .OrderByDescending(sample => sample.EstimatedWh)
                .ToArray();
            _database.InsertAppEnergySamples(estimates);
            EnergyRecorded?.Invoke(minute, estimates);
            _pendingEnergy.Clear();
        }

        _minute = null;
    }

    /// <summary>Floors a timestamp to its minute boundary (offset preserved), the storage
    /// key shared by the recorder and any writer of single-window rows.</summary>
    public static DateTimeOffset MinuteKey(DateTimeOffset timestamp) => new(
        timestamp.Year, timestamp.Month, timestamp.Day, timestamp.Hour, timestamp.Minute, 0,
        timestamp.Offset);
}
