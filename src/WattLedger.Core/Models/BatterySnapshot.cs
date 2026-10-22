namespace WattLedger.Core.Models;

public sealed record BatterySnapshot
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public bool IsAcOnline { get; init; }
    public bool IsCharging { get; init; }
    public bool IsDischarging { get; init; }
    public int? Percent { get; init; }
    public bool PercentIsEstimated { get; init; }
    public long? RemainingCapacityMwh { get; init; }
    public long? FullChargeCapacityMwh { get; init; }
    public long? DesignCapacityMwh { get; init; }
    public int? VoltageMv { get; init; }
    public long? DischargeMw { get; init; }
    public long? ChargeMw { get; init; }
    public int? CycleCount { get; init; }
    public int BatteryCount { get; init; }
    public string? DeviceName { get; init; }
    public string? Manufacturer { get; init; }
    public string? SerialNumber { get; init; }
    public string? Chemistry { get; init; }

    public double? HealthPercent =>
        DesignCapacityMwh is > 0 && FullChargeCapacityMwh is > 0
            ? Math.Clamp(FullChargeCapacityMwh.Value * 100.0 / DesignCapacityMwh.Value, 0, 100)
            : null;

    public double? DischargeWatts => DischargeMw is > 0 ? DischargeMw / 1000.0 : null;
    public double? ChargeWatts => ChargeMw is > 0 ? ChargeMw / 1000.0 : null;
}
