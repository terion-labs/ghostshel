namespace GhostShell.Monitoring.Tests;

internal sealed class SequenceProcessSnapshotSource : IProcessSnapshotSource
{
    private readonly Queue<Func<CancellationToken, RawProcessCapture>> _captures = [];

    public int CaptureCount { get; private set; }

    public void Enqueue(RawProcessCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        _captures.Enqueue(_ => capture);
    }

    public void Enqueue(Func<CancellationToken, RawProcessCapture> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        _captures.Enqueue(capture);
    }

    public void EnqueueFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _captures.Enqueue(_ => throw exception);
    }

    public RawProcessCapture Capture(CancellationToken cancellationToken)
    {
        CaptureCount++;
        cancellationToken.ThrowIfCancellationRequested();
        return _captures.Count > 0
            ? _captures.Dequeue()(cancellationToken)
            : throw new InvalidOperationException("No process capture was queued.");
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private long _timestamp;
    private DateTimeOffset _utcNow = utcNow;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        _utcNow += duration;
        _timestamp = checked(_timestamp + duration.Ticks);
    }
}
