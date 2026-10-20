using System.Runtime.InteropServices;

namespace WattLedger.Core.Native;

internal static class NativeMethods
{
    internal static readonly Guid GuidDevClassBattery = new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");

    internal const uint DigcfPresent = 0x02;
    internal const uint DigcfDeviceInterface = 0x10;

    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;
    internal static readonly IntPtr InvalidHandleValue = new(-1);

    internal const uint IoctlBatteryQueryTag = 0x294040;
    internal const uint IoctlBatteryQueryInformation = 0x294044;
    internal const uint IoctlBatteryQueryStatus = 0x29404C;

    internal const uint BatteryUnknownCapacity = 0xFFFFFFFF;
    internal const uint BatteryUnknownVoltage = 0xFFFFFFFF;
    internal const int BatteryUnknownRate = unchecked((int)0x80000000);

    internal const uint BatteryPowerOnLine = 0x00000001;
    internal const uint BatteryDischarging = 0x00000002;
    internal const uint BatteryCharging = 0x00000004;

    internal const int ErrorNoMoreItems = 259;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BATTERY_QUERY_INFORMATION
    {
        public uint BatteryTag;
        public uint InformationLevel;
        public int AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BATTERY_WAIT_STATUS
    {
        public uint BatteryTag;
        public uint Timeout;
        public uint PowerState;
        public uint LowCapacity;
        public uint HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BATTERY_STATUS
    {
        public uint PowerState;
        public uint Capacity;
        public uint Voltage;
        public int Rate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BATTERY_INFORMATION
    {
        public uint Capabilities;
        public byte Technology;
        public byte Reserved1;
        public byte Reserved2;
        public byte Reserved3;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] Chemistry;

        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
        public uint CriticalBias;
        public uint CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        IntPtr enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControlQueryTag(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref uint lpInBuffer,
        uint nInBufferSize,
        out uint lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControlQueryInformation(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref BATTERY_QUERY_INFORMATION lpInBuffer,
        uint nInBufferSize,
        out BATTERY_INFORMATION lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControlQueryInformationString(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref BATTERY_QUERY_INFORMATION lpInBuffer,
        uint nInBufferSize,
        byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControlQueryStatus(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref BATTERY_WAIT_STATUS lpInBuffer,
        uint nInBufferSize,
        out BATTERY_STATUS lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    internal static extern uint GetTickCount();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);
}
