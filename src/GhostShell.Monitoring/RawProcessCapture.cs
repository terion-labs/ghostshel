namespace GhostShell.Monitoring;

internal sealed record RawProcessCapture(
    TimeSpan HostUptime,
    int EnumeratedProcessCount,
    IReadOnlyList<RawProcessObservation> Processes,
    bool IsTruncated);
