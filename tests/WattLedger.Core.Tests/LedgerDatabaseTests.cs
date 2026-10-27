using Microsoft.Data.Sqlite;
using WattLedger.Core.Models;
using WattLedger.Core.Storage;

namespace WattLedger.Core.Tests;

public sealed class LedgerDatabaseTests
{
    [Fact]
    public void Sample_roundtrip_preserves_every_field()
    {
        using var fixture = new DatabaseFixture();
        var timestamp = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
        var sample = new SampleRecord
        {
            Timestamp = timestamp,
            PowerState = "Charging",
            BatteryPercent = 77,
            RemainingMwh = 46_200,
            FullChargeMwh = 60_000,
            DesignMwh = 65_000,
            VoltageMv = 12_345,
            DischargeMw = null,
            ChargeMw = 4_321,
            IsAcOnline = true,
            IsCharging = true,
            IsDischarging = false,
            RaplPackageWatts = 12.5,
            RaplCpuWatts = 7.25,
            RaplGpuWatts = 2.5,
            RaplDramWatts = 1.125,
            BrightnessPercent = 63,
            ForegroundProcess = "code",
            CycleCount = 182,
            DeviceName = "Example battery"
        };

        var id = fixture.Database.InsertSample(sample);
        Assert.True(id > 0);

        var loaded = Assert.Single(fixture.Database.GetSamples(timestamp.AddSeconds(-1), timestamp.AddSeconds(1)));
        Assert.Equal(id, loaded.Id);
        Assert.Equal(timestamp, loaded.Timestamp);
        Assert.Equal("Charging", loaded.PowerState);
        Assert.Equal(77, loaded.BatteryPercent);
        Assert.Equal(46_200, loaded.RemainingMwh);
        Assert.Equal(60_000, loaded.FullChargeMwh);
        Assert.Equal(65_000, loaded.DesignMwh);
        Assert.Equal(12_345, loaded.VoltageMv);
        Assert.Null(loaded.DischargeMw);
        Assert.Equal(4_321, loaded.ChargeMw);
        Assert.True(loaded.IsAcOnline);
        Assert.True(loaded.IsCharging);
        Assert.False(loaded.IsDischarging);
        Assert.Equal(12.5, loaded.RaplPackageWatts!.Value, 9);
        Assert.Equal(7.25, loaded.RaplCpuWatts!.Value, 9);
        Assert.Equal(2.5, loaded.RaplGpuWatts!.Value, 9);
        Assert.Equal(1.125, loaded.RaplDramWatts!.Value, 9);
        Assert.Equal(63, loaded.BrightnessPercent);
        Assert.Equal("code", loaded.ForegroundProcess);
        Assert.Equal(182, loaded.CycleCount);
        Assert.Equal("Example battery", loaded.DeviceName);
        Assert.Equal(-4.321, loaded.NetDischargeWatts!.Value, 9);
    }

    [Fact]
    public void Sample_roundtrip_preserves_null_fields()
    {
        using var fixture = new DatabaseFixture();
        var timestamp = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
        fixture.Database.InsertSample(new SampleRecord { Timestamp = timestamp, PowerState = "Unknown" });

        var loaded = Assert.Single(fixture.Database.GetSamples(timestamp.AddMinutes(-1), timestamp.AddMinutes(1)));
        Assert.Equal("Unknown", loaded.PowerState);
        Assert.Null(loaded.BatteryPercent);
        Assert.Null(loaded.RemainingMwh);
        Assert.Null(loaded.VoltageMv);
        Assert.False(loaded.IsAcOnline);
        Assert.False(loaded.IsCharging);
        Assert.False(loaded.IsDischarging);
        Assert.Null(loaded.NetDischargeWatts);
        Assert.Null(loaded.ForegroundProcess);
    }

    [Fact]
    public void GetLatestSample_prefers_the_newest_row_and_respects_the_range()
    {
        using var fixture = new DatabaseFixture();
        var start = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(8));
        Insert(fixture, start, "Battery");
        Insert(fixture, start.AddMinutes(5), "AC");
        Insert(fixture, start.AddMinutes(10), "Battery");

        Assert.Equal("Battery", fixture.Database.GetLatestSample()!.PowerState);
        Assert.Equal("AC", fixture.Database.GetLatestSample(start.AddMinutes(4), start.AddMinutes(6))!.PowerState);
        Assert.Null(fixture.Database.GetLatestSample(start.AddHours(1), start.AddHours(2)));
    }

    [Fact]
    public void PruneSamples_removes_only_older_rows()
    {
        using var fixture = new DatabaseFixture();
        var start = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(8));
        Insert(fixture, start, "Battery");
        Insert(fixture, start.AddDays(1), "Battery");
        Insert(fixture, start.AddDays(2), "Battery");

        fixture.Database.PruneSamples(start.AddDays(1));

        Assert.Equal(2, fixture.Database.GetSampleCount());
        Assert.All(fixture.Database.GetSamples(DateTimeOffset.MinValue, DateTimeOffset.MaxValue),
            sample => Assert.True(sample.Timestamp >= start.AddDays(1)));
    }

    [Fact]
    public void Health_roundtrip_and_history()
    {
        using var fixture = new DatabaseFixture();
        var start = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(8));
        fixture.Database.InsertHealth(new HealthRecord
        {
            Timestamp = start,
            DesignMwh = 65_000,
            FullChargeMwh = 58_500,
            CycleCount = 180,
            HealthPercent = 90.0
        });
        fixture.Database.InsertHealth(new HealthRecord
        {
            Timestamp = start.AddDays(1),
            DesignMwh = 65_000,
            FullChargeMwh = 58_175,
            CycleCount = 181,
            HealthPercent = 89.5
        });

        var latest = fixture.Database.GetLatestHealth();
        Assert.Equal(89.5, latest!.HealthPercent!.Value, 6);
        Assert.Equal(181, latest.CycleCount);

        var history = fixture.Database.GetHealthHistory(start, start.AddDays(2));
        Assert.Equal(2, history.Count);
        Assert.Equal(90.0, history[0].HealthPercent!.Value, 6);
        Assert.Equal(65_000, history[0].DesignMwh);
    }

    [Fact]
    public void UpsertSession_updates_the_row_with_the_same_start_instead_of_duplicating()
    {
        using var fixture = new DatabaseFixture();
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
        var session = new SessionRecord
        {
            Start = start,
            End = start.AddMinutes(5),
            Type = "Battery",
            EnergyWh = 1,
            AverageWatts = 12,
            PeakWatts = 15
        };
        fixture.Database.UpsertSession(session);
        fixture.Database.UpsertSession(session with { End = start.AddMinutes(6), EnergyWh = 1.2 });

        var sessions = fixture.Database.GetSessions(start.AddMinutes(-1), start.AddMinutes(10));
        var updated = Assert.Single(sessions);
        Assert.Equal(start.AddMinutes(6), updated.End);
        Assert.Equal(1.2, updated.EnergyWh, 6);
        Assert.Equal(1, fixture.Database.GetSessionCount());
    }

    [Fact]
    public void GetSessions_matches_sessions_overlapping_the_window_edges()
    {
        using var fixture = new DatabaseFixture();
        var start = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(8));
        Upsert(fixture, start.AddMinutes(10), start.AddMinutes(20), "Battery");
        Upsert(fixture, start.AddMinutes(25), start.AddMinutes(35), "AC");
        Upsert(fixture, start.AddMinutes(40), start.AddMinutes(50), "Battery");

        Assert.Equal(3, fixture.Database.GetSessions(start.AddMinutes(15), start.AddMinutes(45)).Count);
        Assert.Empty(fixture.Database.GetSessions(start.AddMinutes(21), start.AddMinutes(24)));
        Assert.Equal(2, fixture.Database.GetSessions(start.AddMinutes(20), start.AddMinutes(25)).Count);
        Assert.Equal(2, fixture.Database.GetSessions(start.AddMinutes(5), start.AddMinutes(26)).Count);
    }

    [Fact]
    public void GetDailySummaries_returns_empty_for_an_empty_or_inverted_range()
    {
        using var fixture = new DatabaseFixture();
        var start = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(8));
        Assert.Empty(fixture.Database.GetDailySummaries(start, start));
        Assert.Empty(fixture.Database.GetDailySummaries(start.AddHours(1), start));
    }

    [Fact]
    public void Decimation_can_return_a_single_final_sample()
    {
        using var fixture = new DatabaseFixture();
        var start = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(8));
        for (var i = 0; i < 5; i++)
        {
            Insert(fixture, start.AddMinutes(i), "Battery");
        }

        var samples = fixture.Database.GetSamples(start, start.AddMinutes(10), maxPoints: 1);
        var single = Assert.Single(samples);
        Assert.Equal(start.AddMinutes(4), single.Timestamp);
    }

    private static void Insert(DatabaseFixture fixture, DateTimeOffset timestamp, string powerState) =>
        fixture.Database.InsertSample(new SampleRecord { Timestamp = timestamp, PowerState = powerState });

    private static void Upsert(DatabaseFixture fixture, DateTimeOffset start, DateTimeOffset end, string type) =>
        fixture.Database.UpsertSession(new SessionRecord
        {
            Start = start,
            End = end,
            Type = type,
            EnergyWh = 1,
            AverageWatts = 1,
            PeakWatts = 1
        });

    internal sealed class DatabaseFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), $"wattledger-db-{Guid.NewGuid():N}");

        public DatabaseFixture()
        {
            Directory.CreateDirectory(_root);
            Database = new LedgerDatabase(Path.Combine(_root, "ledger.sqlite"));
            Database.Initialize();
        }

        public LedgerDatabase Database { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
    }
}
