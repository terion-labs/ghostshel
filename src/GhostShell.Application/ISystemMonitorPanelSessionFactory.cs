using GhostShell.Core;

namespace GhostShell.Application;

public interface ISystemMonitorPanelSessionFactory
{
    CapabilitySet StatisticsCapabilities { get; }

    CapabilitySet ProcessMonitorCapabilities { get; }

    ValueTask<IStatisticsPanelSession> CreateStatisticsAsync(
        SessionId sessionId,
        CancellationToken cancellationToken);

    ValueTask<IProcessMonitorPanelSession> CreateProcessMonitorAsync(
        SessionId sessionId,
        CancellationToken cancellationToken);
}
