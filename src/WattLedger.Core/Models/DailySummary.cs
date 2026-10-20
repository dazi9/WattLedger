namespace WattLedger.Core.Models;

public sealed record DailySummary
{
    public DateOnly Date { get; init; }
    public double ActiveWh { get; init; }
    public double StandbyWh { get; init; }
    public double ChargeWh { get; init; }
    public double ActiveSeconds { get; init; }
    public double StandbySeconds { get; init; }

    public double DischargeWh => ActiveWh + StandbyWh;
    public double ActiveHours => ActiveSeconds / 3600.0;
    public double StandbyHours => StandbySeconds / 3600.0;
    public double AverageActiveWatts => ActiveHours > 0 ? ActiveWh / ActiveHours : 0;
    public double AverageStandbyWatts => StandbyHours > 0 ? StandbyWh / StandbyHours : 0;
}
