using WattLedger.Core.Models;
using WattLedger.Core.Native;
using WattLedger.Core.Reporting;
using WattLedger.Core.Storage;

namespace WattLedger.Core.Engine;

public sealed class WattLedgerService : IDisposable
{
    private readonly LedgerDatabase _database;
    private readonly BatteryReader _batteryReader;
    private readonly EnergyMeterReader _energyMeter;
    private readonly BrightnessReader _brightnessReader;
    private readonly SessionTracker _sessionTracker = new();
    private readonly Queue<SampleRecord> _recentSamples = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sampleGate = new();

    private Task? _loopTask;
    private bool _initialized;
    private bool _suspended;
    private bool _disposed;
    private DateOnly? _lastHealthDate;

    public WattLedgerService(string? databasePath = null)
    {
        _database = new LedgerDatabase(databasePath);
        _batteryReader = new BatteryReader();
        _energyMeter = new EnergyMeterReader();
        _brightnessReader = new BrightnessReader();
    }

    public event EventHandler<LedgerStatus>? StatusUpdated;
    public event EventHandler<SampleRecord>? SampleRecorded;
    public event EventHandler<string>? Error;

    public LedgerStatus? Latest { get; private set; }
    public string DatabasePath => _database.DatabasePath;
    public LedgerDatabase Database => _database;
    public BatteryReader BatteryReader => _batteryReader;
    public EnergyMeterReader EnergyMeter => _energyMeter;
    public BrightnessReader BrightnessReader => _brightnessReader;

    public void Initialize()
    {
        lock (_sampleGate)
        {
            if (_initialized)
            {
                return;
            }

            _database.Initialize();
            _initialized = true;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Initialize();
        if (_loopTask is not null)
        {
            return;
        }

        _loopTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        CloseCurrentSession();
    }

    public LedgerStatus SampleOnce()
    {
        lock (_sampleGate)
        {
            EnsureInitialized();

            if (_suspended && Latest is not null)
            {
                return Latest;
            }

            try
            {
                var battery = _batteryReader.Read();
                var rapl = _energyMeter.Read();
                var brightness = _brightnessReader.Read();
                var foreground = ForegroundProcessReader.GetName();

                var sample = new SampleRecord
                {
                    Timestamp = battery.Timestamp,
                    PowerState = battery.IsAcOnline
                        ? (battery.IsCharging ? "Charging" : "AC")
                        : "Battery",
                    BatteryPercent = battery.Percent,
                    RemainingMwh = battery.RemainingCapacityMwh,
                    FullChargeMwh = battery.FullChargeCapacityMwh,
                    DesignMwh = battery.DesignCapacityMwh,
                    VoltageMv = battery.VoltageMv,
                    DischargeMw = battery.DischargeMw,
                    ChargeMw = battery.ChargeMw,
                    IsAcOnline = battery.IsAcOnline,
                    IsCharging = battery.IsCharging,
                    IsDischarging = battery.IsDischarging,
                    RaplPackageWatts = rapl.PackageWatts,
                    RaplCpuWatts = rapl.CpuWatts,
                    RaplGpuWatts = rapl.GpuWatts,
                    RaplDramWatts = rapl.DramWatts,
                    BrightnessPercent = brightness,
                    ForegroundProcess = foreground,
                    CycleCount = battery.CycleCount,
                    DeviceName = battery.DeviceName
                };

                sample = sample with { Id = _database.InsertSample(sample) };
                UpdateSessions(sample);
                MaybeRecordHealth(battery);

                _recentSamples.Enqueue(sample);
                while (_recentSamples.Count > 120)
                {
                    _recentSamples.Dequeue();
                }

                var recentAverage = ComputeRecentAverageWatts();
                var remainingHours = battery.RemainingCapacityMwh is > 0 && recentAverage is > 0.1
                    ? (double?)(battery.RemainingCapacityMwh.Value / 1000.0 / recentAverage.Value)
                    : null;

                Latest = new LedgerStatus
                {
                    Battery = battery,
                    Rapl = rapl,
                    BrightnessPercent = brightness,
                    ForegroundProcess = foreground,
                    RecentAverageWatts = recentAverage,
                    EstimatedRemainingHours = remainingHours,
                    Timestamp = sample.Timestamp
                };

                SampleRecorded?.Invoke(this, sample);
                StatusUpdated?.Invoke(this, Latest);
                return Latest;
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex.Message);
                return Latest ?? new LedgerStatus();
            }
        }
    }

    public void NotifySuspend() => _suspended = true;

    public void NotifyResume()
    {
        _suspended = false;
        _ = Task.Run(() =>
        {
            try
            {
                SampleOnce();
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex.Message);
            }
        });
    }

    public DayReport GetTodayReport() =>
        ReportBuilder.BuildDay(_database, DateOnly.FromDateTime(DateTime.Now));

    public DayReport GetReport(DateOnly date) =>
        ReportBuilder.BuildDay(_database, date);

    public IReadOnlyList<SessionRecord> GetSessions(DateTimeOffset from, DateTimeOffset to) =>
        _database.GetSessions(from, to);

    public IReadOnlyList<SampleRecord> GetSamples(DateTimeOffset from, DateTimeOffset to, int maxPoints = 0) =>
        _database.GetSamples(from, to, maxPoints);

    public IReadOnlyList<DailySummary> GetDailySummaries(DateTimeOffset from, DateTimeOffset to) =>
        _database.GetDailySummaries(from, to);

    public HealthRecord? GetLatestHealth() => _database.GetLatestHealth();

    public SampleRecord? GetLatestSample() => _database.GetLatestSample();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            LedgerStatus status;
            try
            {
                status = SampleOnce();
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex.Message);
                status = Latest ?? new LedgerStatus();
            }

            var interval = status.Battery.IsAcOnline
                ? TimeSpan.FromSeconds(30)
                : TimeSpan.FromSeconds(5);

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void UpdateSessions(SampleRecord sample)
    {
        var result = _sessionTracker.Add(sample);
        foreach (var completed in result.Completed)
        {
            _database.UpsertSession(completed);
        }

        if (result.Current is not null)
        {
            _database.UpsertSession(result.Current);
        }
    }

    private void MaybeRecordHealth(BatterySnapshot battery)
    {
        if (battery.DesignCapacityMwh is not > 0 || battery.FullChargeCapacityMwh is not > 0)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_lastHealthDate == today)
        {
            return;
        }

        if (_lastHealthDate is null)
        {
            var latest = _database.GetLatestHealth();
            if (latest is not null && DateOnly.FromDateTime(latest.Timestamp.LocalDateTime) == today)
            {
                _lastHealthDate = today;
                return;
            }
        }

        _database.InsertHealth(new HealthRecord
        {
            Timestamp = DateTimeOffset.Now,
            DesignMwh = battery.DesignCapacityMwh,
            FullChargeMwh = battery.FullChargeCapacityMwh,
            CycleCount = battery.CycleCount,
            HealthPercent = battery.HealthPercent
        });
        _lastHealthDate = today;
    }

    private double? ComputeRecentAverageWatts()
    {
        var values = _recentSamples
            .Select(s => s.NetDischargeWatts)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToArray();

        if (values.Length > 0)
        {
            return values.Average();
        }

        return EnergyIntegrator.EstimateAverageWatts(_recentSamples.ToArray());
    }

    private void CloseCurrentSession()
    {
        try
        {
            var result = _sessionTracker.Close();
            foreach (var session in result.Completed)
            {
                _database.UpsertSession(session);
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            Initialize();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore shutdown failures.
        }

        _energyMeter.Dispose();
        _cts.Dispose();
    }
}
