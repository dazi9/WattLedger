using WattLedger.Core.Models;
using WattLedger.Core.Native;
using WattLedger.Core.Reporting;
using WattLedger.Core.Storage;

namespace WattLedger.Core.Engine;

public sealed class WattLedgerService : IDisposable
{
    private readonly LedgerDatabase _database;
    private readonly IPlatformSensorProvider _sensors;
    private readonly Lazy<BatteryReader> _batteryReader = new(() => new BatteryReader());
    private readonly Lazy<EnergyMeterReader> _energyMeter = new(() => new EnergyMeterReader());
    private readonly Lazy<BrightnessReader> _brightnessReader = new(() => new BrightnessReader());
    private readonly SessionTracker _sessionTracker = new();
    private readonly Queue<SampleRecord> _recentSamples = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sampleGate = new();
    private readonly object _lifecycleGate = new();
    private FileStream? _writerLease;
    private CancellationTokenSource? _runCancellation;

    private Task? _loopTask;
    private Task? _stopTask;
    private bool _initialized;
    private bool _suspended;
    private bool _disposed;
    private bool _stopping;
    private bool _suspendObserved;
    private bool _confirmedSuspendResume;
    private DateOnly? _lastHealthDate;

    public WattLedgerService(string? databasePath = null)
        : this(databasePath, PlatformSensorFactory.Create())
    {
    }

    internal WattLedgerService(string? databasePath, IPlatformSensorProvider sensors)
    {
        _database = new LedgerDatabase(databasePath);
        _sensors = sensors;
    }

    public event EventHandler<LedgerStatus>? StatusUpdated;
    public event EventHandler<SampleRecord>? SampleRecorded;
    public event EventHandler<string>? Error;

    public LedgerStatus? Latest { get; private set; }
    public string DatabasePath => _database.DatabasePath;
    public LedgerDatabase Database => _database;
    // Retained for existing Windows UI integrations. These Windows-only readers
    // are constructed only when explicitly requested.
    public BatteryReader BatteryReader => _batteryReader.Value;
    public EnergyMeterReader EnergyMeter => _energyMeter.Value;
    public BrightnessReader BrightnessReader => _brightnessReader.Value;

    public IReadOnlyList<BatteryDeviceSnapshot> ReadBatteryDevices() => _sensors.ReadBatteryDevices();
    public IReadOnlyDictionary<string, double> ReadRawEnergyMilliwatts() => _sensors.ReadRawEnergyMilliwatts();
    public int? ReadBrightnessForDiagnostics() => _sensors.ReadBrightness(force: true);

    /// <summary>Reads the current sensor state without initializing or writing the ledger.</summary>
    public LedgerStatus ReadStatus()
    {
        lock (_sampleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var battery = _sensors.ReadBattery();
            var rapl = _sensors.ReadRapl();
            var brightness = _sensors.ReadBrightness();
            var foreground = _sensors.ReadForegroundProcess();
            var instantaneousDischargeWatts = battery.IsDischarging ? battery.DischargeWatts : null;
            var remainingHours = battery.RemainingCapacityMwh is > 0 && instantaneousDischargeWatts is > 0.1
                ? (double?)(battery.RemainingCapacityMwh.Value / 1000.0 / instantaneousDischargeWatts.Value)
                : null;

            return new LedgerStatus
            {
                Battery = battery,
                Rapl = rapl,
                BrightnessPercent = brightness,
                ForegroundProcess = foreground,
                EstimatedRemainingHours = remainingHours,
                Timestamp = battery.Timestamp
            };
        }
    }

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
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping)
            {
                throw new InvalidOperationException("This service has already been stopped.");
            }

            Initialize();
            if (_loopTask is not null) return;

            if (!TryAcquireWriterLease())
            {
                throw new InvalidOperationException($"Another WattLedger sampler owns '{DatabasePath}'.");
            }

            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            _loopTask = Task.Run(() => RunAsync(_runCancellation.Token), CancellationToken.None);
        }
        await Task.Yield();
    }

    public Task StopAsync()
    {
        lock (_lifecycleGate)
        {
            lock (_sampleGate)
            {
                _stopping = true;
            }

            return _stopTask ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        if (!_cts.IsCancellationRequested) _cts.Cancel();
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
        _writerLease?.Dispose();
        _writerLease = null;
        _runCancellation?.Dispose();
        _runCancellation = null;
    }

    public LedgerStatus SampleOnce()
    {
        lock (_sampleGate)
        {
            if (_disposed)
            {
                return Latest ?? new LedgerStatus();
            }

            EnsureInitialized();

            if (_stopping)
            {
                throw new InvalidOperationException("The WattLedger sampler is stopping or has stopped.");
            }

            if (!TryAcquireWriterLease())
            {
                var message = $"Another WattLedger sampler owns '{DatabasePath}'.";
                Error?.Invoke(this, message);
                throw new InvalidOperationException(message);
            }

            if (_suspended && Latest is not null)
            {
                return Latest;
            }

            try
            {
                var battery = _sensors.ReadBattery();
                var trackSessions = _sensors.ShouldTrackSessions(battery);
                var rapl = _sensors.ReadRapl();
                var brightness = _sensors.ReadBrightness();
                var foreground = _sensors.ReadForegroundProcess();

                var sample = new SampleRecord
                {
                    Timestamp = battery.Timestamp,
                    PowerState = !trackSessions
                        ? "Unknown"
                        : battery.IsAcOnline
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
                if (trackSessions)
                {
                    UpdateSessions(sample, _confirmedSuspendResume);
                    _confirmedSuspendResume = false;
                }
                else
                {
                    _confirmedSuspendResume = false;
                    CloseCurrentSession();
                    _recentSamples.Clear();
                }
                MaybeRecordHealth(battery);

                if (trackSessions)
                {
                    _recentSamples.Enqueue(sample);
                    while (_recentSamples.Count > 120)
                    {
                        _recentSamples.Dequeue();
                    }
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

    public void NotifySuspend()
    {
        lock (_sampleGate)
        {
            if (!_disposed && !_stopping) _suspended = true;
            if (!_disposed && !_stopping) _suspendObserved = true;
        }
    }

    public void NotifyResume() => NotifyResume(confirmedByEvent: true);

    public void NotifyResume(bool confirmedByEvent)
    {
        lock (_sampleGate)
        {
            if (_disposed || _stopping) return;
            _confirmedSuspendResume = confirmedByEvent && _suspendObserved;
            _suspendObserved = false;
            _suspended = false;
        }
        _ = Task.Run(() =>
        {
            lock (_sampleGate)
            {
                if (_disposed || _stopping) return;
            }
            try
            {
                SampleOnce();
            }
            catch (Exception ex)
            {
                lock (_sampleGate)
                {
                    if (_disposed || _stopping) return;
                }
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

    private void UpdateSessions(SampleRecord sample, bool confirmedSuspendResume)
    {
        var result = _sessionTracker.Add(
            sample,
            _sensors.GapSessionType,
            _sensors.GapSessionType == "SleepGap" && confirmedSuspendResume);
        foreach (var completed in result.Completed)
        {
            _database.UpsertSession(completed);
        }

        if (result.Current is { DurationSeconds: > 0 })
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
        lock (_lifecycleGate)
        {
            lock (_sampleGate)
            {
                if (_disposed) return;
                _disposed = true;
            }
        }
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore shutdown failures.
        }

        _sensors.Dispose();
        if (_energyMeter.IsValueCreated) _energyMeter.Value.Dispose();
        _writerLease?.Dispose();
        _runCancellation?.Dispose();
        _cts.Dispose();
    }

    private bool TryAcquireWriterLease()
    {
        if (_writerLease is not null) return true;
        try
        {
            var leasePath = Path.GetFullPath(DatabasePath) + ".sampling.lock";
            _writerLease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Cannot acquire the sampler lease for '{DatabasePath}'.", ex);
        }
    }
}
