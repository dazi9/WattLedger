namespace WattLedger.Core.Processes;

/// <summary>Merges process instances into applications. The first-version identity rule is
/// the executable name (case-insensitive); the roadmap replaces it with platform app
/// identities later. Instance counts deduplicate by <see cref="ProcessKey"/> so one
/// application with several processes is counted once per process, not per sample.</summary>
public static class ProcessAppGrouper
{
    public static IReadOnlyList<AppCpuUsage> Group(IReadOnlyList<ProcessCpuUsage> usages)
    {
        ArgumentNullException.ThrowIfNull(usages);

        var byApp = new Dictionary<string, Dictionary<ProcessKey, double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var usage in usages)
        {
            var name = string.IsNullOrWhiteSpace(usage.Name) ? "unknown" : usage.Name;
            if (!byApp.TryGetValue(name, out var instances))
            {
                instances = new Dictionary<ProcessKey, double>();
                byApp[name] = instances;
            }

            var key = new ProcessKey(usage.Pid, usage.StartIdentity);
            instances[key] = instances.TryGetValue(key, out var existing) ? existing + usage.CpuSeconds : usage.CpuSeconds;
        }

        return byApp
            .Select(entry => new AppCpuUsage(
                entry.Key,
                entry.Value.Values.Sum(),
                entry.Value.Count))
            .OrderByDescending(app => app.CpuSeconds)
            .ThenBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
