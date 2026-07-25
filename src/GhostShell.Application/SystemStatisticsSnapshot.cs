namespace GhostShell.Application;

/// <summary>
/// A bounded local-host observation. "Observed" totals include only processes whose public
/// resource counters the operating system allowed GhostSHELL to read.
/// </summary>
public sealed record SystemStatisticsSnapshot(
    DateTimeOffset CapturedAtUtc,
    TimeSpan HostUptime,
    int LogicalProcessorCount,
    int EnumeratedProcessCount,
    int ObservedProcessCount,
    double? ObservedCpuPercent,
    long ObservedWorkingSetBytes,
    double? GhostShellCpuPercent,
    long GhostShellWorkingSetBytes);
