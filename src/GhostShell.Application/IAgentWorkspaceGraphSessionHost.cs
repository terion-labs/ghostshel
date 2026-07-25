using GhostShell.Core;

namespace GhostShell.Application;

/// <summary>
/// Governed read-only workspace graph execution. The host re-resolves the
/// original run target before and after consuming one-action authorization.
/// </summary>
public interface IAgentWorkspaceGraphSessionHost
{
    ValueTask<HostResult<AgentWorkspaceGraphActionResult>>
        RunAgentWorkspaceGraphActionAsync(
            AgentAuthorizationId authorizationId,
            AgentWorkspaceGraphAction action,
            CancellationToken cancellationToken);
}
