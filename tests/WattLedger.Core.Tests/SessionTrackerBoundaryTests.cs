using WattLedger.Core.Engine;
using WattLedger.Core.Models;

namespace WattLedger.Core.Tests;

public sealed class SessionTrackerBoundaryTests
{
    [Fact]
    public void Charging_session_accumulates_negative_energy()
    {
        var tracker = new SessionTracker();
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
        tracker.Add(Sample(start, ac: true, charging: true, chargeRate: 36_000));
        tracker.Add(Sample(start.AddSeconds(30), ac: true, charging: true, chargeRate: 36_000));

        var current = tracker.Current!;
        Assert.Equal("Charging", current.Type);
        Assert.Equal(-0.3, current.EnergyWh, 6);
        Assert.Equal(36, current.PeakWatts, 6);
    }

    [Fact]
    public void Ac_to_charging_transition_splits_into_two_sessions()
    {
        var tracker = new SessionTracker();
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
        tracker.Add(Sample(start, ac: true, charging: false, chargeRate: null));
        var result = tracker.Add(Sample(start.AddSeconds(30), ac: true, charging: true, chargeRate: 30_000));

        var ac = Assert.Single(result.Completed, s => s.Type == "AC");
        Assert.Equal(30, ac.DurationSeconds);
        Assert.Equal("Charging", result.Current!.Type);
        // The transition interval is attributed to the closed session, so the charge shows up there.
        Assert.True(ac.EnergyWh < 0);
        Assert.Equal(0, result.Current.EnergyWh, 9);
    }

    [Fact]
    public void Peak_watts_tracks_the_maximum_observed_rate()
    {
        var tracker = new SessionTracker();
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
        tracker.Add(Sample(start, ac: false, charging: false, chargeRate: null, dischargeRate: 3_000));
        tracker.Add(Sample(start.AddSeconds(10), ac: false, charging: false, chargeRate: null, dischargeRate: 9_000));
        tracker.Add(Sample(start.AddSeconds(20), ac: false, charging: false, chargeRate: null, dischargeRate: 6_000));

        var current = tracker.Current!;
        Assert.Equal(9, current.PeakWatts, 6);
        Assert.Equal(90, current.StartPercent);
        Assert.Equal(90, current.EndPercent);
    }

    [Fact]
    public void Close_twice_returns_the_session_once_and_resets_the_tracker()
    {
        var tracker = new SessionTracker();
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
        tracker.Add(Sample(start, ac: false, charging: false, chargeRate: null, dischargeRate: 6_000));
        tracker.Add(Sample(start.AddSeconds(10), ac: false, charging: false, chargeRate: null, dischargeRate: 6_000));

        var first = tracker.Close();
        Assert.Single(first.Completed);
        Assert.Null(tracker.Current);

        var second = tracker.Close();
        Assert.Empty(second.Completed);
    }

    [Fact]
    public void Samples_added_after_close_start_a_fresh_session()
    {
        var tracker = new SessionTracker();
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
        tracker.Add(Sample(start, ac: false, charging: false, chargeRate: null, dischargeRate: 6_000));
        tracker.Close();

        var result = tracker.Add(Sample(start.AddSeconds(30), ac: true, charging: false, chargeRate: null));

        Assert.Empty(result.Completed);
        Assert.Equal("AC", result.Current!.Type);
        Assert.Equal(0, result.Current.DurationSeconds);
    }

    [Fact]
    public void Gap_session_on_ac_records_the_ac_note()
    {
        var tracker = new SessionTracker();
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
        tracker.Add(Sample(start, ac: true, charging: false, chargeRate: null, remaining: 55_000));

        var result = tracker.Add(
            Sample(start.AddHours(2), ac: true, charging: false, chargeRate: null, remaining: 55_000),
            "ModernStandby");

        var standby = Assert.Single(result.Completed, s => s.Type == "ModernStandby");
        Assert.Equal("AC", standby.Note);
        Assert.Equal(0, standby.EnergyWh, 9);
    }

    [Fact]
    public void IntegrateWh_ignores_non_positive_durations()
    {
        Assert.Equal(0, EnergyIntegrator.IntegrateWh(10, 10, TimeSpan.Zero), 9);
        Assert.Equal(0, EnergyIntegrator.IntegrateWh(10, 10, TimeSpan.FromSeconds(-5)), 9);
    }

    [Fact]
    public void EstimateAverageWatts_needs_two_spaced_capacity_samples()
    {
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
        Assert.Null(EnergyIntegrator.EstimateAverageWatts([Sample(start, ac: false, charging: false, chargeRate: null)]));
        Assert.Null(EnergyIntegrator.EstimateAverageWatts(
        [
            Sample(start, ac: false, charging: false, chargeRate: null),
            Sample(start, ac: false, charging: false, chargeRate: null)
        ]));
        Assert.Null(EnergyIntegrator.EstimateAverageWatts(
        [
            Sample(start, ac: false, charging: false, chargeRate: null),
            Sample(start.AddHours(1), ac: false, charging: false, chargeRate: null) with { RemainingMwh = null }
        ]));

        // Charging raises capacity, so the estimate is a negative discharge rate.
        var estimate = EnergyIntegrator.EstimateAverageWatts(
        [
            Sample(start, ac: false, charging: false, chargeRate: null, remaining: 50_000),
            Sample(start.AddHours(1), ac: false, charging: false, chargeRate: null, remaining: 56_000)
        ]);
        Assert.Equal(-6, estimate!.Value, 6);
    }

    private static SampleRecord Sample(
        DateTimeOffset timestamp,
        bool ac,
        bool charging,
        long? chargeRate,
        long? dischargeRate = null,
        long remaining = 54_000)
    {
        return new SampleRecord
        {
            Timestamp = timestamp,
            PowerState = ac ? (charging ? "Charging" : "AC") : "Battery",
            BatteryPercent = 90,
            RemainingMwh = remaining,
            FullChargeMwh = 60_000,
            IsAcOnline = ac,
            IsCharging = charging,
            IsDischarging = !ac && dischargeRate is not null,
            DischargeMw = dischargeRate,
            ChargeMw = chargeRate
        };
    }
}
