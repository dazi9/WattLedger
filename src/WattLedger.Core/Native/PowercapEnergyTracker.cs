namespace WattLedger.Core.Native;

/// <summary>Diffs cumulative powercap energy counters into per-interval watt-hours. Pairs
/// with <see cref="LinuxPowercapReader"/>; single-sampler, call <see cref="Collect"/> with
/// each new batch. Rules (mirroring the per-app roadmap's "no invented energy"):
/// first sight of a domain is a zero baseline; a counter that moved backwards is treated as
/// wraparound and reconstructed only when the domain declares max_energy_range_uj, otherwise
/// reported as zero; an unreadable reading clears the baseline so a later gap can never
/// smuggle a whole wrap cycle into one interval.</summary>
public sealed class PowercapEnergyTracker
{
    private const double MicrojoulesPerWattHour = 3_600_000_000.0;

    private readonly Dictionary<string, (string Name, long EnergyUj)> _previous = new(StringComparer.Ordinal);

    public IReadOnlyList<EnergyDomainDelta> Collect(IReadOnlyList<EnergyDomainSnapshot> current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var result = new List<EnergyDomainDelta>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in current)
        {
            if (snapshot.EnergyUj is not { } energy)
            {
                // Present but unreadable: forget any baseline so the next readable sample
                // restarts from zero instead of diffing across an unknown gap.
                _previous.Remove(snapshot.Path);
                continue;
            }

            if (!seen.Add(snapshot.Path))
            {
                continue; // Duplicate path in one batch keeps the first reading.
            }

            var energyWh = 0.0;
            if (_previous.TryGetValue(snapshot.Path, out var prior))
            {
                var deltaUj = energy - prior.EnergyUj;
                if (deltaUj < 0)
                {
                    // The counter wrapped (or was reset). Reconstruction needs the range.
                    deltaUj = snapshot.MaxRangeUj is { } range && range > 0
                        ? range - prior.EnergyUj + energy
                        : 0;
                }

                if (deltaUj > 0)
                {
                    energyWh = deltaUj / MicrojoulesPerWattHour;
                }
            }

            result.Add(new EnergyDomainDelta(snapshot.Name, snapshot.Path, energyWh));
            _previous[snapshot.Path] = (snapshot.Name, energy);
        }

        // Domains that disappeared are dropped from the baseline, like exited processes.
        foreach (var path in _previous.Keys.Where(path => !seen.Contains(path)).ToArray())
        {
            _previous.Remove(path);
        }

        return result;
    }
}
