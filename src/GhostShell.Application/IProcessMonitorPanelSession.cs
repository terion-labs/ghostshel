namespace GhostShell.Application;

public interface IProcessMonitorPanelSession : IPanelSession
{
    ValueTask<MonitorPanelResult<ProcessMonitorSnapshot>> ListProcessesAsync(
        ProcessMonitorQuery query,
        CancellationToken cancellationToken);
}
