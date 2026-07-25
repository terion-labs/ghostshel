using GhostShell.Core;

namespace GhostShell.Application;

/// <summary>
/// Plans and tests durable connection definitions without exposing adapter or credential material.
/// </summary>
public interface IConnectionRuntime
{
    ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken);

    ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken);
}
