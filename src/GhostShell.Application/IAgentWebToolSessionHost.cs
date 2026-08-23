using GhostShell.Core;

namespace GhostShell.Application;

public interface IAgentWebToolSessionHost
{
    ValueTask<HostResult<AgentWebToolResult>> RunAgentWebToolAsync(
        AgentAuthorizationId authorizationId,
        AgentWebToolAction action,
        CancellationToken cancellationToken);
}
