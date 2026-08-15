using GhostShell.Core;

namespace GhostShell.Application;

/// <summary>
/// Creates one stateful agent runtime for one live workspace. Runtimes must
/// never be shared between workspace instances because their active
/// conversation, history catalog, provider pin, and approvals are mutable.
/// </summary>
public interface IAgentWorkspaceRuntimeFactory
{
    IGovernedAgentRuntime Create(
        WorkspaceInstanceId workspaceId,
        AgentConversationScopeId conversationScopeId);
}
