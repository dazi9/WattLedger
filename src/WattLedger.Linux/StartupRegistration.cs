using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace WattLedger.Linux;

internal static class StartupRegistration
{
    public static string EntryPath
    {
        get
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome) || !Path.IsPathRooted(configHome))
            {
                configHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }

            return Path.Combine(configHome, "autostart", "wattledger.desktop");
        }
    }

    public static bool IsEnabled => File.Exists(EntryPath);

    public static void SetEnabled(bool enabled, string? databasePath)
    {
        if (!enabled)
        {
            if (File.Exists(EntryPath)) File.Delete(EntryPath);
            return;
        }

        var directory = Path.GetDirectoryName(EntryPath)!;
        Directory.CreateDirectory(directory);
        var (program, assemblyPath) = GetLaunchTarget();
        var launchArguments = new List<string> { program };
        if (assemblyPath is not null) launchArguments.Add(assemblyPath);
        launchArguments.Add("--tray");
        launchArguments.Add($"--backend={DesktopBackendOptions.StartupPreference.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(databasePath))
        {
            launchArguments.Add("--db");
            launchArguments.Add(databasePath);
        }

        File.WriteAllText(EntryPath, BuildDesktopEntry(launchArguments), new UTF8Encoding(false));
    }

    internal static string BuildDesktopEntry(IEnumerable<string> arguments)
    {
        var exec = BuildExecValue(arguments);
        // Desktop entry files are LF-separated by spec; AppendLine would emit CRLF on Windows.
        return new StringBuilder()
            .Append("[Desktop Entry]\n")
            .Append("Type=Application\n")
            .Append("Name=WattLedger\n")
            .Append("Comment=Local battery energy journal\n")
            .Append($"Exec={exec}\n")
            .Append("Terminal=false\n")
            .Append("X-GNOME-Autostart-enabled=true\n")
            .ToString();
    }

    internal static string DesktopQuote(string value)
    {
        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new ArgumentException("Desktop entry arguments cannot contain newlines.", nameof(value));
        }

        var escaped = value.Replace("%", "%%", StringComparison.Ordinal)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    internal static string BuildExecValue(IEnumerable<string> arguments) =>
        DesktopValueEscape(string.Join(" ", arguments.Select(DesktopQuote)));

    private static string DesktopValueEscape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static (string Program, string? AssemblyPath) GetLaunchTarget()
    {
        var program = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate the running executable.");
        if (Path.GetFileNameWithoutExtension(program).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
            if (string.IsNullOrWhiteSpace(assemblyName))
            {
                throw new InvalidOperationException("Could not locate the application assembly for startup registration.");
            }

            var assembly = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
            if (!File.Exists(assembly))
            {
                throw new InvalidOperationException($"The application assembly was not found at {assembly}.");
            }

            return (program, assembly);
        }

        return (program, null);
    }
}
