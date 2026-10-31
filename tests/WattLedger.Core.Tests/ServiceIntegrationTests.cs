using Microsoft.Data.Sqlite;
using WattLedger.Core.Engine;
using WattLedger.Core.Models;
using WattLedger.Core.Native;
using WattLedger.Core.Time;

namespace WattLedger.Core.Tests;

public sealed class ServiceIntegrationTests
{
    [Fact]
    public void SampleOnce_records_samples_and_keeps_a_battery_session()
    {
        using var fixture = new ServiceFixture();
        var start = fixture.LocalMorning;
        fixture.Sensors.Enqueue(Battery(start, discharge: 6_000, remaining: 60_000));
        fixture.Sensors.Enqueue(Battery(start.AddSeconds(5), discharge: 6_000, remaining: 59_900));

        fixture.Service.SampleOnce();
        fixture.Service.SampleOnce();

        Assert.Equal(2, fixture.Service.Database.GetSampleCount());
        var session = Assert.Single(fixture.Service.GetSessions(start.AddSeconds(-1), start.AddMinutes(1)));
        Assert.Equal("Battery", session.Type);
        Assert.Equal(start, session.Start);
        Assert.Equal(start.AddSeconds(5), session.End);
        Assert.Equal(5, session.DurationSeconds);
        Assert.Equal(6.0, session.AverageWatts, 6);
        Assert.Equal(6, session.PeakWatts, 6);

        var report = fixture.Service.GetReport(DateOnly.FromDateTime(start.LocalDateTime));
        Assert.True(report.DischargeWh > 0);
        Assert.Equal(6.0 * 5.0 / 3600.0, report.DischargeWh, 6);
    }

    [Fact]
    public void ReadStatus_does_not_create_the_database_or_take_the_writer_lease()
    {
        using var fixture = new ServiceFixture();
        fixture.Sensors.Enqueue(Battery(fixture.LocalMorning, discharge: 6_000, remaining: 60_000));

        var status = fixture.Service.ReadStatus();

        Assert.Equal(6.0, status.Battery.DischargeWatts!.Value, 6);
        Assert.False(File.Exists(fixture.Service.DatabasePath), "status is a sensor-only read and must not create the ledger");
        Assert.False(File.Exists(fixture.Service.DatabasePath + ".sampling.lock"));
    }

    [Fact]
    public async Task StartAsync_fails_when_another_sampler_holds_the_writer_lease()
    {
        using var fixture = new ServiceFixture();
        await fixture.Service.StartAsync();

        using var second = new WattLedgerService(fixture.DatabasePath, new FakeSensorProvider());
        var error = Assert.Throws<InvalidOperationException>(() => second.StartAsync().GetAwaiter().GetResult());
        Assert.Contains("Another WattLedger sampler owns", error.Message);
        Assert.Contains(fixture.DatabasePath, error.Message);

        await fixture.Service.StopAsync();
    }

    [Fact]
    public async Task StopAsync_closes_the_current_session_and_rejects_further_samples()
    {
        using var fixture = new ServiceFixture();
        var start = fixture.LocalMorning;
        fixture.Sensors.Enqueue(Battery(start, discharge: 6_000, remaining: 60_000));
        fixture.Sensors.Enqueue(Battery(start.AddSeconds(5), discharge: 8_000, remaining: 59_900));
        fixture.Service.SampleOnce();
        fixture.Service.SampleOnce();

        await fixture.Service.StopAsync();

        var session = Assert.Single(fixture.Service.GetSessions(start.AddSeconds(-1), start.AddMinutes(1)));
        Assert.Equal("Battery", session.Type);
        Assert.Equal(5, session.DurationSeconds);
        Assert.Throws<InvalidOperationException>(fixture.Service.SampleOnce);
    }

    [Fact]
    public async Task Confirmed_suspend_resume_creates_a_confirmed_sleep_gap_session()
    {
        using var fixture = new ServiceFixture();
        fixture.Sensors.GapSessionType = "SleepGap";
        var start = fixture.LocalMorning;
        fixture.Sensors.Enqueue(Battery(start, discharge: 6_000, remaining: 60_000));
        fixture.Sensors.Enqueue(Battery(start.AddSeconds(5), discharge: 6_000, remaining: 59_950));
        fixture.Sensors.Enqueue(Battery(start.AddMinutes(10), discharge: null, remaining: 58_000));
        fixture.Service.SampleOnce();
        fixture.Service.SampleOnce();

        fixture.Service.NotifySuspend();
        fixture.Service.NotifyResume(confirmedByEvent: true);

        // NotifyResume records the resume sample on a background task; wait for both sessions.
        var end = start.AddMinutes(11);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (fixture.Service.GetSessions(start.AddSeconds(-1), end).Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        var sessions = fixture.Service.GetSessions(start.AddSeconds(-1), end);
        var battery = Assert.Single(sessions, s => s.Type == "Battery");
        Assert.Equal(5, battery.DurationSeconds);
        var gap = Assert.Single(sessions, s => s.Type == "SleepGap");
        Assert.Contains("Suspend/resume event confirmed", gap.Note);
        Assert.Equal(595, gap.DurationSeconds);
        Assert.Equal(1.95, gap.EnergyWh, 6);
        Assert.Equal(3, fixture.Service.Database.GetSampleCount());
    }

    [Fact]
    public void Untracked_battery_state_pauses_session_tracking()
    {
        using var fixture = new ServiceFixture();
        var start = fixture.LocalMorning;
        // No battery evidence: the sample is kept but session continuity must end.
        fixture.Sensors.Enqueue(new BatterySnapshot { Timestamp = start });
        fixture.Sensors.Enqueue(Battery(start.AddSeconds(5), discharge: 6_000, remaining: 60_000));
        fixture.Sensors.Enqueue(Battery(start.AddSeconds(10), discharge: 6_000, remaining: 59_950));

        fixture.Service.SampleOnce();
        fixture.Service.SampleOnce();
        fixture.Service.SampleOnce();

        Assert.Equal(3, fixture.Service.Database.GetSampleCount());
        var samples = fixture.Service.GetSamples(start.AddSeconds(-1), start.AddMinutes(1));
        Assert.Equal("Unknown", samples[0].PowerState);
        var session = Assert.Single(fixture.Service.GetSessions(start.AddSeconds(-1), start.AddMinutes(1)));
        Assert.Equal("Battery", session.Type);
        Assert.Equal(start.AddSeconds(5), session.Start);
        Assert.Equal(5, session.DurationSeconds);
    }

    [Fact]
    public void Dispose_is_idempotent_and_safe_after_stop()
    {
        var fixture = new ServiceFixture();
        fixture.Service.SampleOnce();
        fixture.Service.Dispose();
        fixture.Service.Dispose();
    }

    private static BatterySnapshot Battery(DateTimeOffset timestamp, long? discharge, long? remaining) => new()
    {
        Timestamp = timestamp,
        BatteryCount = 1,
        Percent = 80,
        IsAcOnline = false,
        IsDischarging = discharge is not null,
        RemainingCapacityMwh = remaining,
        FullChargeCapacityMwh = 60_000,
        DesignCapacityMwh = 60_000,
        DischargeMw = discharge,
        VoltageMv = 11_400
    };

    private sealed class ServiceFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), $"wattledger-service-{Guid.NewGuid():N}");

        public ServiceFixture()
        {
            Directory.CreateDirectory(_root);
            DatabasePath = Path.Combine(_root, "ledger.sqlite");
            Sensors = new FakeSensorProvider();
            Service = new WattLedgerService(DatabasePath, Sensors);
            LocalMorning = LocalDay.Start(DateOnly.FromDateTime(DateTime.Today)).AddHours(10);
        }

        public string DatabasePath { get; }
        public FakeSensorProvider Sensors { get; }
        public WattLedgerService Service { get; }
        public DateTimeOffset LocalMorning { get; }

        public void Dispose()
        {
            Service.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
    }

    internal sealed class FakeSensorProvider : IPlatformSensorProvider
    {
        private readonly Queue<BatterySnapshot> _battery = new();
        private BatterySnapshot _last = new();

        public string GapSessionType { get; set; } = "ModernStandby";

        public void Enqueue(BatterySnapshot snapshot) => _battery.Enqueue(snapshot);

        public BatterySnapshot ReadBattery()
        {
            _last = _battery.Count > 0 ? _battery.Dequeue() : _last with { Timestamp = DateTimeOffset.Now };
            return _last;
        }

        public IReadOnlyList<BatteryDeviceSnapshot> ReadBatteryDevices() => Array.Empty<BatteryDeviceSnapshot>();
        public RaplSnapshot ReadRapl() => RaplSnapshot.Unavailable;
        public IReadOnlyDictionary<string, double> ReadRawEnergyMilliwatts() => new Dictionary<string, double>();
        public int? ReadBrightness(bool force = false) => null;
        public System.Collections.Generic.IReadOnlyList<WattLedger.Core.Native.EnergyDomainSnapshot> ReadEnergyDomains() => Array.Empty<WattLedger.Core.Native.EnergyDomainSnapshot>();
        public string? ReadForegroundProcess() => null;

        public bool ShouldTrackSessions(BatterySnapshot battery) =>
            PlatformSensorFactory.WindowsShouldTrackSessions(battery);

        public void Dispose() { }
    }
}
