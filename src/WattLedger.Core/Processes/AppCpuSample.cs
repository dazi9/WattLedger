namespace WattLedger.Core.Processes;

/// <summary>One persisted per-minute application CPU row. Rows are written per app per
/// minute, never per process per sample: the roadmap forbids storing "every process × every
/// sampling point". <see cref="Windows"/> records how many sampling windows contributed, so
/// coverage gaps stay visible; every application is stored, not only a leaderboard.</summary>
public sealed record AppCpuSample(
    DateTimeOffset Minute,
    string AppName,
    double CpuSeconds,
    int ProcessCount,
    int Windows);

/// <summary>Aggregated application CPU usage over a query range.</summary>
public sealed record AppCpuTotal(
    string Name,
    double CpuSeconds,
    int Windows,
    int MaxProcessCount);

/// <summary>One persisted per-minute attribution row: an application's estimated energy
/// under a named model and energy source. Estimates are always surfaced labelled as
/// estimates and are never summed into the ledger's measured battery totals.</summary>
public sealed record AppEnergySample(
    DateTimeOffset Minute,
    string AppName,
    double EstimatedWh,
    double CpuSeconds,
    string EnergySource,
    string ModelVersion,
    int Windows);

/// <summary>Aggregated attribution over a query range, grouped by application and energy
/// source (a day can mix core-level windows with package-level fallback windows).</summary>
public sealed record AppEnergyTotal(
    string Name,
    string EnergySource,
    double EstimatedWh,
    double CpuSeconds,
    int Windows);
