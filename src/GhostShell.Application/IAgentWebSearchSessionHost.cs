using GhostShell.Core;

namespace GhostShell.Application;

public interface IAgentWebSearchSessionHost
{
    ValueTask<HostResult<AgentWebSearchResult>> RunAgentWebSearchAsync(
        AgentAuthorizationId authorizationId,
        AgentWebSearchAction action,
        CancellationToken cancellationToken);
}
