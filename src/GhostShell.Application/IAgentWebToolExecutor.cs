namespace GhostShell.Application;

public interface IAgentWebToolExecutor
{
    ValueTask<AgentWebToolExecutionResult> ExecuteAsync(
        AgentWebToolRequest request,
        CancellationToken cancellationToken);
}
