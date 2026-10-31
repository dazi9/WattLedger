using System.Globalization;

namespace WattLedger.Core.Native;

/// <summary>One discovered powercap energy domain. <see cref="EnergyUj"/> is the domain's
/// cumulative microjoule counter; it is null when the domain exists but the value is not
/// readable (permission or transient failure) — that absence is deliberately distinct from
/// zero, per the capability rules in the per-app roadmap.</summary>
public sealed record EnergyDomainSnapshot(
    string Name,
    string Path,
    long? EnergyUj,
    long? MaxRangeUj,
    DateTimeOffset Timestamp);

/// <summary>Cumulative energy one domain consumed inside a sampling interval, already
/// converted to watt-hours. First sight of a domain reports zero; a wraparound is
/// reconstructed only when the domain declares its counter range.</summary>
public sealed record EnergyDomainDelta(string Name, string Path, double EnergyWh);

/// <summary>Reads Linux powercap energy domains (Intel/AMD RAPL exposed via
/// /sys/class/powercap). Interface presence does not guarantee data: the counter is
/// cumulative, often root-only or gated by the intel_rapl module, and wraps at
/// max_energy_range_uj — all of which the paired <see cref="PowercapEnergyTracker"/>
/// handles for callers.</summary>
public sealed class LinuxPowercapReader
{
    private readonly string _powercapPath;

    public LinuxPowercapReader(string? powercapPath = null)
    {
        _powercapPath = powercapPath ?? "/sys/class/powercap";
    }

    public IReadOnlyList<EnergyDomainSnapshot> Read()
    {
        var timestamp = DateTimeOffset.Now;
        var snapshots = new List<EnergyDomainSnapshot>();
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(_powercapPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return snapshots;
        }

        foreach (var directory in directories)
        {
            // A domain is identified by name or energy_uj; a domain whose energy_uj is
            // missing or unreadable still counts as present with a null value, so the
            // capability stays distinguishable from "hardware not provided". The control
            // directory itself (e.g. /sys/class/powercap/intel-rapl) has neither file.
            var hasEnergyFile = File.Exists(Path.Combine(directory, "energy_uj"));
            var declaredName = ReadText(directory, "name");
            if (!hasEnergyFile && declaredName is null)
            {
                continue;
            }

            var name = declaredName ?? Path.GetFileName(directory);
            snapshots.Add(new EnergyDomainSnapshot(
                name,
                directory,
                ReadLong(directory, "energy_uj"),
                ReadLong(directory, "max_energy_range_uj"),
                timestamp));
        }

        return snapshots
            .OrderBy(snapshot => snapshot.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ReadText(string directory, string file)
    {
        try
        {
            return File.ReadAllText(Path.Combine(directory, file)).Trim() is { Length: > 0 } value ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? ReadLong(string directory, string file)
    {
        var text = ReadText(directory, file);
        return text is not null && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
