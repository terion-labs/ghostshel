using GhostShell.Core;

namespace GhostShell.Application;

public interface IAgentDockerSessionHost
{
    ValueTask<HostResult<AgentDockerReadResult>> RunAgentDockerReadAsync(
        AgentAuthorizationId authorizationId,
        AgentDockerReadAction action,
        CancellationToken cancellationToken);
}
