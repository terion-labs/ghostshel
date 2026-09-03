using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Monitoring;

namespace GhostShell.Desktop;

/// <summary>
/// Selects the system-monitor implementation for a workspace while the session
/// host remains the sole owner of sessions and workspace graph links.
/// </summary>
internal sealed class WorkspaceSystemMonitorPanelSessionFactory(
    SystemMonitorPanelSessionFactory hostFactory) : ISystemMonitorPanelSessionFactory
{
    private readonly object _gate = new();
    private readonly Dictionary<WorkspaceInstanceId, ISystemMonitorPanelSessionFactory> _factories = [];

    public CapabilitySet StatisticsCapabilities => hostFactory.StatisticsCapabilities;

    public CapabilitySet ProcessMonitorCapabilities => hostFactory.ProcessMonitorCapabilities;

    public IDisposable Register(
        WorkspaceInstanceId workspaceId,
        ISystemMonitorPanelSessionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            if (!_factories.TryAdd(workspaceId, factory))
            {
                throw new InvalidOperationException(
                    "The workspace already has a system-monitor factory.");
            }
        }

        return new Registration(this, workspaceId, factory);
    }

    public ValueTask<IStatisticsPanelSession> CreateStatisticsAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        ConnectionProfile connection,
        CancellationToken cancellationToken) =>
        FactoryFor(workspaceId).CreateStatisticsAsync(
            workspaceId,
            sessionId,
            connection,
            cancellationToken);

    public ValueTask<IProcessMonitorPanelSession> CreateProcessMonitorAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        ConnectionProfile connection,
        CancellationToken cancellationToken) =>
        FactoryFor(workspaceId).CreateProcessMonitorAsync(
            workspaceId,
            sessionId,
            connection,
            cancellationToken);

    private ISystemMonitorPanelSessionFactory FactoryFor(WorkspaceInstanceId workspaceId)
    {
        lock (_gate)
        {
            return _factories.GetValueOrDefault(workspaceId, hostFactory);
        }
    }

    private void Unregister(
        WorkspaceInstanceId workspaceId,
        ISystemMonitorPanelSessionFactory factory)
    {
        lock (_gate)
        {
            if (_factories.TryGetValue(workspaceId, out var current)
                && ReferenceEquals(current, factory))
            {
                _factories.Remove(workspaceId);
            }
        }
    }

    private sealed class Registration(
        WorkspaceSystemMonitorPanelSessionFactory owner,
        WorkspaceInstanceId workspaceId,
        ISystemMonitorPanelSessionFactory factory) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unregister(workspaceId, factory);
            }
        }
    }
}
