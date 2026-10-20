namespace WattLedger.Core.Models;

public sealed record LedgerStatus
{
    public BatterySnapshot Battery { get; init; } = new();
    public RaplSnapshot Rapl { get; init; } = RaplSnapshot.Unavailable;
    public int? BrightnessPercent { get; init; }
    public string? ForegroundProcess { get; init; }
    public double? RecentAverageWatts { get; init; }
    public double? EstimatedRemainingHours { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}
