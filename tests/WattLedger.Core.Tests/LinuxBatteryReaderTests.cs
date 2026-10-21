using Microsoft.Data.Sqlite;
using WattLedger.Core.Engine;
using WattLedger.Core.Models;
using WattLedger.Core.Native;
using WattLedger.Core.Storage;

namespace WattLedger.Core.Tests;

public sealed class LinuxBatteryReaderTests
{
    [Fact]
    public void ReadsEnergyAndPowerInMilliUnits()
    {
        using var fixture = new SysfsFixture();
        fixture.Battery("BAT0", ("energy_now", "4567890"), ("energy_full", "8000000"),
            ("energy_full_design", "9000000"), ("power_now", "5432000"), ("voltage_now", "12000000"),
            ("status", "Discharging"), ("cycle_count", "0"));

        var snapshot = new LinuxBatteryReader(fixture.Root).Read();
        var device = Assert.Single(new LinuxBatteryReader(fixture.Root).ReadDevices());

        Assert.Equal(4_567, snapshot.RemainingCapacityMwh);
        Assert.Equal(8_000, snapshot.FullChargeCapacityMwh);
        Assert.Equal(9_000, snapshot.DesignCapacityMwh);
        Assert.Equal(12_000, snapshot.VoltageMv);
        Assert.Equal(5_432, snapshot.DischargeMw);
        Assert.Equal(0, snapshot.CycleCount);
        Assert.Equal(0, device.CycleCount);
        Assert.Equal(57, snapshot.Percent);
    }

    [Fact]
    public void UsesCurrentTimesVoltageWhenPowerNowIsMissing()
    {
        using var fixture = new SysfsFixture();
        fixture.Battery("BAT0", ("current_now", "1500000"), ("voltage_now", "12000000"),
            ("status", "Discharging"));

        var snapshot = new LinuxBatteryReader(fixture.Root).Read();

        Assert.Equal(18_000, snapshot.DischargeMw);
        Assert.Null(snapshot.RemainingCapacityMwh);
        Assert.Null(snapshot.FullChargeCapacityMwh);
    }

    [Fact]
    public void ChargingUsesSharedPowerFlagsAndFallsBackToAcOnline()
    {
        using var fixture = new SysfsFixture();
        fixture.Battery("BAT0", ("power_now", "2250000"), ("status", "Charging"));

        var reader = new LinuxBatteryReader(fixture.Root);
        var snapshot = reader.Read();
        var device = Assert.Single(reader.ReadDevices());

        Assert.True(device.IsCharging);
        Assert.False(device.IsDischarging);
        Assert.Equal(2_250, snapshot.ChargeMw);
        Assert.True(snapshot.IsAcOnline);
    }

    [Fact]
    public void SignedPowerAndCurrentUseMagnitudeWithStatusDirectionAndGuardLongMinValue()
    {
        using var fixture = new SysfsFixture();
        fixture.Battery("BAT0", ("power_now", "-2345000"), ("current_now", "-1500000"),
            ("voltage_now", "12000000"), ("status", "Discharging"));

        var powerNow = new LinuxBatteryReader(fixture.Root).Read();
        Assert.Equal(2_345, powerNow.DischargeMw);

        fixture.RemoveProperty("BAT0", "power_now");
        var currentNow = new LinuxBatteryReader(fixture.Root).Read();
        Assert.Equal(18_000, currentNow.DischargeMw);

        fixture.Battery("BAT1", ("power_now", "-9223372036854775808"), ("status", "Discharging"));
        var overflow = new LinuxBatteryReader(fixture.Root).ReadDevices();
        Assert.Null(Assert.Single(overflow, d => d.DevicePath.EndsWith("BAT1", StringComparison.Ordinal)).RateMw);
    }

    [Fact]
    public void UnknownBatteryStatusIsNotTreatedAsDischarging()
    {
        using var fixture = new SysfsFixture();
        fixture.Battery("BAT0", ("energy_now", "4000000"), ("energy_full", "8000000"), ("status", "Unknown"));

        var snapshot = new LinuxBatteryReader(fixture.Root).Read();

        Assert.Equal(1, snapshot.BatteryCount);
        Assert.False(snapshot.IsAcOnline);
        Assert.False(snapshot.IsDischarging);
        Assert.False(PlatformSensorFactory.LinuxShouldTrackSessions(snapshot));
    }

    [Fact]
    public void ChargeOnlySysfsReportsPercentWithoutInventingEnergyCounters()
    {
        using var fixture = new SysfsFixture();
        fixture.Battery("BAT0", ("charge_now", "4000000"), ("charge_full", "8000000"),
            ("charge_full_design", "9000000"), ("status", "Unknown"));

        var snapshot = new LinuxBatteryReader(fixture.Root).Read();

        Assert.Equal(50, snapshot.Percent);
        Assert.True(snapshot.PercentIsEstimated);
        Assert.Null(snapshot.RemainingCapacityMwh);
        Assert.Null(snapshot.FullChargeCapacityMwh);
        Assert.Null(snapshot.DesignCapacityMwh);
    }

    [Fact]
    public void SumsCompleteEnergyCountersAndLeavesPartialAggregateUnknown()
    {
        using var fixture = new SysfsFixture();
        fixture.Battery("BAT0", ("energy_now", "4000000"), ("energy_full", "8000000"), ("capacity", "50"));
        fixture.Battery("BAT1", ("energy_now", "6000000"), ("energy_full", "12000000"), ("capacity", "50"));

        var complete = new LinuxBatteryReader(fixture.Root).Read();
        Assert.Equal(10_000, complete.RemainingCapacityMwh);
        Assert.Equal(20_000, complete.FullChargeCapacityMwh);
        Assert.Equal(50, complete.Percent);

        fixture.RemoveProperty("BAT1", "energy_now");
        var partial = new LinuxBatteryReader(fixture.Root).Read();
        Assert.Null(partial.RemainingCapacityMwh);
        Assert.Equal(20_000, partial.FullChargeCapacityMwh);
        Assert.Null(partial.Percent);
    }

    [Fact]
    public void IncludesZeroCountersAndFiltersDeviceScopedOrAbsentSupplies()
    {
        using var fixture = new SysfsFixture();
        fixture.Battery("BAT0", ("energy_now", "0"), ("energy_full", "0"), ("capacity", "0"));
        fixture.Battery("BAT1", ("energy_now", "1000000"), ("energy_full", "2000000"), ("scope", "Device"));
        fixture.Battery("BAT2", ("present", "0"), ("capacity", "99"));

        var snapshot = new LinuxBatteryReader(fixture.Root).Read();

        Assert.Equal(1, snapshot.BatteryCount);
        Assert.Equal(0L, snapshot.RemainingCapacityMwh);
        Assert.Equal(0L, snapshot.FullChargeCapacityMwh);
        Assert.Equal(0, snapshot.Percent);
    }

    [Fact]
    public void ZeroRemainingEnergyIsAValidMeasuredZeroPercent()
    {
        using var fixture = new SysfsFixture();
        fixture.Battery("BAT0", ("energy_now", "0"), ("energy_full", "8000000"));

        var snapshot = new LinuxBatteryReader(fixture.Root).Read();

        Assert.Equal(0, snapshot.Percent);
        Assert.Equal(0L, snapshot.RemainingCapacityMwh);
        Assert.Equal(8_000L, snapshot.FullChargeCapacityMwh);
        Assert.False(snapshot.PercentIsEstimated);
    }

    [Fact]
    public void MissingSupplyDirectoryReturnsUnknownWithoutThrowing()
    {
        using var fixture = new SysfsFixture();

        var snapshot = new LinuxBatteryReader(Path.Combine(fixture.Root, "missing")).Read();

        Assert.Equal(0, snapshot.BatteryCount);
        Assert.Null(snapshot.Percent);
        Assert.Null(snapshot.RemainingCapacityMwh);
    }

    [Fact]
    public void DefaultDatabasePathPreservesWindowsAndUsesAbsoluteXdgDataHome()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wattledger-path-fixture"));
        var localAppData = Path.Combine(root, "Local");
        var dataHome = Path.Combine(root, "data");
        var userProfile = Path.Combine(root, "home", "tester");

        Assert.Equal(Path.Combine(localAppData, "WattLedger", "wattledger.db"),
            LedgerDatabase.GetDefaultDatabasePath(true, localAppData, dataHome, userProfile));
        Assert.Equal(Path.Combine(dataHome, "wattledger", "wattledger.db"),
            LedgerDatabase.GetDefaultDatabasePath(false, "", dataHome, userProfile));
        Assert.Equal(Path.Combine(userProfile, ".local", "share", "wattledger", "wattledger.db"),
            LedgerDatabase.GetDefaultDatabasePath(false, "", Path.Combine("relative", "data"), userProfile));
    }

    [Fact]
    public void NoBatterySampleBreaksSessionContinuityAndDoesNotInventSleepEnergy()
    {
        using var fixture = new SysfsFixture();
        var start = DateTimeOffset.UtcNow;
        var provider = new FakeSensorProvider(
            TrackBattery,
            Battery(start, count: 1, percent: 80, remaining: 40_000),
            Battery(start.AddMinutes(1), count: 0),
            Battery(start.AddHours(3), count: 1, percent: 70, remaining: 35_000));
        using var service = new WattLedgerService(fixture.DatabasePath, provider);

        service.SampleOnce();
        var missing = service.SampleOnce();
        service.SampleOnce();

        var stored = service.GetSamples(start.AddDays(-1), start.AddDays(1));
        Assert.Equal("Unknown", stored[1].PowerState);
        Assert.Equal("Battery", stored[2].PowerState);
        Assert.Equal(70, service.GetLatestSample()!.BatteryPercent);
        Assert.Null(missing.Battery.Percent);
        Assert.Empty(service.GetSessions(start.AddDays(-1), start.AddDays(1)));
    }

    [Fact]
    public void WindowsFallbackBatteryPercentStillAllowsSessionTrackingWithoutIoctlDevices()
    {
        using var fixture = new SysfsFixture();
        var start = DateTimeOffset.UtcNow;
        var provider = new FakeSensorProvider(
            TrackFallback,
            Battery(start, count: 0, percent: 62),
            Battery(start.AddSeconds(10), count: 0, percent: 61));
        using var service = new WattLedgerService(fixture.DatabasePath, provider);

        service.SampleOnce();
        service.SampleOnce();

        var session = Assert.Single(service.GetSessions(start.AddMinutes(-1), start.AddMinutes(1)));
        Assert.Equal("Battery", session.Type);
        Assert.Equal(62, session.StartPercent);
    }

    [Fact]
    public void WindowsSystemFallbackKeepsSessionsWhenIoctlFindsNoDevices()
    {
        Assert.True(PlatformSensorFactory.WindowsShouldTrackSessions(new BatterySnapshot { Percent = 45 }));
        Assert.True(PlatformSensorFactory.WindowsShouldTrackSessions(new BatterySnapshot { IsAcOnline = true }));
        Assert.False(PlatformSensorFactory.WindowsShouldTrackSessions(new BatterySnapshot()));
    }

    [Fact]
    public void LinuxSleepGapIsMarkedInferredAndIncludedInDailySummaries()
    {
        using var fixture = new SysfsFixture();
        var start = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
        var provider = new FakeSensorProvider(
            TrackBattery,
            Battery(start, count: 1, percent: 70),
            Battery(start.AddHours(1), count: 1, percent: 70));
        using var service = new WattLedgerService(fixture.DatabasePath, provider);

        service.SampleOnce();
        service.SampleOnce();

        var gap = Assert.Single(service.GetSessions(start.AddDays(-1), start.AddDays(1)));
        Assert.Equal("SleepGap", gap.Type);
        Assert.Contains("Inferred sleep gap", gap.Note);
        Assert.Contains("battery energy unavailable", gap.Note);
        var daily = Assert.Single(service.GetDailySummaries(start.AddDays(-1), start.AddDays(1)));
        Assert.Equal(3_600, daily.StandbySeconds);
        Assert.Equal(0d, daily.StandbyWh);
        Assert.True(daily.StandbyEnergyIncomplete);
        Assert.True(service.GetReport(DateOnly.FromDateTime(start.DateTime)).StandbyEnergyIncomplete);

        service.Database.UpsertSession(new SessionRecord
        {
            Start = start.AddHours(-1),
            End = start.AddMinutes(-30),
            Type = "ModernStandby",
            EnergyWh = 1,
            AverageWatts = 2,
            PeakWatts = 2
        });
        var withHistoricalStandby = Assert.Single(service.GetDailySummaries(start.AddDays(-1), start.AddDays(1)));
        Assert.Equal(5_400, withHistoricalStandby.StandbySeconds);
        Assert.Equal(1d, withHistoricalStandby.StandbyWh);
    }

    [Fact]
    public async Task SuspendAndResumeNotificationsMarkTheSleepGapAsConfirmed()
    {
        using var fixture = new SysfsFixture();
        var start = DateTimeOffset.UtcNow;
        var provider = new FakeSensorProvider(
            TrackBattery,
            Battery(start, count: 1, percent: 70),
            Battery(start.AddHours(1), count: 1, percent: 69));
        using var service = new WattLedgerService(fixture.DatabasePath, provider);
        var sampleCount = 0;
        var resumedSample = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SampleRecorded += (_, _) =>
        {
            if (Interlocked.Increment(ref sampleCount) == 2) resumedSample.TrySetResult();
        };
        service.SampleOnce();

        service.NotifySuspend();
        service.NotifyResume();
        await resumedSample.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var gap = Assert.Single(service.GetSessions(start.AddDays(-1), start.AddDays(1)));
        Assert.Equal("SleepGap", gap.Type);
        Assert.Contains("Suspend/resume event confirmed", gap.Note);
    }

    [Fact]
    public async Task UnconfirmedResumeUsesInferredGapNote()
    {
        using var fixture = new SysfsFixture();
        var start = DateTimeOffset.UtcNow;
        var provider = new FakeSensorProvider(
            TrackBattery,
            Battery(start, count: 1, percent: 70),
            Battery(start.AddHours(1), count: 1, percent: 69));
        using var service = new WattLedgerService(fixture.DatabasePath, provider);
        var sampleCount = 0;
        var resumedSample = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SampleRecorded += (_, _) =>
        {
            if (Interlocked.Increment(ref sampleCount) == 2) resumedSample.TrySetResult();
        };
        service.SampleOnce();

        service.NotifySuspend();
        service.NotifyResume(confirmedByEvent: false);
        await resumedSample.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var gap = Assert.Single(service.GetSessions(start.AddDays(-1), start.AddDays(1)));
        Assert.Equal("SleepGap", gap.Type);
        Assert.Contains("Inferred sleep gap", gap.Note);
        Assert.DoesNotContain("Suspend/resume event confirmed", gap.Note);
    }

    [Fact]
    public async Task WriterLeaseConflictIsReportedBySamplingAndStart()
    {
        using var fixture = new SysfsFixture();
        var provider1 = new FakeSensorProvider(TrackBattery, Battery(DateTimeOffset.UtcNow, count: 1, percent: 50));
        var provider2 = new FakeSensorProvider(TrackBattery, Battery(DateTimeOffset.UtcNow, count: 1, percent: 50));
        using var first = new WattLedgerService(fixture.DatabasePath, provider1);
        using var second = new WattLedgerService(fixture.DatabasePath, provider2);
        first.SampleOnce();
        string? error = null;
        second.Error += (_, message) => error = message;

        var sampleError = Assert.Throws<InvalidOperationException>(() => second.SampleOnce());
        var startError = await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync());

        Assert.Contains("Another WattLedger sampler owns", sampleError.Message);
        Assert.Contains("Another WattLedger sampler owns", startError.Message);
        Assert.Equal(sampleError.Message, error);
        Assert.Empty(second.GetReport(DateOnly.FromDateTime(DateTime.Now)).Sessions);
    }

    [Fact]
    public async Task StopCancelsLoopAndResumeAfterStopDoesNotReadSensors()
    {
        using var fixture = new SysfsFixture();
        var provider = new FakeSensorProvider(TrackBattery, Battery(DateTimeOffset.UtcNow, count: 1, percent: 50));
        using var service = new WattLedgerService(fixture.DatabasePath, provider);

        await service.StartAsync();
        await provider.FirstRead.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        service.NotifyResume();
        await Task.Delay(50);

        Assert.Equal(1, provider.ReadCount);
        Assert.Throws<InvalidOperationException>(() => service.SampleOnce());
    }

    [Fact]
    public void ResumeAfterDisposeDoesNotReadSensors()
    {
        using var fixture = new SysfsFixture();
        var provider = new FakeSensorProvider(TrackBattery, Battery(DateTimeOffset.UtcNow, count: 1, percent: 50));
        var service = new WattLedgerService(fixture.DatabasePath, provider);
        service.Dispose();

        service.NotifyResume();

        Assert.Equal(0, provider.ReadCount);
    }

    private static bool TrackBattery(BatterySnapshot battery) =>
        battery.BatteryCount > 0 && (battery.IsCharging || battery.IsDischarging || battery.IsAcOnline);
    private static bool TrackFallback(BatterySnapshot battery) => battery.BatteryCount > 0 || battery.Percent is not null || battery.IsAcOnline;

    private static BatterySnapshot Battery(
        DateTimeOffset timestamp,
        int count,
        int? percent = null,
        long? remaining = null) => new()
        {
            Timestamp = timestamp,
            BatteryCount = count,
            Percent = percent,
            RemainingCapacityMwh = remaining,
            IsDischarging = count > 0
        };

    private sealed class FakeSensorProvider(
        Func<BatterySnapshot, bool> shouldTrack,
        params BatterySnapshot[] batteries) : IPlatformSensorProvider
    {
        private int _index;
        private int _readCount;
        public int ReadCount => Volatile.Read(ref _readCount);
        private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task FirstRead => _firstRead.Task;
        public BatterySnapshot ReadBattery()
        {
            Interlocked.Increment(ref _readCount);
            _firstRead.TrySetResult();
            var index = Math.Min(_index++, batteries.Length - 1);
            return batteries[index];
        }
        public IReadOnlyList<BatteryDeviceSnapshot> ReadBatteryDevices() => Array.Empty<BatteryDeviceSnapshot>();
        public RaplSnapshot ReadRapl() => RaplSnapshot.Unavailable;
        public IReadOnlyDictionary<string, double> ReadRawEnergyMilliwatts() => new Dictionary<string, double>();
        public int? ReadBrightness(bool force = false) => null;
        public string? ReadForegroundProcess() => null;
        public string GapSessionType => "SleepGap";
        public bool ShouldTrackSessions(BatterySnapshot battery) => shouldTrack(battery);
        public void Dispose() { }
    }

    private sealed class SysfsFixture : IDisposable
    {
        public SysfsFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"wattledger-sysfs-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            DatabasePath = Path.Combine(Root, "ledger.db");
        }

        public string Root { get; }
        public string DatabasePath { get; }

        public string Battery(string name, params (string Property, string Value)[] properties)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            Write(path, "type", "Battery");
            foreach (var (property, value) in properties) Write(path, property, value);
            return path;
        }

        public void RemoveProperty(string battery, string property) =>
            File.Delete(Path.Combine(Root, battery, property));

        private static void Write(string path, string property, string value) =>
            File.WriteAllText(Path.Combine(path, property), value);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
