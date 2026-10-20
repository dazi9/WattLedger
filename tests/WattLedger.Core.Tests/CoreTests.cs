using WattLedger.Core.Engine;
using WattLedger.Core.Models;

namespace WattLedger.Core.Tests;

public sealed class EnergyIntegratorTests
{
    [Fact]
    public void Integrate_UsesTrapezoid()
    {
        var energy = EnergyIntegrator.IntegrateWh(6, 8, TimeSpan.FromHours(0.5));
        Assert.Equal(3.5, energy, 6);
    }

    [Fact]
    public void Integrate_HandlesSingleSidedSamples()
    {
        Assert.Equal(10, EnergyIntegrator.IntegrateWh(10, null, TimeSpan.FromHours(1)), 6);
        Assert.Equal(10, EnergyIntegrator.IntegrateWh(null, 10, TimeSpan.FromHours(1)), 6);
        Assert.Equal(0, EnergyIntegrator.IntegrateWh(null, null, TimeSpan.FromHours(1)), 6);
    }

    [Fact]
    public void Integrate_CountsChargingAsNegative()
    {
        var energy = EnergyIntegrator.IntegrateWh(-20, -20, TimeSpan.FromHours(1));
        Assert.Equal(-20, energy, 6);
    }

    [Fact]
    public void CapacityDelta_IsPositiveWhenDischarging()
    {
        Assert.Equal(20, EnergyIntegrator.CapacityDeltaWh(60_000, 40_000), 6);
        Assert.Equal(-10, EnergyIntegrator.CapacityDeltaWh(40_000, 50_000), 6);
    }

    [Fact]
    public void EstimateAverageWatts_UsesCapacityDelta()
    {
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
        var samples = new[]
        {
            new SampleRecord { Timestamp = start, RemainingMwh = 60_000 },
            new SampleRecord { Timestamp = start.AddHours(1), RemainingMwh = 54_000 }
        };

        var estimate = EnergyIntegrator.EstimateAverageWatts(samples);
        Assert.NotNull(estimate);
        Assert.Equal(6.0, estimate!.Value, 6);
    }
}

public sealed class SessionTrackerTests
{
    [Fact]
    public void TracksAcBatteryAndStandby()
    {
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
        var tracker = new SessionTracker();

        var ac1 = Sample(start, ac: true, percent: 100, remaining: 60_000, rate: null);
        var r1 = tracker.Add(ac1);
        Assert.NotNull(r1.Current);
        Assert.Equal("AC", r1.Current!.Type);

        var ac2 = Sample(start.AddSeconds(5), ac: true, percent: 100, remaining: 60_000, rate: null);
        tracker.Add(ac2);

        var battery1 = Sample(start.AddSeconds(10), ac: false, percent: 99, remaining: 59_000, rate: 7_200);
        var r3 = tracker.Add(battery1);
        Assert.Contains(r3.Completed, s => s.Type == "AC");
        Assert.Equal("Battery", r3.Current!.Type);

        var battery2 = Sample(start.AddSeconds(20), ac: false, percent: 98, remaining: 58_000, rate: 7_200);
        var r4 = tracker.Add(battery2);
        Assert.Equal("Battery", r4.Current!.Type);
        Assert.True(r4.Current.EnergyWh > 0);

        var afterSleep = Sample(start.AddHours(2), ac: false, percent: 80, remaining: 48_000, rate: 6_000);
        var r5 = tracker.Add(afterSleep);

        var standby = Assert.Single(r5.Completed, s => s.Type == "ModernStandby");
        Assert.Equal(10.0, standby.EnergyWh, 3);
        Assert.Equal(7180L, standby.DurationSeconds);
        Assert.Equal("Battery", r5.Current!.Type);
    }

    [Fact]
    public void Close_ReturnsCurrentSession()
    {
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
        var tracker = new SessionTracker();
        tracker.Add(Sample(start, ac: false, percent: 90, remaining: 54_000, rate: 6_000));
        tracker.Add(Sample(start.AddSeconds(10), ac: false, percent: 90, remaining: 53_950, rate: 6_000));

        var result = tracker.Close();
        var session = Assert.Single(result.Completed);
        Assert.Equal("Battery", session.Type);
        Assert.Equal(10, session.DurationSeconds);
        Assert.True(session.EnergyWh > 0);
    }

    private static SampleRecord Sample(
        DateTimeOffset timestamp,
        bool ac,
        int percent,
        long remaining,
        long? rate)
    {
        return new SampleRecord
        {
            Timestamp = timestamp,
            PowerState = ac ? "AC" : "Battery",
            BatteryPercent = percent,
            RemainingMwh = remaining,
            FullChargeMwh = 60_000,
            IsAcOnline = ac,
            IsCharging = false,
            IsDischarging = !ac,
            DischargeMw = ac ? null : rate,
            ChargeMw = null
        };
    }
}
