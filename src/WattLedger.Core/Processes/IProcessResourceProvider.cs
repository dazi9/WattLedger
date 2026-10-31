using System.ComponentModel;

namespace WattLedger.Core.Processes;

/// <summary>Reads process instances with cumulative CPU time. Per the per-app roadmap this
/// collector reports resource quantities only; failures (exited process, denied access) skip
/// the instance instead of reporting zero consumption for it.</summary>
public interface IProcessResourceProvider
{
    IReadOnlyList<ProcessCpuSnapshot> ReadCpuSnapshots();
}

public static class ProcessResourceProvider
{
    public static IProcessResourceProvider Create() => OperatingSystem.IsWindows()
        ? new WindowsProcessCpuReader()
        : OperatingSystem.IsLinux()
            ? new LinuxProcessCpuReader()
            : new UnavailableProcessResourceProvider();
}

internal sealed class UnavailableProcessResourceProvider : IProcessResourceProvider
{
    public IReadOnlyList<ProcessCpuSnapshot> ReadCpuSnapshots() => Array.Empty<ProcessCpuSnapshot>();
}

/// <summary>Windows collector: process cumulative user+kernel time (the managed wrapper over
/// GetProcessTimes) plus the process start time as instance identity. Instances that exited
/// between enumeration and query, or that deny access, are skipped and therefore stay in the
/// unattributed remainder rather than in any application's number.</summary>
public sealed class WindowsProcessCpuReader : IProcessResourceProvider
{
    public IReadOnlyList<ProcessCpuSnapshot> ReadCpuSnapshots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<ProcessCpuSnapshot>();
        }

        var timestamp = DateTimeOffset.Now;
        var snapshots = new List<ProcessCpuSnapshot>();
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                snapshots.Add(new ProcessCpuSnapshot(
                    process.Id,
                    new DateTimeOffset(process.StartTime).ToUnixTimeMilliseconds(),
                    process.ProcessName,
                    process.TotalProcessorTime.TotalSeconds,
                    timestamp));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
            {
                // The process exited or access was denied: no sample for it this round.
            }
            finally
            {
                process.Dispose();
            }
        }

        return snapshots;
    }
}
