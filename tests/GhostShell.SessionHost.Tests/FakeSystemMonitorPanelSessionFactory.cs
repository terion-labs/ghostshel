using System.Runtime.CompilerServices;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.SessionHost.Tests;

internal sealed class FakeSystemMonitorPanelSessionFactory
    : ISystemMonitorPanelSessionFactory
{
    private readonly Dictionary<SessionId, FakeProcessMonitorPanelSession> _processes = [];
    private readonly Dictionary<SessionId, FakeStatisticsPanelSession> _statistics = [];

    public CapabilitySet StatisticsCapabilities { get; } = new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.StatisticsRead,
    ]);

    public CapabilitySet ProcessMonitorCapabilities { get; } = new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.ProcessesList,
    ]);

    public int StatisticsCreateCount { get; private set; }

    public int ProcessMonitorCreateCount { get; private set; }

    public FakeStatisticsPanelSession Statistics(SessionId id) => _statistics[id];

    public FakeProcessMonitorPanelSession Processes(SessionId id) => _processes[id];

    public ValueTask<IStatisticsPanelSession> CreateStatisticsAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StatisticsCreateCount++;
        var session = new FakeStatisticsPanelSession(
            sessionId,
            StatisticsCapabilities);
        _statistics.Add(sessionId, session);
        return ValueTask.FromResult<IStatisticsPanelSession>(session);
    }

    public ValueTask<IProcessMonitorPanelSession> CreateProcessMonitorAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProcessMonitorCreateCount++;
        var session = new FakeProcessMonitorPanelSession(
            sessionId,
            ProcessMonitorCapabilities);
        _processes.Add(sessionId, session);
        return ValueTask.FromResult<IProcessMonitorPanelSession>(session);
    }
}

internal abstract class FakeMonitorPanelSession(
    SessionId id,
    PanelKind kind,
    CapabilitySet capabilities) : IPanelSession
{
    private CapabilitySet _capabilities = capabilities;
    private bool _closed;

    public SessionId Id { get; } = id;

    public PanelKind Kind { get; } = kind;

    public CapabilitySet Capabilities => _capabilities;

    public int CloseCount { get; private set; }

    public int DisposeCount { get; private set; }

    public bool IsClosed => _closed;

    public PanelCloseMode? LastCloseMode { get; private set; }

    public void RemoveCapability(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        _capabilities = new CapabilitySet(
            _capabilities.Values.Where(
                value => !string.Equals(
                    value,
                    capability,
                    StringComparison.Ordinal)));
    }

    public ValueTask<PanelSessionSnapshot> SnapshotAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_closed
            ? new PanelSessionSnapshot(
                SessionLifecycle.Closed,
                SessionHealth.Ended,
                false,
                "Closed")
            : new PanelSessionSnapshot(
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                false,
                "Ready"));
    }

    public async IAsyncEnumerable<PanelSessionEvent> WatchAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = afterSequence;
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloseCount++;
        LastCloseMode = mode;
        if (_closed)
        {
            return ValueTask.FromResult(PanelCloseOutcome.AlreadyClosed);
        }

        _closed = true;
        return ValueTask.FromResult(mode == PanelCloseMode.Force
            ? PanelCloseOutcome.ForceTerminated
            : PanelCloseOutcome.GracefullyClosed);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        _closed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeStatisticsPanelSession(
    SessionId id,
    CapabilitySet capabilities)
    : FakeMonitorPanelSession(id, PanelKind.Statistics, capabilities),
      IStatisticsPanelSession
{
    public int ReadCount { get; private set; }

    public ValueTask<MonitorPanelResult<SystemStatisticsSnapshot>> ReadStatisticsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        return ValueTask.FromResult(MonitorPanelResult<SystemStatisticsSnapshot>.Success(
            new SystemStatisticsSnapshot(
                DateTimeOffset.UnixEpoch,
                TimeSpan.FromHours(1),
                4,
                8,
                7,
                12.5,
                4_096,
                1.5,
                512)));
    }
}

internal sealed class FakeProcessMonitorPanelSession(
    SessionId id,
    CapabilitySet capabilities)
    : FakeMonitorPanelSession(id, PanelKind.ProcessMonitor, capabilities),
      IProcessMonitorPanelSession
{
    public int ListCount { get; private set; }

    public bool BlockList { get; set; }

    public TaskCompletionSource ListStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReleaseList { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MonitorPanelError? ListError { get; set; }

    public ProcessMonitorQuery? LastQuery { get; private set; }

    public ProcessMonitorSnapshot Snapshot { get; set; } =
        new(
            DateTimeOffset.UnixEpoch,
            [],
            0,
            0,
            false);

    public async ValueTask<MonitorPanelResult<ProcessMonitorSnapshot>> ListProcessesAsync(
        ProcessMonitorQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ListCount++;
        LastQuery = query;
        ListStarted.TrySetResult();
        if (BlockList)
        {
            await ReleaseList.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (ListError is { } error)
        {
            return MonitorPanelResult<ProcessMonitorSnapshot>.Failure(error);
        }

        return MonitorPanelResult<ProcessMonitorSnapshot>.Success(
            Snapshot);
    }
}
