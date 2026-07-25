using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Monitoring;

public sealed class SystemMonitorPanelSessionFactory : ISystemMonitorPanelSessionFactory
{
    private readonly IProcessSnapshotSource _source;
    private readonly TimeProvider _timeProvider;

    public SystemMonitorPanelSessionFactory(TimeProvider timeProvider)
        : this(new SystemProcessSnapshotSource(), timeProvider)
    {
    }

    internal SystemMonitorPanelSessionFactory(
        IProcessSnapshotSource source,
        TimeProvider timeProvider)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

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

    public ValueTask<IStatisticsPanelSession> CreateStatisticsAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IStatisticsPanelSession>(new StatisticsPanelSession(
            sessionId,
            NewSampler(),
            StatisticsCapabilities,
            _timeProvider));
    }

    public ValueTask<IProcessMonitorPanelSession> CreateProcessMonitorAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IProcessMonitorPanelSession>(new ProcessMonitorPanelSession(
            sessionId,
            NewSampler(),
            ProcessMonitorCapabilities,
            _timeProvider));
    }

    private ProcessResourceSampler NewSampler() => new(_source, _timeProvider);
}
