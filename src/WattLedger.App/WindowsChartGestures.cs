using System.Runtime.InteropServices;
using WattLedger.Desktop;

namespace WattLedger.App;

/// <summary>Input ownership and cumulative-distance decoding, independent of HWNDs or WPF.</summary>
internal sealed class WindowsChartZoomSession
{
    private enum InputOwner { None, NativeGesture, Manipulation }
    private InputOwner _owner;
    private uint _sequence;
    private readonly ChartScaleSession _nativeScale = new();

    public bool IsActive => _owner != InputOwner.None;
    public bool IsManipulationActive => _owner == InputOwner.Manipulation;

    public bool BeginManipulation()
    {
        if (_owner == InputOwner.NativeGesture) return false;
        _owner = InputOwner.Manipulation;
        return true;
    }

    public void EndManipulation()
    {
        if (_owner == InputOwner.Manipulation) _owner = InputOwner.None;
    }

    public bool NativeZoom(uint sequence, uint flags, double distance, bool startsInPlot, out double factor)
    {
        factor = 1;
        // WPF touch handling already owns this input; consume any promoted duplicate without zooming again.
        if (_owner == InputOwner.Manipulation) return true;
        if (_owner != InputOwner.NativeGesture || sequence != _sequence || (flags & WindowsChartGestureInterop.Begin) != 0)
        {
            EndNative();
            if (!startsInPlot) return false;
            _owner = InputOwner.NativeGesture;
            _sequence = sequence;
            _nativeScale.Begin(distance);
        }
        else factor = _nativeScale.Update(distance);
        if ((flags & WindowsChartGestureInterop.End) != 0) EndNative();
        return true;
    }

    public void EndNative()
    {
        if (_owner == InputOwner.NativeGesture) _owner = InputOwner.None;
        _nativeScale.End();
    }

    public void Reset() { _owner = InputOwner.None; _nativeScale.End(); }
}

internal static class WindowsChartGestureInterop
{
    internal const int MouseWheel = 0x020A;
    internal const int MouseHorizontalWheel = 0x020E;
    internal const int Gesture = 0x0119;
    internal const uint GestureEnd = 2;
    internal const uint Zoom = 3;
    internal const uint Begin = 1;
    internal const uint End = 4;
    private const int Shift = 0x0004;
    private const int Control = 0x0008;

    internal static double WheelSteps(IntPtr parameter) => (short)((parameter.ToInt64() >> 16) & 0xffff) / 120d;
    internal static bool IsControlDown(IntPtr parameter) => (parameter.ToInt64() & Control) != 0;
    internal static bool IsShiftDown(IntPtr parameter) => (parameter.ToInt64() & Shift) != 0;
    internal static (short X, short Y) ScreenPoint(IntPtr parameter) =>
        ((short)(parameter.ToInt64() & 0xffff), (short)((parameter.ToInt64() >> 16) & 0xffff));

    internal static double ManipulationFactor(double scaleX, double scaleY) =>
        double.IsFinite(scaleX) && double.IsFinite(scaleY) && scaleX > 0 && scaleY > 0
            ? ChartGestureScale.MagnificationFactor(Math.Sqrt(scaleX) * Math.Sqrt(scaleY)) : 1;

    // GESTUREINFO uses native pointer alignment. Locations and distances are physical screen pixels.
    [StructLayout(LayoutKind.Sequential)]
    internal struct GestureInfo
    {
        public uint Size;
        public uint Flags;
        public uint Id;
        public IntPtr Target;
        public short X;
        public short Y;
        public uint InstanceId;
        public uint SequenceId;
        public ulong Arguments;
        public uint ExtraSize;
    }

    internal static bool TryRead(IntPtr handle, out GestureInfo information)
    {
        information = new GestureInfo { Size = (uint)Marshal.SizeOf<GestureInfo>() };
        return GetGestureInfo(handle, ref information);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGestureInfo(IntPtr handle, ref GestureInfo information);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseGestureInfoHandle(IntPtr handle);
}
