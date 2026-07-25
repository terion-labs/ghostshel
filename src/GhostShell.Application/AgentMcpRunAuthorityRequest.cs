using GhostShell.Core;

namespace GhostShell.Application;

public sealed record AgentMcpRunAuthorityRequest
{
    public AgentMcpRunAuthorityRequest(
        AgentRunId runId,
        ActorDescriptor agent)
    {
        AgentRunRegistration.ValidateRunId(runId);
        RunId = runId;
        Agent = AgentRunRegistration.ValidateAgent(agent);
    }

    public AgentRunId RunId { get; }

    public ActorDescriptor Agent { get; }
}
