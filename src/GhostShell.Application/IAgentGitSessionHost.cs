using GhostShell.Core;

namespace GhostShell.Application;

public interface IAgentGitSessionHost
{
    ValueTask<HostResult<GitAgentOperationResult>> RunAgentGitActionAsync(
        AgentAuthorizationId authorizationId,
        AgentGitAction action,
        CancellationToken cancellationToken);
}
