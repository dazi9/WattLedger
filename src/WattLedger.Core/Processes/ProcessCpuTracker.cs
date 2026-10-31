namespace WattLedger.Core.Processes;

/// <summary>Turns cumulative per-process CPU counters into per-interval consumption.
/// The tracker is stateful and single-sampler: call <see cref="Collect"/> with each new
/// snapshot batch; the first call only establishes the baseline and yields zero usage.</summary>
public sealed class ProcessCpuTracker
{
    private readonly Dictionary<ProcessKey, (string Name, double TotalCpuSeconds)> _previous = new();

    /// <summary>Interval CPU seconds per process instance seen in <paramref name="current"/>.
    /// Exited instances are dropped; instances not seen before report zero for this interval
    /// so boot-time consumption is never attributed to the measurement window. A counter that
    /// moved backwards (kernel reset or reused identity) is clamped to zero instead of
    /// inventing usage.</summary>
    public IReadOnlyList<ProcessCpuUsage> Collect(IReadOnlyList<ProcessCpuSnapshot> current)
    {
        ArgumentNullException.ThrowIfNull(current);

        // Same-key duplicates within one batch keep the last sample, matching "the newest
        // reading wins" without double-counting a single instance.
        var samples = new Dictionary<ProcessKey, ProcessCpuSnapshot>(current.Count);
        foreach (var snapshot in current)
        {
            samples[new ProcessKey(snapshot.Pid, snapshot.StartIdentity)] = snapshot;
        }

        var result = new List<ProcessCpuUsage>(samples.Count);
        foreach (var (key, snapshot) in samples)
        {
            var usage = 0d;
            if (_previous.TryGetValue(key, out var prior))
            {
                var delta = snapshot.TotalCpuSeconds - prior.TotalCpuSeconds;
                if (delta > 0)
                {
                    usage = delta;
                }
            }

            result.Add(new ProcessCpuUsage(snapshot.Pid, snapshot.StartIdentity, snapshot.Name, usage));
        }

        _previous.Clear();
        foreach (var (key, snapshot) in samples)
        {
            _previous[key] = (snapshot.Name, snapshot.TotalCpuSeconds);
        }

        return result;
    }
}
