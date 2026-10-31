using WattLedger.Core.Models;

namespace WattLedger.Core.Native;

internal interface IPlatformSensorProvider : IDisposable
{
    BatterySnapshot ReadBattery();
    IReadOnlyList<BatteryDeviceSnapshot> ReadBatteryDevices();
    RaplSnapshot ReadRapl();
    IReadOnlyDictionary<string, double> ReadRawEnergyMilliwatts();
    IReadOnlyList<EnergyDomainSnapshot> ReadEnergyDomains();
    int? ReadBrightness(bool force = false);
    string? ReadForegroundProcess();
    string GapSessionType { get; }
    bool ShouldTrackSessions(BatterySnapshot battery);
}

internal static class PlatformSensorFactory
{
    internal static bool WindowsShouldTrackSessions(BatterySnapshot battery) =>
        battery.BatteryCount > 0 || battery.Percent is not null || battery.IsAcOnline;

    // Full/Unknown without evidence of external power cannot identify AC versus
    // battery use. Preserve the sample but end session continuity (see METHODOLOGY).
    internal static bool LinuxShouldTrackSessions(BatterySnapshot battery) =>
        battery.BatteryCount > 0 && (battery.IsCharging || battery.IsDischarging || battery.IsAcOnline);

    public static IPlatformSensorProvider Create() => OperatingSystem.IsWindows()
        ? new WindowsSensorProvider()
        : OperatingSystem.IsLinux()
            ? new LinuxSensorProvider()
            : new UnavailableSensorProvider();

    private sealed class WindowsSensorProvider : IPlatformSensorProvider
    {
        private readonly Lazy<BatteryReader> _battery = new(() => new BatteryReader());
        private readonly Lazy<EnergyMeterReader> _energy = new(() => new EnergyMeterReader());
        private readonly Lazy<BrightnessReader> _brightness = new(() => new BrightnessReader());
        public string GapSessionType => "ModernStandby";
        public bool ShouldTrackSessions(BatterySnapshot battery) => WindowsShouldTrackSessions(battery);
        public BatterySnapshot ReadBattery() => _battery.Value.Read();
        public IReadOnlyList<BatteryDeviceSnapshot> ReadBatteryDevices() => _battery.Value.ReadDevices();
        public RaplSnapshot ReadRapl() => _energy.Value.Read();
        public IReadOnlyDictionary<string, double> ReadRawEnergyMilliwatts() => _energy.Value.ReadRawMilliwatts();
        public IReadOnlyList<EnergyDomainSnapshot> ReadEnergyDomains() => Array.Empty<EnergyDomainSnapshot>();
        public int? ReadBrightness(bool force = false) => _brightness.Value.Read(force);
        public string? ReadForegroundProcess() => ForegroundProcessReader.GetName();
        public void Dispose()
        {
            if (_energy.IsValueCreated) _energy.Value.Dispose();
        }
    }

    private sealed class LinuxSensorProvider : IPlatformSensorProvider
    {
        private readonly LinuxBatteryReader _battery = new();
        private readonly Lazy<LinuxPowercapReader> _powercap = new(() => new LinuxPowercapReader());
        public string GapSessionType => "SleepGap";
        public bool ShouldTrackSessions(BatterySnapshot battery) => LinuxShouldTrackSessions(battery);
        public BatterySnapshot ReadBattery() => _battery.Read();
        public IReadOnlyList<BatteryDeviceSnapshot> ReadBatteryDevices() => _battery.ReadDevices();
        public RaplSnapshot ReadRapl() => RaplSnapshot.Unavailable;
        public IReadOnlyDictionary<string, double> ReadRawEnergyMilliwatts() => new Dictionary<string, double>();
        public IReadOnlyList<EnergyDomainSnapshot> ReadEnergyDomains() => _powercap.Value.Read();
        public int? ReadBrightness(bool force = false) => null;
        public string? ReadForegroundProcess() => null;
        public void Dispose() { }
    }

    private sealed class UnavailableSensorProvider : IPlatformSensorProvider
    {
        public string GapSessionType => "SleepGap";
        public bool ShouldTrackSessions(BatterySnapshot battery) => false;
        public BatterySnapshot ReadBattery() => new() { Timestamp = DateTimeOffset.Now };
        public IReadOnlyList<BatteryDeviceSnapshot> ReadBatteryDevices() => Array.Empty<BatteryDeviceSnapshot>();
        public RaplSnapshot ReadRapl() => RaplSnapshot.Unavailable;
        public IReadOnlyDictionary<string, double> ReadRawEnergyMilliwatts() => new Dictionary<string, double>();
        public IReadOnlyList<EnergyDomainSnapshot> ReadEnergyDomains() => Array.Empty<EnergyDomainSnapshot>();
        public int? ReadBrightness(bool force = false) => null;
        public string? ReadForegroundProcess() => null;
        public void Dispose() { }
    }
}
