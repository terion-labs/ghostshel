namespace GhostShell.Application;

/// <summary>
/// Secret-minimized process metadata. Command lines, executable paths, users,
/// environment, open files, and cumulative processor time are intentionally
/// absent.
/// </summary>
public sealed record AgentProcessListEntry
{
    internal AgentProcessListEntry(
        int processId,
        AgentProcessDisplayName name,
        double? processorUsagePercent,
        long? workingSetBytes,
        DateTimeOffset? startedAtUtc,
        bool isGhostShell)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(processId);
        if (processorUsagePercent is { } processorUsage
            && (!double.IsFinite(processorUsage)
                || processorUsage is < 0 or > 100))
        {
            throw new ArgumentOutOfRangeException(
                nameof(processorUsagePercent));
        }

        if (workingSetBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workingSetBytes));
        }

        if (startedAtUtc is { Offset: var offset }
            && offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A process start timestamp must be UTC.",
                nameof(startedAtUtc));
        }

        ProcessId = processId;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ProcessorUsagePercent = processorUsagePercent;
        WorkingSetBytes = workingSetBytes;
        StartedAtUtc = startedAtUtc;
        IsGhostShell = isGhostShell;
    }

    public int ProcessId { get; }

    public AgentProcessDisplayName Name { get; }

    public double? ProcessorUsagePercent { get; }

    public long? WorkingSetBytes { get; }

    public DateTimeOffset? StartedAtUtc { get; }

    public bool IsGhostShell { get; }
}
