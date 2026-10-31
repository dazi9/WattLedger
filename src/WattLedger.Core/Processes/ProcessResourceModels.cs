namespace WattLedger.Core.Processes;

/// <summary>Distinguishes a process instance across samples: the OS reuses PIDs, so the
/// platform's start identity is part of the key. Two instances that share a PID but not an
/// identity are different processes and must never be diffed against each other.</summary>
public readonly record struct ProcessKey(int Pid, long StartIdentity);

/// <summary>One sampled process instance with its cumulative CPU time. Names come from the
/// executable only; window titles and command lines are deliberately never captured.</summary>
public sealed record ProcessCpuSnapshot(
    int Pid,
    long StartIdentity,
    string Name,
    double TotalCpuSeconds,
    DateTimeOffset Timestamp);

/// <summary>CPU time one process instance consumed inside a sampling interval. New instances
/// report zero for their first interval because their earlier consumption predates the window.</summary>
public sealed record ProcessCpuUsage(
    int Pid,
    long StartIdentity,
    string Name,
    double CpuSeconds);

/// <summary>Resource usage of one application (a group of process instances). This is a
/// resource quantity — CPU seconds — never an energy quantity; energy attribution is a
/// separate, explicitly-labelled layer (see docs/per-app roadmap).</summary>
public sealed record AppCpuUsage(
    string Name,
    double CpuSeconds,
    int ProcessCount);
