using System.Collections.Immutable;
using GhostShell.Agent;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private ImmutableArray<IAgentToolContribution> CreateToolContributions() =>
    [
        new WorkspaceGraphToolContribution(this),
        new PanelToolContribution(this),
        new TerminalToolContribution(this),
        new BrowserToolContribution(this),
        new ProcessToolContribution(this),
        new StatisticsToolContribution(this),
        new FileToolContribution(this),
        new McpToolContribution(this),
    ];

    private ResolvedAgentToolContribution? ResolveToolContribution(
        string toolName)
    {
        ResolvedAgentToolContribution? resolved = null;
        foreach (var contribution in _toolContributions)
        {
            var candidate = contribution.Resolve(toolName);
            if (candidate is null)
            {
                continue;
            }

            // A tool name can have only one owner. Ambiguity is rejected rather
            // than allowing registration order to select an execution path.
            if (resolved is not null)
            {
                return null;
            }

            resolved = candidate;
        }

        return resolved;
    }

    private async ValueTask<AgentToolResult>
        ExecuteOperationalToolContributionAsync(
            AgentToolExecutionRequest request,
            OperationalAgentToolExecutor executor,
            CancellationToken cancellationToken)
    {
        if (request.TargetContexts.Operational is not { } context
            || !MatchesPinnedScope(request.TargetContexts))
        {
            return CreateRejectedResult(request.Proposal, "target_changed");
        }

        var resizeAttachments = await InspectResizeAttachmentsAsync(
                context,
                cancellationToken)
            .ConfigureAwait(false);
        var browserEligiblePanelIds = await InspectBrowserAttachmentsAsync(
                context,
                cancellationToken)
            .ConfigureAwait(false);
        var fileMetadata = await InspectFileSessionsAsync(
                context,
                cancellationToken)
            .ConfigureAwait(false);
        var operationalContext = new OperationalAgentToolContext(
            context,
            resizeAttachments,
            browserEligiblePanelIds,
            fileMetadata);
        return await executor(
                request,
                operationalContext,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private interface IAgentToolContribution
    {
        ImmutableArray<AgentToolDefinition> BuildTools(
            AgentToolBuildContext context);

        ResolvedAgentToolContribution? Resolve(string toolName);
    }

    private sealed record AgentToolBuildContext(
        AgentContextSnapshot StructuralContext,
        AgentContextSnapshot OperationalContext,
        IReadOnlySet<PanelInstanceId> ResizeEligiblePanelIds,
        IReadOnlySet<PanelInstanceId> BrowserEligiblePanelIds,
        IReadOnlyDictionary<PanelInstanceId, FileSessionMetadata> FileMetadata,
        AgentMcpRunManifest? McpManifest)
    {
        public bool HasExactTarget => OperationalContext.Target
            is AgentTarget.Panel or AgentTarget.ConnectionSession;
    }

    private sealed record AgentToolExecutionRequest(
        AgentToolProposal Proposal,
        AgentToolDescriptor Descriptor,
        RunTargetContexts TargetContexts);

    private sealed record OperationalAgentToolContext(
        AgentContextSnapshot Context,
        IReadOnlyDictionary<PanelInstanceId, ResizeAttachmentBinding>
            ResizeAttachments,
        IReadOnlySet<PanelInstanceId> BrowserEligiblePanelIds,
        IReadOnlyDictionary<PanelInstanceId, FileSessionMetadata> FileMetadata)
    {
        public IReadOnlySet<PanelInstanceId> ResizeEligiblePanelIds =>
            ResizeAttachments.Keys.ToImmutableHashSet();
    }

    private sealed record ResolvedAgentToolContribution(
        string CatalogToolName,
        Func<
            AgentToolExecutionRequest,
            CancellationToken,
            ValueTask<AgentToolResult>> ExecuteAsync);

    private delegate ValueTask<AgentToolResult> OperationalAgentToolExecutor(
        AgentToolExecutionRequest request,
        OperationalAgentToolContext context,
        CancellationToken cancellationToken);
}
