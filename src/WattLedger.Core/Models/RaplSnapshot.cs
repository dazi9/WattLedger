namespace WattLedger.Core.Models;

public sealed record RaplSnapshot
{
    public bool Available { get; init; }
    public double? PackageWatts { get; init; }
    public double? CpuWatts { get; init; }
    public double? GpuWatts { get; init; }
    public double? DramWatts { get; init; }
    public double? UncoreWatts { get; init; }
    public IReadOnlyDictionary<string, double> Components { get; init; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    public static RaplSnapshot Unavailable { get; } = new();
}
