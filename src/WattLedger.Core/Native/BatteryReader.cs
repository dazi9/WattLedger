using System.Runtime.InteropServices;
using System.Text;
using WattLedger.Core.Models;

namespace WattLedger.Core.Native;

public sealed record BatteryDeviceSnapshot
{
    public string DevicePath { get; init; } = "";
    public uint Tag { get; init; }
    public long? DesignedCapacityMwh { get; init; }
    public long? FullChargeCapacityMwh { get; init; }
    public long? RemainingCapacityMwh { get; init; }
    public int? VoltageMv { get; init; }
    public int? RateMw { get; init; }
    public uint PowerState { get; init; }
    public int? CycleCount { get; init; }
    public string? DeviceName { get; init; }
    public string? Manufacturer { get; init; }
    public string? SerialNumber { get; init; }
    public string? Chemistry { get; init; }

    public bool IsCharging => (PowerState & NativeMethods.BatteryCharging) != 0;
    public bool IsDischarging => (PowerState & NativeMethods.BatteryDischarging) != 0;
    public bool IsOnLine => (PowerState & NativeMethods.BatteryPowerOnLine) != 0;
}

public sealed class BatteryReader
{
    private readonly object _gate = new();

    public IReadOnlyList<BatteryDeviceSnapshot> ReadDevices()
    {
        var paths = EnumerateBatteryDevicePaths();
        if (paths.Count == 0)
        {
            return Array.Empty<BatteryDeviceSnapshot>();
        }

        var devices = new List<BatteryDeviceSnapshot>(paths.Count);
        foreach (var path in paths)
        {
            var device = QueryDevice(path);
            if (device is not null)
            {
                devices.Add(device);
            }
        }

        return devices;
    }

    public BatterySnapshot Read()
    {
        lock (_gate)
        {
            var system = SystemPowerReader.Read();
            var devices = ReadDevices();
            var timestamp = DateTimeOffset.Now;

            long? remaining = SumKnown(devices.Select(d => d.RemainingCapacityMwh));
            long? full = SumKnown(devices.Select(d => d.FullChargeCapacityMwh));
            long? design = SumKnown(devices.Select(d => d.DesignedCapacityMwh));

            int? percent = null;
            if (remaining is > 0 && full is > 0)
            {
                percent = (int)Math.Clamp(Math.Round(remaining.Value * 100.0 / full.Value), 0, 100);
            }
            percent ??= system.Percent;

            bool acOnline = system.AcOnline ?? devices.Any(d => d.IsOnLine);
            bool charging = system.Charging || devices.Any(d => d.IsCharging);
            bool discharging = system.Discharging || devices.Any(d => d.IsDischarging);

            long? dischargeMw = null;
            long? chargeMw = null;

            foreach (var device in devices)
            {
                if (device.RateMw is not { } rate || rate == 0)
                {
                    continue;
                }

                var magnitude = Math.Abs((long)rate);

                if (device.IsDischarging || (!acOnline && !device.IsCharging))
                {
                    dischargeMw = (dischargeMw ?? 0) + magnitude;
                }
                else if (device.IsCharging || acOnline)
                {
                    chargeMw = (chargeMw ?? 0) + magnitude;
                }
            }

            int? voltage = null;
            var voltages = devices
                .Where(d => d.VoltageMv is > 0)
                .Select(d => d.VoltageMv!.Value)
                .ToArray();
            if (voltages.Length > 0)
            {
                voltage = (int)Math.Round(voltages.Average());
            }

            int? cycleCount = null;
            var cycles = devices
                .Where(d => d.CycleCount is > 0)
                .Select(d => d.CycleCount!.Value)
                .ToArray();
            if (cycles.Length > 0)
            {
                cycleCount = cycles.Max();
            }

            var name = JoinDistinct(devices.Select(d => d.DeviceName));
            var manufacturer = JoinDistinct(devices.Select(d => d.Manufacturer));
            var serial = JoinDistinct(devices.Select(d => d.SerialNumber));
            var chemistry = JoinDistinct(devices.Select(d => d.Chemistry));

            return new BatterySnapshot
            {
                Timestamp = timestamp,
                IsAcOnline = acOnline,
                IsCharging = charging,
                IsDischarging = discharging,
                Percent = percent,
                RemainingCapacityMwh = remaining,
                FullChargeCapacityMwh = full,
                DesignCapacityMwh = design,
                VoltageMv = voltage,
                DischargeMw = dischargeMw,
                ChargeMw = chargeMw,
                CycleCount = cycleCount,
                BatteryCount = devices.Count,
                DeviceName = name,
                Manufacturer = manufacturer,
                SerialNumber = serial,
                Chemistry = chemistry
            };
        }
    }

    private static long? SumKnown(IEnumerable<long?> values)
    {
        long sum = 0;
        var any = false;
        foreach (var value in values)
        {
            if (value is > 0)
            {
                sum += value.Value;
                any = true;
            }
        }

        return any ? sum : null;
    }

    private static string? JoinDistinct(IEnumerable<string?> values)
    {
        var items = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return items.Length == 0 ? null : string.Join(" + ", items);
    }

    private static List<string> EnumerateBatteryDevicePaths()
    {
        var paths = new List<string>();
        var guid = NativeMethods.GuidDevClassBattery;
        var deviceInfoSet = NativeMethods.SetupDiGetClassDevs(
            ref guid,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.DigcfPresent | NativeMethods.DigcfDeviceInterface);

        if (deviceInfoSet == NativeMethods.InvalidHandleValue)
        {
            return paths;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new NativeMethods.SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.SP_DEVICE_INTERFACE_DATA>()
                };

                if (!NativeMethods.SetupDiEnumDeviceInterfaces(
                        deviceInfoSet,
                        IntPtr.Zero,
                        ref guid,
                        index,
                        ref interfaceData))
                {
                    break;
                }

                NativeMethods.SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet,
                    ref interfaceData,
                    IntPtr.Zero,
                    0,
                    out var requiredSize,
                    IntPtr.Zero);

                if (requiredSize == 0)
                {
                    continue;
                }

                var buffer = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    // cbSize is 8 on x64 and 6 on x86; the path starts at offset 4.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (NativeMethods.SetupDiGetDeviceInterfaceDetail(
                            deviceInfoSet,
                            ref interfaceData,
                            buffer,
                            requiredSize,
                            out _,
                            IntPtr.Zero))
                    {
                        var path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            paths.Add(path);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return paths;
    }

    private static BatteryDeviceSnapshot? QueryDevice(string path)
    {
        var handle = NativeMethods.CreateFile(
            path,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle == NativeMethods.InvalidHandleValue)
        {
            return null;
        }

        try
        {
            uint timeout = 0;
            if (!NativeMethods.DeviceIoControlQueryTag(
                    handle,
                    NativeMethods.IoctlBatteryQueryTag,
                    ref timeout,
                    sizeof(uint),
                    out var tag,
                    sizeof(uint),
                    out _,
                    IntPtr.Zero))
            {
                return null;
            }

            var query = new NativeMethods.BATTERY_QUERY_INFORMATION
            {
                BatteryTag = tag,
                InformationLevel = 0,
                AtRate = 0
            };

            if (!NativeMethods.DeviceIoControlQueryInformation(
                    handle,
                    NativeMethods.IoctlBatteryQueryInformation,
                    ref query,
                    (uint)Marshal.SizeOf<NativeMethods.BATTERY_QUERY_INFORMATION>(),
                    out var info,
                    (uint)Marshal.SizeOf<NativeMethods.BATTERY_INFORMATION>(),
                    out _,
                    IntPtr.Zero))
            {
                return null;
            }

            var wait = new NativeMethods.BATTERY_WAIT_STATUS
            {
                BatteryTag = tag,
                Timeout = 0,
                PowerState = 0,
                LowCapacity = 0,
                HighCapacity = 0
            };

            if (!NativeMethods.DeviceIoControlQueryStatus(
                    handle,
                    NativeMethods.IoctlBatteryQueryStatus,
                    ref wait,
                    (uint)Marshal.SizeOf<NativeMethods.BATTERY_WAIT_STATUS>(),
                    out var status,
                    (uint)Marshal.SizeOf<NativeMethods.BATTERY_STATUS>(),
                    out _,
                    IntPtr.Zero))
            {
                return null;
            }

            var designed = info.DesignedCapacity == NativeMethods.BatteryUnknownCapacity || info.DesignedCapacity == 0
                ? (long?)null
                : info.DesignedCapacity;
            var fullCharge = info.FullChargedCapacity == NativeMethods.BatteryUnknownCapacity || info.FullChargedCapacity == 0
                ? (long?)null
                : info.FullChargedCapacity;
            var remaining = status.Capacity == NativeMethods.BatteryUnknownCapacity
                ? (long?)null
                : status.Capacity;
            var voltage = status.Voltage == NativeMethods.BatteryUnknownVoltage
                ? (int?)null
                : (int)status.Voltage;
            var rate = status.Rate == NativeMethods.BatteryUnknownRate
                ? (int?)null
                : status.Rate;
            var chemistry = info.Chemistry is { Length: 4 }
                ? Encoding.ASCII.GetString(info.Chemistry).TrimEnd('\0')
                : null;

            return new BatteryDeviceSnapshot
            {
                DevicePath = path,
                Tag = tag,
                DesignedCapacityMwh = designed,
                FullChargeCapacityMwh = fullCharge,
                RemainingCapacityMwh = remaining,
                VoltageMv = voltage,
                RateMw = rate,
                PowerState = status.PowerState,
                CycleCount = info.CycleCount == 0 ? null : (int)info.CycleCount,
                DeviceName = QueryString(handle, tag, 4),
                Manufacturer = QueryString(handle, tag, 6),
                SerialNumber = QueryString(handle, tag, 7),
                Chemistry = chemistry
            };
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static string? QueryString(IntPtr handle, uint tag, uint level)
    {
        var query = new NativeMethods.BATTERY_QUERY_INFORMATION
        {
            BatteryTag = tag,
            InformationLevel = level,
            AtRate = 0
        };

        var buffer = new byte[512];
        if (!NativeMethods.DeviceIoControlQueryInformationString(
                handle,
                NativeMethods.IoctlBatteryQueryInformation,
                ref query,
                (uint)Marshal.SizeOf<NativeMethods.BATTERY_QUERY_INFORMATION>(),
                buffer,
                (uint)buffer.Length,
                out var bytesReturned,
                IntPtr.Zero))
        {
            return null;
        }

        var length = (int)Math.Min(bytesReturned, (uint)buffer.Length);
        if (length == 0)
        {
            return null;
        }

        return Encoding.Unicode.GetString(buffer, 0, length).TrimEnd('\0').Trim();
    }
}
