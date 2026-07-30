namespace GhostShell.Application;

/// <summary>
/// A bounded local-host sample. CPU and working-set totals include only processes whose
/// public resource counters the operating system allowed the session host to read.
/// </summary>
public sealed record SystemStatisticsSnapshot(
    DateTimeOffset CapturedAtUtc,
    TimeSpan HostUptime,
    int LogicalProcessorCount,
    int EnumeratedProcessCount,
    int ObservedProcessCount,
    double? ObservedCpuPercent,
    long ObservedWorkingSetBytes);
