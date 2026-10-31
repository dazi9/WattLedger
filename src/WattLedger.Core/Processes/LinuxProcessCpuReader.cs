using System.Runtime.InteropServices;

namespace WattLedger.Core.Processes;

/// <summary>Linux collector: cumulative utime+stime from /proc/&lt;pid&gt;/stat, with the
/// stat starttime field (clock ticks since boot) as the instance identity that defends
/// against PID reuse. Process names come from comm, which the kernel truncates to 15
/// characters — the coarsest identifier that is still stable and needs no extra privileges.
/// Entries that vanish or deny access mid-read are skipped.</summary>
public sealed class LinuxProcessCpuReader : IProcessResourceProvider
{
    private const int ClockTicksPerSecondFallback = 100;
    private const int SysconfClockTicksPerSecond = 2; // _SC_CLK_TCK

    private readonly string _procPath;
    private readonly long _clockTicksPerSecond;

    public LinuxProcessCpuReader(string? procPath = null, long? clockTicksPerSecond = null)
    {
        _procPath = procPath ?? "/proc";
        _clockTicksPerSecond = clockTicksPerSecond ?? DetectClockTicksPerSecond();
    }

    public IReadOnlyList<ProcessCpuSnapshot> ReadCpuSnapshots()
    {
        if (!OperatingSystem.IsLinux() && !Directory.Exists(_procPath))
        {
            return Array.Empty<ProcessCpuSnapshot>();
        }

        var timestamp = DateTimeOffset.Now;
        var snapshots = new List<ProcessCpuSnapshot>();
        string[] processDirectories;
        try
        {
            processDirectories = Directory.GetDirectories(_procPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return snapshots;
        }

        foreach (var directory in processDirectories)
        {
            var name = Path.GetFileName(directory);
            if (!int.TryParse(name, out var pid) || pid < 0)
            {
                // /proc also hosts non-PID entries such as acpi, sys and self.
                continue;
            }

            var snapshot = ReadStat(directory, pid, timestamp);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }

    private ProcessCpuSnapshot? ReadStat(string directory, int pid, DateTimeOffset timestamp)
    {
        string text;
        try
        {
            text = File.ReadAllText(Path.Combine(directory, "stat"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // Exited between enumeration and read, or hidden by procfs permissions.
        }

        var fields = ParseStat(text);
        if (fields is null)
        {
            return null;
        }

        var (name, userTicks, systemTicks, startTicks) = fields.Value;
        var totalSeconds = (userTicks + systemTicks) / (double)_clockTicksPerSecond;
        return new ProcessCpuSnapshot(pid, startTicks, name, totalSeconds, timestamp);
    }

    /// <summary>Parses one /proc/&lt;pid&gt;/stat line. comm may contain spaces and
    /// parentheses, so the value fields are located relative to the last ')' (field 3
    /// onwards): utime is field 14, stime field 15, starttime field 22.</summary>
    internal static (string Name, long UserTicks, long SystemTicks, long StartTicks)? ParseStat(string text)
    {
        var open = text.IndexOf('(');
        var close = text.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return null;
        }

        var name = text[(open + 1)..close];
        var rest = text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (rest.Length < 20)
        {
            return null;
        }

        if (!long.TryParse(rest[11], out var userTicks) ||
            !long.TryParse(rest[12], out var systemTicks) ||
            !long.TryParse(rest[19], out var startTicks))
        {
            return null;
        }

        return (name, userTicks, systemTicks, startTicks);
    }

    private static long DetectClockTicksPerSecond()
    {
        try
        {
            var value = Sysconf(SysconfClockTicksPerSecond);
            return value > 0 ? value : ClockTicksPerSecondFallback;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // The portable userspace default on Linux is 100 Hz.
            return ClockTicksPerSecondFallback;
        }
    }

    [DllImport("libc", SetLastError = false)]
    private static extern long Sysconf(int name);
}
