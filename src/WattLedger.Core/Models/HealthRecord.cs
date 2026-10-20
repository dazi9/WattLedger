namespace WattLedger.Core.Models;

public sealed record HealthRecord
{
    public DateTimeOffset Timestamp { get; init; }
    public long? DesignMwh { get; init; }
    public long? FullChargeMwh { get; init; }
    public int? CycleCount { get; init; }
    public double? HealthPercent { get; init; }
}
