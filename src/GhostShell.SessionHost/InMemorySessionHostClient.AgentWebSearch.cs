using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    public async ValueTask<HostResult<AgentWebSearchResult>>
        RunAgentWebSearchAsync(
            AgentAuthorizationId authorizationId,
            AgentWebSearchAction action,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_agentWebSearchActionComposer is null
            || _agentWebSearchExecutor is null
            || _agentAuthorizationConsumer is null)
        {
            return Unsupported<AgentWebSearchResult>(
                "The governed web search bridge is not composed.",
                revision: 0);
        }

        AgentActionPermit? permit = null;
        HostResult<AgentWebSearchResult>? preDispatchFailure = null;
        long revision = 0;
        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<AgentWebSearchResult>(revision);
        }

        try
        {
            ThrowIfDisposed();
            var contextResult = ResolveAgentWebSearchContext(
                action.Proposal.Target);
            if (contextResult is HostResult<AgentContextSnapshot>.Failure failure)
            {
                return HostResult<AgentWebSearchResult>.Fail(
                    failure.Error,
                    failure.CurrentRevision);
            }

            var context =
                ((HostResult<AgentContextSnapshot>.Success)contextResult).Value;
            revision = context.Revision;
            AgentActionExecutionBinding binding;
            try
            {
                binding = _agentWebSearchActionComposer.BindForExecution(
                    action,
                    context);
            }
            catch (ArgumentException)
            {
                return InvalidWebSearchAction(
                    "The web search run target changed before authorization.",
                    revision);
            }
            catch (InvalidOperationException)
            {
                return InvalidWebSearchAction(
                    "The prepared web search no longer matches its typed query.",
                    revision);
            }

            var permitResult = await _agentAuthorizationConsumer
                .ConsumeAsync(
                    authorizationId,
                    binding,
                    cancellationToken)
                .ConfigureAwait(false);
            if (permitResult is AgentPermitResult.Denied denied)
            {
                return MapWebSearchAuthorizationFailure(
                    denied.Error,
                    revision);
            }

            permit = ((AgentPermitResult.Granted)permitResult).Permit;
            if (!HasWebSearchAuthorization(permit.Authorization)
                || !AuthorizationMatchesBinding(
                    permit.Authorization,
                    binding))
            {
                preDispatchFailure = InvalidWebSearchAction(
                    "The consumed authorization does not grant this web search.",
                    revision);
            }
            else if (permit.CancellationToken.IsCancellationRequested
                || cancellationToken.IsCancellationRequested)
            {
                preDispatchFailure = CancelledWebSearchAction(
                    permit,
                    cancellationToken,
                    revision);
            }
        }
        catch (OperationCanceledException) when (permit is null)
        {
            return Cancelled<AgentWebSearchResult>(revision);
        }
        catch (OperationCanceledException)
        {
            preDispatchFailure = CancelledWebSearchAction(
                permit!,
                cancellationToken,
                revision);
        }
        catch (ObjectDisposedException) when (permit is null)
        {
            return Cancelled<AgentWebSearchResult>(revision);
        }
        catch (ObjectDisposedException)
        {
            preDispatchFailure = CancelledWebSearchAction(
                permit!,
                cancellationToken,
                revision);
        }
        catch (Exception) when (permit is null)
        {
            return HostResult<AgentWebSearchResult>.Fail(
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The web search authorization broker is unavailable.",
                    retryable: true),
                revision);
        }
        catch (Exception)
        {
            preDispatchFailure = WebSearchEngineFailure(revision);
        }
        finally
        {
            _sessionGraphGate.Release();
        }

        if (preDispatchFailure is not null)
        {
            return await CompleteWebSearchAsync(permit!, preDispatchFailure)
                .ConfigureAwait(false);
        }

        HostResult<AgentWebSearchResult> result;
        try
        {
            using var operationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    permit!.CancellationToken,
                    cancellationToken);
            var executed = await _agentWebSearchExecutor
                .SearchAsync(action.Request, operationCancellation.Token)
                .ConfigureAwait(false);
            result = executed switch
            {
                AgentWebSearchExecutionResult.Succeeded succeeded
                    when !operationCancellation.IsCancellationRequested =>
                    HostResult<AgentWebSearchResult>.Succeed(
                        succeeded.Result,
                        revision),
                AgentWebSearchExecutionResult.Succeeded =>
                    CancelledWebSearchAction(
                        permit,
                        cancellationToken,
                        revision),
                AgentWebSearchExecutionResult.Failed failed =>
                    MapWebSearchExecutionFailure(failed.Code, revision),
                _ => WebSearchEngineFailure(revision),
            };
        }
        catch (OperationCanceledException)
        {
            result = CancelledWebSearchAction(
                permit!,
                cancellationToken,
                revision);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            result = WebSearchEngineFailure(revision);
        }

        return await CompleteWebSearchAsync(permit!, result)
            .ConfigureAwait(false);
    }

    private HostResult<AgentContextSnapshot> ResolveAgentWebSearchContext(
        AgentTarget target)
    {
        HostedSession[] hostedSessions;
        lock (_gate)
        {
            hostedSessions = [.. _sessions.Values];
        }

        var sessions = hostedSessions
            .Select(session => session.Snapshot().Descriptor)
            .ToDictionary(session => session.Id);
        return ResolveAgentContext(
            new AgentContextRequest(
                target,
                AgentTarget.SelectedPanels.MaximumPanelCount),
            sessions);
    }

    private static bool HasWebSearchAuthorization(
        AgentActionAuthorization authorization) =>
        string.Equals(
            authorization.ToolName,
            BuiltInAgentTools.WebSearch,
            StringComparison.Ordinal)
        && BuiltInAgentTools.Catalog.TryGet(
            BuiltInAgentTools.WebSearch,
            out var descriptor)
        && descriptor!.Capability == AgentCapability.WebFetch
        && descriptor.Risk == AgentActionRisk.Observation;

    private async ValueTask<HostResult<AgentWebSearchResult>>
        CompleteWebSearchAsync(
            AgentActionPermit permit,
            HostResult<AgentWebSearchResult> result)
    {
        var (outcome, stableCode, count) = result switch
        {
            HostResult<AgentWebSearchResult>.Failure failure
                when failure.Error.Code == HostErrorCode.Cancelled =>
                (AgentActionOutcome.Cancelled, failure.Error.StableCode, (int?)null),
            HostResult<AgentWebSearchResult>.Failure failure =>
                (AgentActionOutcome.Failed, failure.Error.StableCode, (int?)null),
            HostResult<AgentWebSearchResult>.Success success =>
                (AgentActionOutcome.Succeeded, "web_search_completed", success.Value.Links.Count),
            _ => throw new InvalidOperationException(
                "A governed web search returned an unknown result."),
        };
        return await CompleteConsumedAgentActionAsync(
                permit,
                Completion(permit, outcome, stableCode, count),
                result,
                WebSearchResultRevision(result))
            .ConfigureAwait(false);
    }

    private static HostResult<AgentWebSearchResult> MapWebSearchExecutionFailure(
        AgentWebSearchErrorCode code,
        long revision)
    {
        var mapped = code switch
        {
            AgentWebSearchErrorCode.NavigationDenied =>
                (HostErrorCode.InvalidRequest, "web_search_navigation_denied", false),
            AgentWebSearchErrorCode.LoadFailed =>
                (HostErrorCode.EngineFailed, "web_search_load_failed", true),
            AgentWebSearchErrorCode.Interstitial =>
                (HostErrorCode.EngineFailed, "web_search_interstitial", false),
            AgentWebSearchErrorCode.ExtractionFailed =>
                (HostErrorCode.EngineFailed, "web_search_extraction_failed", true),
            AgentWebSearchErrorCode.TimedOut =>
                (HostErrorCode.DeadlineExceeded, "web_search_timed_out", true),
            AgentWebSearchErrorCode.Cancelled =>
                (HostErrorCode.Cancelled, "web_search_cancelled", false),
            _ =>
                (HostErrorCode.EngineFailed, "web_search_unavailable", true),
        };
        return HostResult<AgentWebSearchResult>.Fail(
            new HostError(
                mapped.Item1,
                mapped.Item2,
                "The offscreen browser could not complete the web search.",
                mapped.Item3),
            revision);
    }

    private static HostResult<AgentWebSearchResult>
        MapWebSearchAuthorizationFailure(
            AgentAuthorizationError error,
            long revision)
    {
        var hostError = error.Code switch
        {
            AgentAuthorizationErrorCode.AuthorizationExpired
                or AgentAuthorizationErrorCode.ApprovalExpired =>
                new HostError(
                    HostErrorCode.DeadlineExceeded,
                    "web_search_timed_out",
                    "The one-action web search authorization expired."),
            AgentAuthorizationErrorCode.Cancelled
                or AgentAuthorizationErrorCode.RunCancelled =>
                new HostError(
                    HostErrorCode.Cancelled,
                    "web_search_cancelled",
                    "The governed web search was cancelled."),
            AgentAuthorizationErrorCode.AuditUnavailable =>
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The web search audit trail is unavailable.",
                    retryable: true),
            _ => HostError.Create(
                HostErrorCode.InvalidRequest,
                "The exact one-action web search authorization was rejected."),
        };
        return HostResult<AgentWebSearchResult>.Fail(hostError, revision);
    }

    private static HostResult<AgentWebSearchResult> CancelledWebSearchAction(
        AgentActionPermit permit,
        CancellationToken callerCancellation,
        long revision)
    {
        var stableCode = permit.CancellationToken.IsCancellationRequested
            ? "authority_revoked"
            : callerCancellation.IsCancellationRequested
                ? "web_search_cancelled"
                : "operation_cancelled";
        return HostResult<AgentWebSearchResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                stableCode,
                "The governed web search was cancelled."),
            revision);
    }

    private static HostResult<AgentWebSearchResult> InvalidWebSearchAction(
        string message,
        long revision) =>
        HostResult<AgentWebSearchResult>.Fail(
            HostError.Create(HostErrorCode.InvalidRequest, message),
            revision);

    private static HostResult<AgentWebSearchResult> WebSearchEngineFailure(
        long revision) =>
        HostResult<AgentWebSearchResult>.Fail(
            new HostError(
                HostErrorCode.EngineFailed,
                "web_search_unavailable",
                "The governed web search engine is unavailable.",
                Retryable: true),
            revision);

    private static long WebSearchResultRevision(
        HostResult<AgentWebSearchResult> result) =>
        result switch
        {
            HostResult<AgentWebSearchResult>.Success success =>
                success.ResultingRevision,
            HostResult<AgentWebSearchResult>.Failure failure =>
                failure.CurrentRevision,
            _ => throw new InvalidOperationException(
                "A governed web search returned an unknown result."),
        };
}
