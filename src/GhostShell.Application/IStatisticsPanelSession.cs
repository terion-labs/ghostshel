namespace GhostShell.Application;

public interface IStatisticsPanelSession : IPanelSession
{
    ValueTask<MonitorPanelResult<SystemStatisticsSnapshot>> ReadStatisticsAsync(
        CancellationToken cancellationToken);
}
