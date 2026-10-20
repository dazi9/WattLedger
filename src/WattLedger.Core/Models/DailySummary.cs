namespace WattLedger.Core.Models;

public sealed record DailySummary
{
    public DateOnly Date { get; init; }
    public double DischargeWh { get; init; }
    public double ChargeWh { get; init; }
    public double ActiveSeconds { get; init; }
    public double StandbySeconds { get; init; }

    public double ActiveHours => ActiveSeconds / 3600.0;
    public double StandbyHours => StandbySeconds / 3600.0;
    public double AverageActiveWatts => ActiveHours > 0 ? DischargeWh / ActiveHours : 0;
    public double AverageStandbyWatts => StandbyHours > 0 ? DischargeWh / StandbyHours : 0;
}
