using WattLedger.Core.Models;

namespace WattLedger.Core.Engine;

public static class EnergyIntegrator
{
    /// <summary>
    /// Trapezoidal integration of net battery power over a sample interval.
    /// Positive energy means energy taken out of the battery; negative means
    /// energy put into the battery while charging.
    /// </summary>
    public static double IntegrateWh(double? previousWatts, double? currentWatts, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return 0;
        }

        if (previousWatts is null && currentWatts is null)
        {
            return 0;
        }

        var p1 = previousWatts ?? currentWatts!.Value;
        var p2 = currentWatts ?? previousWatts!.Value;
        var averageWatts = (p1 + p2) / 2.0;
        return averageWatts * duration.TotalHours;
    }

    /// <summary>
    /// Coulomb-counter fallback/calibration: the change in remaining capacity.
    /// Positive means discharged.
    /// </summary>
    public static double CapacityDeltaWh(long? startMwh, long? endMwh)
    {
        if (startMwh is null || endMwh is null)
        {
            return 0;
        }

        return (startMwh.Value - endMwh.Value) / 1000.0;
    }

    public static double AverageWatts(double energyWh, TimeSpan duration)
    {
        return duration.TotalHours > 0 ? energyWh / duration.TotalHours : 0;
    }

    /// <summary>
    /// Estimates average net power from the battery's remaining-capacity counter.
    /// Used when the firmware does not report a rate; positive means discharge.
    /// </summary>
    public static double? EstimateAverageWatts(IReadOnlyList<SampleRecord> samples)
    {
        var withCapacity = samples
            .Where(s => s.RemainingMwh is > 0)
            .ToArray();

        if (withCapacity.Length < 2)
        {
            return null;
        }

        var first = withCapacity[0];
        var last = withCapacity[^1];
        var hours = (last.Timestamp - first.Timestamp).TotalHours;
        if (hours <= 0)
        {
            return null;
        }

        var energyWh = (first.RemainingMwh!.Value - last.RemainingMwh!.Value) / 1000.0;
        if (Math.Abs(energyWh) < 0.001)
        {
            return null;
        }

        return energyWh / hours;
    }
}
