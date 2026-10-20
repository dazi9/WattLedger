using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace WattLedger.App;

internal static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WattLedger";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                        ?? throw new InvalidOperationException("Cannot open the Windows startup registry key.");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var processPath = Environment.ProcessPath
                          ?? Process.GetCurrentProcess().MainModule?.FileName
                          ?? throw new InvalidOperationException("Cannot determine the WattLedger executable path.");

        var command = Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? BuildDotnetCommand(processPath)
            : $"\"{processPath}\" --tray";

        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    private static string BuildDotnetCommand(string dotnetPath)
    {
        var entryName = Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrWhiteSpace(entryName))
        {
            return $"\"{dotnetPath}\" --tray";
        }

        var assemblyPath = Path.Combine(AppContext.BaseDirectory, entryName + ".dll");
        return $"\"{dotnetPath}\" \"{assemblyPath}\" --tray";
    }
}
