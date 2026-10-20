using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WattLedger.Core.Native;

public static class ForegroundProcessReader
{
    public static string? GetName()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public static int GetIdleSeconds()
    {
        try
        {
            var info = new NativeMethods.LASTINPUTINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>()
            };

            if (!NativeMethods.GetLastInputInfo(ref info))
            {
                return 0;
            }

            var tickCount = NativeMethods.GetTickCount();
            return (int)((tickCount - info.dwTime) / 1000);
        }
        catch
        {
            return 0;
        }
    }
}
