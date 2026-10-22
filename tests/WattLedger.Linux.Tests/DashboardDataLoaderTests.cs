using WattLedger.Core.Engine;
using WattLedger.Core.Models;
using WattLedger.Linux;

namespace WattLedger.Linux.Tests;

public sealed class DashboardDataLoaderTests
{
    [Fact]
    public void Incremental_reads_keep_equal_timestamp_rows_and_mark_confirmed_short_sleep_gaps()
    {
        using var fixture = new Fixture();
        var start = new DateTimeOffset(DateTime.Today).AddHours(1);
        var loader = new DashboardDataLoader(fixture.Service);
        fixture.Add(start, 5000);
        var initial = loader.Load(start);
        Assert.Single(initial.Samples);
        fixture.Add(start, 7000);
        fixture.Add(start.AddSeconds(10), 9000);
        fixture.Service.Database.UpsertSession(new SessionRecord
        {
            Start = start, End = start.AddSeconds(10), Type = "SleepGap",
            Note = "Suspend/resume event confirmed"
        });

        var snapshot = loader.Load(start.AddSeconds(15));

        Assert.Equal(3, snapshot.Samples.Count);
        Assert.False(snapshot.Samples[1].BreakBefore);
        Assert.True(snapshot.Samples[2].BreakBefore);
        Assert.Equal(3, loader.Load(start.AddSeconds(20)).Samples.Count);
        Assert.Single(initial.Samples); // Background cache updates do not mutate published data.
    }

    [Fact]
    public void Midnight_and_backward_clock_changes_reload_the_correct_day()
    {
        using var fixture = new Fixture();
        var today = new DateTimeOffset(DateTime.Today);
        var yesterday = today.AddDays(-1).AddHours(23);
        var loader = new DashboardDataLoader(fixture.Service);
        fixture.Add(yesterday, 5000);
        fixture.Add(today.AddSeconds(5), 7000);
        fixture.Add(today.AddSeconds(15), 9000);

        Assert.Equal(5, Assert.Single(loader.Load(yesterday.AddSeconds(5)).Samples).Watts);
        Assert.Equal(2, loader.Load(today.AddSeconds(20)).Samples.Count);
        Assert.Equal(7, Assert.Single(loader.Load(today.AddSeconds(10)).Samples).Watts);
    }

    [Fact]
    public void Invalidation_includes_out_of_order_samples_after_clock_recovers()
    {
        using var fixture = new Fixture();
        var start = new DateTimeOffset(DateTime.Today).AddHours(1);
        var loader = new DashboardDataLoader(fixture.Service);
        fixture.Add(start, 5000);
        Assert.Single(loader.Load(start.AddSeconds(1)).Samples);
        fixture.Add(start.AddSeconds(-5), 7000);
        loader.Invalidate();

        var snapshot = loader.Load(start.AddSeconds(20));

        Assert.Equal(2, snapshot.Samples.Count);
        Assert.Equal(7, snapshot.Samples[0].Watts);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"wattledger-dashboard-{Guid.NewGuid():N}");
        public WattLedgerService Service { get; }

        public Fixture()
        {
            Service = new WattLedgerService(Path.Combine(_root, "ledger.sqlite"));
            Service.Database.Initialize();
        }

        public void Add(DateTimeOffset timestamp, long milliwatts) => Service.Database.InsertSample(new SampleRecord
        {
            Timestamp = timestamp, PowerState = "Battery", IsDischarging = true,
            DischargeMw = milliwatts, BatteryPercent = 80
        });

        public void Dispose()
        {
            Service.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
