using System.Globalization;
using WattLedger.Core.Models;

namespace WattLedger.Core.Native;

/// <summary>Reads Linux power_supply battery data without requiring elevated permissions.</summary>
public sealed class LinuxBatteryReader
{
    private readonly string _powerSupplyPath;

    public LinuxBatteryReader(string? powerSupplyPath = null)
    {
        _powerSupplyPath = powerSupplyPath ?? "/sys/class/power_supply";
    }

    public IReadOnlyList<BatteryDeviceSnapshot> ReadDevices()
    {
        return ReadDeviceData().Select(d => d.Snapshot).ToArray();
    }

    private IReadOnlyList<DeviceData> ReadDeviceData()
    {
        var online = ReadOnline(_powerSupplyPath);
        return ReadDeviceData(online);
    }

    private IReadOnlyList<DeviceData> ReadDeviceData(bool? online)
    {
        var devices = new List<DeviceData>();
        try
        {
            foreach (var path in Directory.EnumerateDirectories(_powerSupplyPath))
            {
                if (!string.Equals(ReadText(path, "type"), "Battery", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(ReadText(path, "scope"), "Device", StringComparison.OrdinalIgnoreCase) ||
                    ReadLong(path, "present") == 0)
                {
                    continue;
                }

                var voltageUv = ReadLong(path, "voltage_now");
                var voltageMv = ToInt(Scale(voltageUv, 1_000));
                var remainingMwh = ReadEnergyMwh(path, "energy_now");
                var fullMwh = ReadEnergyMwh(path, "energy_full");
                var designMwh = ReadEnergyMwh(path, "energy_full_design");
                var currentUa = ReadSignedLong(path, "current_now");
                var rateUw = Magnitude(ReadSignedLong(path, "power_now"));
                var rateMw = Scale(rateUw, 1_000);
                if (rateUw is not > 0 && voltageUv is > 0)
                {
                    var magnitudeUa = Magnitude(currentUa);
                    if (magnitudeUa is not null)
                    {
                        rateMw = ScaleProduct(magnitudeUa.Value, voltageUv.Value, 1_000_000_000);
                    }
                }

                var state = ReadText(path, "status");
                var charging = string.Equals(state, "Charging", StringComparison.OrdinalIgnoreCase);
                var discharging = string.Equals(state, "Discharging", StringComparison.OrdinalIgnoreCase);
                var percent = ToInt(ReadLong(path, "capacity"));
                if (percent is null && remainingMwh is >= 0 && fullMwh is > 0)
                {
                    percent = (int)Math.Clamp(Math.Round(remainingMwh.Value * 100d / fullMwh.Value), 0, 100);
                }

                var name = ReadText(path, "model_name") ?? Path.GetFileName(path);
                var snapshot = new BatteryDeviceSnapshot
                {
                    DevicePath = path,
                    DesignedCapacityMwh = designMwh,
                    FullChargeCapacityMwh = fullMwh,
                    RemainingCapacityMwh = remainingMwh,
                    VoltageMv = voltageMv,
                    RateMw = ToInt(rateMw),
                    PowerState = (charging ? NativeMethods.BatteryCharging : 0u) |
                                 (discharging ? NativeMethods.BatteryDischarging : 0u) |
                                 (online == true || charging ? NativeMethods.BatteryPowerOnLine : 0u),
                    CycleCount = ToInt(ReadLong(path, "cycle_count")),
                    DeviceName = name,
                    Manufacturer = ReadText(path, "manufacturer"),
                    Chemistry = ReadText(path, "technology")
                };
                var chargeNow = ReadLong(path, "charge_now");
                var chargeFull = ReadLong(path, "charge_full");
                devices.Add(new DeviceData(snapshot, percent, chargeNow, chargeFull, charging, discharging));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return devices;
    }

    public BatterySnapshot Read()
    {
        var online = ReadOnline(_powerSupplyPath);
        var data = ReadDeviceData(online);
        var devices = data.Select(d => d.Snapshot).ToArray();
        var remaining = SumKnown(devices.Select(d => d.RemainingCapacityMwh));
        var full = SumKnown(devices.Select(d => d.FullChargeCapacityMwh));
        var design = SumKnown(devices.Select(d => d.DesignedCapacityMwh));
        var chargeNow = SumKnown(data.Select(d => d.ChargeNow));
        var chargeFull = SumKnown(data.Select(d => d.ChargeFull));
        var energyPercent = remaining is not null && full is > 0
            ? (int)Math.Clamp(Math.Round(remaining.Value * 100d / full.Value), 0, 100)
            : (int?)null;
        var chargePercent = chargeNow is not null && chargeFull is > 0
            ? (int)Math.Clamp(Math.Round(chargeNow.Value * 100d / chargeFull.Value), 0, 100)
            : (int?)null;
        var percent = energyPercent ?? chargePercent ?? WeightedPercent(data);

        long? discharge = null;
        long? charge = null;
        foreach (var device in devices)
        {
            if (device.RateMw is not > 0)
            {
                continue;
            }

            if ((device.PowerState & NativeMethods.BatteryDischarging) != 0)
            {
                discharge = (discharge ?? 0) + device.RateMw;
            }
            else if ((device.PowerState & NativeMethods.BatteryCharging) != 0)
            {
                charge = (charge ?? 0) + device.RateMw;
            }
        }

        var voltages = devices.Where(d => d.VoltageMv is > 0).Select(d => d.VoltageMv!.Value).ToArray();
        return new BatterySnapshot
        {
            Timestamp = DateTimeOffset.Now,
            // Charging is positive evidence of external power even when another
            // supply (for example an unused AC/USB input) reports online=0.
            IsAcOnline = online == true || data.Any(d => d.Charging),
            IsCharging = data.Any(d => d.Charging),
            IsDischarging = data.Any(d => d.Discharging),
            Percent = percent,
            PercentIsEstimated = energyPercent is null && chargePercent is not null,
            RemainingCapacityMwh = remaining,
            FullChargeCapacityMwh = full,
            DesignCapacityMwh = design,
            VoltageMv = voltages.Length == 0 ? null : (int)Math.Round(voltages.Average()),
            DischargeMw = discharge,
            ChargeMw = charge,
            CycleCount = devices.Where(d => d.CycleCount is not null).Select(d => d.CycleCount).Max(),
            BatteryCount = devices.Length,
            DeviceName = Join(devices.Select(d => d.DeviceName)),
            Manufacturer = Join(devices.Select(d => d.Manufacturer)),
            Chemistry = Join(devices.Select(d => d.Chemistry))
        };
    }

    private static bool? ReadOnline(string root)
    {
        bool? found = null;
        try
        {
            foreach (var path in Directory.EnumerateDirectories(root))
            {
                var type = ReadText(path, "type");
                if (!string.Equals(type, "Mains", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(type, "USB", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(type, "USB_C", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var online = ReadLong(path, "online");
                if (online is not null)
                {
                    found = found == true || online == 1;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return found;
    }

    private static long? ReadEnergyMwh(string path, string property) => Scale(ReadLong(path, property), 1_000);

    private static long? ReadLong(string path, string property)
    {
        var text = ReadText(path, property);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : null;
    }

    private static long? ReadSignedLong(string path, string property)
    {
        var text = ReadText(path, property);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static long? Magnitude(long? value)
    {
        if (value is null) return null;
        return value.Value == long.MinValue ? null : Math.Abs(value.Value);
    }

    private static string? ReadText(string path, string property)
    {
        try
        {
            var value = File.ReadAllText(Path.Combine(path, property)).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static long? Scale(long? value, long divisor) => value is null ? null : value.Value / divisor;
    private static int? ToInt(long? value) => value is null || value > int.MaxValue ? null : (int)value.Value;
    private static long? ScaleProduct(long left, long right, long divisor)
    {
        try { return checked(left * right) / divisor; }
        catch (OverflowException) { return null; }
    }

    private static long? SumKnown(IEnumerable<long?> values)
    {
        var present = values.ToArray();
        return present.Length == 0 || present.Any(v => v is null) ? null : present.Sum(v => v!.Value);
    }

    private static int? WeightedPercent(IReadOnlyList<DeviceData> devices)
    {
        var eligible = devices.Where(d => d.Percent is not null).ToArray();
        if (eligible.Length == 0) return null;
        var weighted = eligible.Where(d => d.ChargeFull is > 0).ToArray();
        if (weighted.Length == eligible.Length && eligible.Length == devices.Count)
        {
            var total = weighted.Sum(d => (double)d.ChargeFull!.Value);
            var weightedPercent = weighted.Sum(d => (double)d.Percent!.Value * d.ChargeFull!.Value) / total;
            return (int)Math.Clamp(Math.Round(weightedPercent), 0, 100);
        }

        return devices.Count == 1 && eligible.Length == 1
            ? Math.Clamp(eligible[0].Percent!.Value, 0, 100)
            : null;
    }

    private static string? Join(IEnumerable<string?> values)
    {
        var present = values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return present.Length == 0 ? null : string.Join(" + ", present);
    }

    private sealed record DeviceData(
        BatteryDeviceSnapshot Snapshot,
        int? Percent,
        long? ChargeNow,
        long? ChargeFull,
        bool Charging,
        bool Discharging);
}
