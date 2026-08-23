using System.Globalization;
using System.Text;
using GhostShell.Core;

namespace GhostShell.Application;

/// <summary>
/// Binds one anonymous search query to the run target and one-action
/// authorization consumed immediately before browser execution.
/// </summary>
public sealed class AgentWebSearchActionComposer
{
    public AgentWebSearchAction Prepare(
        AgentActionEnvelope envelope,
        AgentContextSnapshot context,
        AgentWebSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var proposal = AgentActionProposal.FromContext(
            envelope.ActionId,
            envelope.RunId,
            envelope.Actor,
            BuiltInAgentTools.WebSearch,
            context,
            CreateArgumentDigest(envelope.ActionId, request),
            new AgentApprovalPresentation(
                "Google search",
                "www.google.com",
                workingDirectory: null,
                [
                    new AgentApprovalArgument("query", request.Query),
                    new AgentApprovalArgument(
                        "result_count",
                        request.ResultCount.ToString(CultureInfo.InvariantCulture)),
                ]),
            envelope.PolicyGeneration,
            envelope.CreatedAtUtc,
            envelope.DeadlineUtc);
        return new AgentWebSearchAction(request, proposal);
    }

    public AgentActionExecutionBinding BindForExecution(
        AgentWebSearchAction action,
        AgentContextSnapshot freshContext)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(freshContext);
        ValidatePreparedAction(action);
        var proposal = action.Proposal;
        var targetIdentity = AgentTargetIdentity.Create(freshContext.Target);
        if (proposal.Target != freshContext.Target
            || proposal.TargetIdentity != targetIdentity)
        {
            throw new ArgumentException(
                "The fresh web search target does not match the original run target.",
                nameof(freshContext));
        }

        return new AgentActionExecutionBinding(
            proposal.Id,
            proposal.RunId,
            proposal.Actor.Id,
            BuiltInAgentTools.WebSearch,
            freshContext.Target,
            targetIdentity,
            freshContext.BindingFingerprint,
            proposal.ArgumentDigest,
            proposal.PolicyGeneration);
    }

    private static AgentActionDigest CreateArgumentDigest(
        AgentActionId actionId,
        AgentWebSearchRequest request)
    {
        var value = string.Join(
            '|',
            "ghostshell.agent-web-search-action",
            "1",
            actionId.Value,
            BuiltInAgentTools.WebSearch,
            request.ResultCount.ToString(CultureInfo.InvariantCulture),
            Convert.ToHexStringLower(Encoding.UTF8.GetBytes(request.Query)));
        return AgentActionDigest.FromUtf8(value);
    }

    private static void ValidatePreparedAction(AgentWebSearchAction action)
    {
        var digest = CreateArgumentDigest(action.Proposal.Id, action.Request);
        if (!string.Equals(
                action.Proposal.ToolName,
                BuiltInAgentTools.WebSearch,
                StringComparison.Ordinal)
            || action.Proposal.ArgumentDigest != digest)
        {
            throw new InvalidOperationException(
                "The prepared web search action no longer matches its typed request.");
        }
    }
}
