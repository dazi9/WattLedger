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
}
