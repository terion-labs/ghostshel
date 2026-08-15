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
        AgentConversationScopeId conversationScopeId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId.Value))
        {
            throw new ArgumentException(
                "A live workspace identity is required.",
                nameof(workspaceId));
        }

        return ActivatorUtilities.CreateInstance<GovernedAgentRuntime>(
            services,
            workspaceId,
            conversationScopeId);
    }
}
