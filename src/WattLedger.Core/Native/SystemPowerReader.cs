using WattLedger.Core.Native;

namespace WattLedger.Core.Native;

public sealed record SystemPowerSnapshot(
    bool? AcOnline,
    int? Percent,
    bool Charging,
    bool Discharging,
    bool NoBattery);

public static class SystemPowerReader
{
    public static SystemPowerSnapshot Read()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status))
        {
            return new SystemPowerSnapshot(null, null, false, false, false);
        }

        bool? acOnline = status.ACLineStatus switch
        {
            0 => false,
            1 => true,
            _ => null
        };

        var noBattery = (status.BatteryFlag & 128) != 0;
        int? percent = status.BatteryLifePercent == 255 || noBattery
            ? null
            : status.BatteryLifePercent;

        var charging = (status.BatteryFlag & 8) != 0;
        var discharging = acOnline == false && !charging && !noBattery;

        return new SystemPowerSnapshot(acOnline, percent, charging, discharging, noBattery);
    }
}
