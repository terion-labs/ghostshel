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
        AgentConversationScopeId conversationScopeId,
        AgentPolicy policy);
}

/// <summary>
/// Optional trusted desktop attachment for workspace-layout tools. Runtimes
/// without a live visual workspace never advertise those tools.
/// </summary>
public interface IAgentWorkspaceLayoutRuntime
{
    void AttachWorkspaceLayoutPort(
        IAgentWorkspaceLayoutMutationPort mutationPort);
}
