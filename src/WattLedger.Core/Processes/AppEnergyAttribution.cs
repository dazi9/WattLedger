using WattLedger.Core.Native;

namespace WattLedger.Core.Processes;

/// <summary>One application's estimated energy for a window, produced by the CPU-share
/// model. Estimates are labelled as estimates everywhere they surface; they are never
/// ledger totals.</summary>
public sealed record AppEnergyEstimate(string AppName, double EstimatedWh, double CpuSeconds);

/// <summary>The outcome of one attribution window: which domain pool was attributed, what
/// was deliberately left unattributed (readable domains that are neither the pool nor its
/// containing package), and the per-application estimates.</summary>
public sealed record EnergyAttributionResult(
    string ModelVersion,
    string EnergySource,
    bool PackageLevelOnly,
    double PoolWh,
    double RemainderWh,
    IReadOnlyList<AppEnergyEstimate> Estimates);

/// <summary>CPU-share attribution, first version of the roadmap's model:
/// "attributable CPU energy × app CPU weight ÷ total CPU weight over the window".
/// The rules that keep the result explainable:
/// - the pool is the "core" subdomain when present (true CPU domain); only when no core
///   subdomain reports energy does the model fall back to package domains, flagged
///   <see cref="EnergyAttributionResult.PackageLevelOnly"/> because a package may include
///   non-CPU activity;
/// - the denominator is the full observed process CPU time (the tracker sees every
///   process), so shares are never normalized across a hand-picked leaderboard;
/// - other readable domains (uncore, graphics, DRAM) are reported as
///   <see cref="EnergyAttributionResult.RemainderWh"/> and never added into applications —
///   the pool and its containing package domain are excluded so nothing is summed twice;
/// - with no readable energy domain, or no CPU activity in the window, the model returns
///   null: idle and unattributable energy stay unattributed instead of being invented.</summary>
public static class AppEnergyAttribution
{
    public const string ModelVersion = "app-cpu-share-v1";

    public static EnergyAttributionResult? Attribute(
        IReadOnlyList<AppCpuUsage> apps,
        IReadOnlyList<EnergyDomainDelta> domains)
    {
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(domains);

        double coreWh = 0, packageWh = 0;
        var coreSeen = false;
        var packageSeen = false;
        foreach (var domain in domains)
        {
            if (domain.Name.Equals("core", StringComparison.OrdinalIgnoreCase))
            {
                coreWh += domain.EnergyWh;
                coreSeen = true;
            }
            else if (domain.Name.StartsWith("package", StringComparison.OrdinalIgnoreCase))
            {
                packageWh += domain.EnergyWh;
                packageSeen = true;
            }
        }

        string source;
        bool packageLevelOnly;
        double poolWh;
        if (coreSeen && coreWh > 0)
        {
            source = "core (powercap)";
            packageLevelOnly = false;
            poolWh = coreWh;
        }
        else if (packageSeen && packageWh > 0)
        {
            source = "package (powercap)";
            packageLevelOnly = true;
            poolWh = packageWh;
        }
        else
        {
            return null;
        }

        double denominator = 0;
        foreach (var app in apps)
        {
            if (app.CpuSeconds > 0)
            {
                denominator += app.CpuSeconds;
            }
        }

        if (denominator <= 0)
        {
            return null;
        }

        var estimates = apps
            .Where(app => app.CpuSeconds > 0)
            .Select(app => new AppEnergyEstimate(
                app.Name,
                poolWh * app.CpuSeconds / denominator,
                app.CpuSeconds))
            .OrderByDescending(estimate => estimate.EstimatedWh)
            .ToArray();

        // Remainder = readable energy that is neither the pool nor an ancestor of the pool
        // (package contains its core/uncore subdomains), so overlapping domains are never
        // summed twice and real non-pool energy (e.g. DRAM) stays visible.
        double remainder = 0;
        foreach (var domain in domains)
        {
            var isPool = domain.Name.Equals(packageLevelOnly ? "package" : "core", StringComparison.OrdinalIgnoreCase)
                         || (!packageLevelOnly && domain.Name.StartsWith("package", StringComparison.OrdinalIgnoreCase));
            if (!isPool)
            {
                remainder += domain.EnergyWh;
            }
        }

        return new EnergyAttributionResult(ModelVersion, source, packageLevelOnly, poolWh, remainder, estimates);
    }
}
