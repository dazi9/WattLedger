namespace WattLedger.Core.Models;

public sealed record SampleRecord
{
    public long Id { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string PowerState { get; init; } = "Unknown";
    public int? BatteryPercent { get; init; }
    public long? RemainingMwh { get; init; }
    public long? FullChargeMwh { get; init; }
    public long? DesignMwh { get; init; }
    public int? VoltageMv { get; init; }
    public long? DischargeMw { get; init; }
    public long? ChargeMw { get; init; }
    public bool IsAcOnline { get; init; }
    public bool IsCharging { get; init; }
    public bool IsDischarging { get; init; }
    public double? RaplPackageWatts { get; init; }
    public double? RaplCpuWatts { get; init; }
    public double? RaplGpuWatts { get; init; }
    public double? RaplDramWatts { get; init; }
    public int? BrightnessPercent { get; init; }
    public string? ForegroundProcess { get; init; }
    public int? CycleCount { get; init; }
    public string? DeviceName { get; init; }

    public double? NetDischargeWatts =>
        DischargeMw is > 0 ? DischargeMw / 1000.0 :
        ChargeMw is > 0 ? -ChargeMw / 1000.0 :
        null;
}
