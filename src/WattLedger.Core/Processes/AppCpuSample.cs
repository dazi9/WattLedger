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
