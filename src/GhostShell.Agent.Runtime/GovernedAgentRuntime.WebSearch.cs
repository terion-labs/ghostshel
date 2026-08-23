using System.Collections.Immutable;
using GhostShell.Agent;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private async ValueTask<AgentToolResult> ExecuteWebSearchProposalAsync(
        AgentToolProposal proposal,
        AgentToolDescriptor descriptor,
        AgentContextSnapshot context,
        CancellationToken cancellationToken)
    {
        if (_agentWebSearchHost is null || _webSearchComposer is null)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var parsed = WebSearchAgentToolParser.Parse(proposal);
        if (parsed is WebSearchAgentIntentResult.Rejected rejected)
        {
            return CreateRejectedResult(proposal, rejected.StableCode);
        }

        var intent = ((WebSearchAgentIntentResult.Parsed)parsed).Intent;
        AgentWebSearchAction action;
        try
        {
            var now = _timeProvider.GetUtcNow();
            action = _webSearchComposer.Prepare(
                new AgentActionEnvelope(
                    AgentActionId.New(),
                    GetRequiredSession().RunId,
                    GetOrCreateAgent(),
                    GetPolicyGeneration(),
                    now,
                    now + ActionLifetime),
                context,
                new AgentWebSearchRequest(intent.Query, intent.ResultCount));
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            _ = exception;
            return CreateRejectedResult(proposal, "tool_request_rejected");
        }

        var authorization = await _broker
            .RequestAsync(action.Proposal, cancellationToken)
            .ConfigureAwait(false);
        if (authorization is AgentAuthorizationResult.ApprovalRequired required)
        {
            authorization = await AwaitApprovalAsync(
                    required.Approval,
                    yieldsInput: false,
                    cancellationToken)
                .ConfigureAwait(false);
            descriptor = required.Approval.Tool;
        }

        if (authorization is AgentAuthorizationResult.Denied denied)
        {
            return CreateRejectedResult(
                proposal,
                StableCode(denied.Error.Code));
        }

        if (authorization
            is not AgentAuthorizationResult.Authorized authorizedResult)
        {
            return CreateRejectedResult(proposal, "approval_still_required");
        }

        var actionCancellation = BeginToolActivity(
            descriptor,
            action.Proposal.Presentation,
            cancellationToken);
        HostResult<AgentWebSearchResult> hostResult;
        try
        {
            try
            {
                hostResult = await _agentWebSearchHost
                    .RunAgentWebSearchAsync(
                        authorizedResult.Authorization.Id,
                        action,
                        actionCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                hostResult = CancelledWebSearch(context.Revision);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _ = exception;
                return CreateRejectedResult(proposal, "web_search_host_failed");
            }
        }
        finally
        {
            await EndToolActivityAsync(actionCancellation).ConfigureAwait(false);
        }

        if (hostResult is HostResult<AgentWebSearchResult>.Failure failure)
        {
            return CreateFailedResult(
                proposal,
                WebSearchAgentToolResultJson.ProviderStableCode(failure.Error),
                WebSearchAgentToolResultJson.Failure(failure.Error));
        }

        if (hostResult is not HostResult<AgentWebSearchResult>.Success success)
        {
            return CreateRejectedResult(proposal, "web_search_failed");
        }

        WebSearchAgentToolJsonProjection projection;
        try
        {
            projection = WebSearchAgentToolResultJson.Project(
                action.Request,
                success.Value);
        }
        catch (Exception exception)
            when (exception is
                ArgumentException
                or InvalidOperationException
                or OverflowException)
        {
            _ = exception;
            return CreateRejectedResult(proposal, "web_search_result_invalid");
        }

        return new AgentToolResult(
            proposal,
            projection.IsSuccess
                ? AgentToolResultStatus.Succeeded
                : AgentToolResultStatus.Failed,
            projection.StableCode,
            JsonValue(projection.Json));
    }

    private static HostResult<AgentWebSearchResult> CancelledWebSearch(
        long revision) =>
        HostResult<AgentWebSearchResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                "web_search_cancelled",
                "The web search was cancelled."),
            revision);

    private sealed class WebSearchToolContribution(
        GovernedAgentRuntime runtime) : IAgentToolContribution
    {
        public ImmutableArray<AgentToolDefinition> BuildTools(
            AgentToolBuildContext context) =>
            runtime._agentWebSearchHost is not null
                && runtime._webSearchComposer is not null
                    ? WebSearchAgentToolSet.Tools
                    : [];

        public ResolvedAgentToolContribution? Resolve(string toolName) =>
            string.Equals(
                toolName,
                BuiltInAgentTools.WebSearch,
                StringComparison.Ordinal)
                ? new ResolvedAgentToolContribution(toolName, ExecuteAsync)
                : null;

        private ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            if (!runtime.MatchesPinnedScope(request.Context))
            {
                return ValueTask.FromResult(
                    CreateRejectedResult(request.Proposal, "target_changed"));
            }

            return runtime.ExecuteWebSearchProposalAsync(
                request.Proposal,
                request.Descriptor,
                request.Context,
                cancellationToken);
        }
    }
}
