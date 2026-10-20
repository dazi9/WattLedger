namespace WattLedger.Core.Models;

public sealed record SessionRecord
{
    public long Id { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public string Type { get; init; } = "Unknown";
    public string? Note { get; init; }
    public int? StartPercent { get; init; }
    public int? EndPercent { get; init; }
    public long? StartRemainingMwh { get; init; }
    public long? EndRemainingMwh { get; init; }
    public double EnergyWh { get; init; }
    public double AverageWatts { get; init; }
    public double PeakWatts { get; init; }

    public long DurationSeconds => Math.Max(0, (long)(End - Start).TotalSeconds);
}
