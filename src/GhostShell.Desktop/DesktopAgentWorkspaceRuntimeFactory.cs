using GhostShell.Agent.Runtime;
using GhostShell.Application;
using GhostShell.Core;
using Microsoft.Extensions.DependencyInjection;

namespace GhostShell.Desktop;

internal sealed class DesktopAgentWorkspaceRuntimeFactory(
    IServiceProvider services) : IAgentWorkspaceRuntimeFactory
{
    public IGovernedAgentRuntime Create(
        WorkspaceInstanceId workspaceId,
        AgentConversationScopeId conversationScopeId,
        AgentPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(workspaceId.Value))
        {
            throw new ArgumentException(
                "A live workspace identity is required.",
                nameof(workspaceId));
        }

        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.IsValidForDurableStorage())
        {
            throw new ArgumentException(
                "A complete agent policy is required to create a workspace runtime.",
                nameof(policy));
        }

        return ActivatorUtilities.CreateInstance<GovernedAgentRuntime>(
            services,
            workspaceId,
            conversationScopeId,
            policy);
    }
}
