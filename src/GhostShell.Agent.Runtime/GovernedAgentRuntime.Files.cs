using GhostShell.Agent;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private async ValueTask<AgentToolResult> ExecuteFileProposalAsync(
        AgentToolProposal proposal,
        AgentToolDescriptor descriptor,
        AgentContextSnapshot context,
        IReadOnlySet<PanelInstanceId> resizeEligiblePanelIds,
        IReadOnlySet<PanelInstanceId> browserEligiblePanelIds,
        IReadOnlyDictionary<PanelInstanceId, FileSessionMetadata> fileMetadata,
        CancellationToken cancellationToken)
    {
        if (_agentFileHost is null || _fileComposer is null)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var eligibleFiles = context.Panels
            .Where(panel =>
                panel.Kind == PanelKind.FileViewer
                && fileMetadata.ContainsKey(panel.PanelId))
            .ToArray();
        if (eligibleFiles.Length == 0)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var exactTarget = context.Target
            is AgentTarget.Panel or AgentTarget.ConnectionSession;
        var parsed = exactTarget
            ? FileAgentToolParser.Parse(
                proposal,
                eligibleFiles.Single(),
                fileMetadata[eligibleFiles[0].PanelId])
            : FileAgentToolParser.Parse(
                proposal,
                eligibleFiles,
                fileMetadata);
        if (parsed is FileAgentIntentResult.Rejected rejected)
        {
            return CreateRejectedResult(proposal, rejected.StableCode);
        }

        var selected = (FileAgentIntentResult.Parsed)parsed;
        var resultPanelId = exactTarget
            ? null
            : selected.PanelId;
        var isMutation = selected.Intent is
            FileAgentIntent.CreateDirectory
            or FileAgentIntent.Delete;
        var panel = context.Panels.SingleOrDefault(
            candidate => candidate.PanelId == selected.PanelId);
        if (panel?.SessionId is not { } sessionId
            || panel.Kind != PanelKind.FileViewer
            || !fileMetadata.TryGetValue(
                panel.PanelId,
                out var panelFileMetadata))
        {
            return CreateRejectedResult(
                proposal,
                "target_changed",
                resultPanelId);
        }

        UpdateTargetPresentation(
            context,
            resizeEligiblePanelIds,
            browserEligiblePanelIds,
            fileMetadata);

        AgentFileAction action;
        try
        {
            var now = _timeProvider.GetUtcNow();
            var envelope = new AgentActionEnvelope(
                AgentActionId.New(),
                GetRequiredSession().RunId,
                GetOrCreateAgent(),
                GetPolicyGeneration(),
                now,
                now + ActionLifetime);
            action = _fileComposer.Prepare(
                envelope,
                context,
                CreateFileRequest(selected.Intent, sessionId));
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            return CreateRejectedResult(
                proposal,
                "tool_request_rejected",
                resultPanelId);
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
                StableCode(denied.Error.Code),
                resultPanelId);
        }

        if (authorization
            is not AgentAuthorizationResult.Authorized authorizedResult)
        {
            return CreateRejectedResult(
                proposal,
                "approval_still_required",
                resultPanelId);
        }

        var actionCancellation = BeginToolActivity(
            descriptor,
            action.Proposal.Presentation,
            cancellationToken);
        HostResult<AgentFileActionResult> hostResult;
        try
        {
            try
            {
                hostResult = await _agentFileHost
                    .RunAgentFileActionAsync(
                        authorizedResult.Authorization.Id,
                        action,
                        actionCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (isMutation)
            {
                hostResult = FileMutationOutcomeUnknown(context.Revision);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
                when (actionCancellation.Token.IsCancellationRequested)
            {
                hostResult = HostResult<AgentFileActionResult>.Fail(
                    new HostError(
                        HostErrorCode.Cancelled,
                        "caller_cancelled",
                        "The file action was cancelled."),
                    context.Revision);
            }
            catch (Exception exception)
                when (isMutation && exception is not OutOfMemoryException)
            {
                _ = exception;
                hostResult = FileMutationOutcomeUnknown(context.Revision);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _ = exception;
                return CreateRejectedResult(
                    proposal,
                    "file_provider_failed",
                    resultPanelId);
            }
        }
        finally
        {
            await EndToolActivityAsync(actionCancellation)
                .ConfigureAwait(false);
        }

        hostResult = NormalizeRequestedFileActionCancellation(
            hostResult,
            actionCancellation.CancellationRequested
                && !cancellationToken.IsCancellationRequested);
        if (hostResult is HostResult<AgentFileActionResult>.Success)
        {
            await RefreshTargetPresentationBestEffortAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (hostResult is HostResult<AgentFileActionResult>.Failure failure)
        {
            return CreateFailedResult(
                proposal,
                FileAgentToolResultJson.ProviderStableCode(failure.Error),
                FileAgentToolResultJson.Failure(
                    failure.Error,
                    resultPanelId));
        }

        if (hostResult is not HostResult<AgentFileActionResult>.Success success)
        {
            return CreateRejectedResult(
                proposal,
                "file_provider_failed",
                resultPanelId);
        }

        FileAgentToolJsonProjection projection;
        try
        {
            projection = FileAgentToolResultJson.Project(
                success.Value,
                selected.Intent,
                panelFileMetadata,
                resultPanelId);
        }
        catch (Exception exception)
            when (exception is
                ArgumentException
                or InvalidOperationException
                or OverflowException)
        {
            _ = exception;
            return CreateRejectedResult(
                proposal,
                "file_result_invalid",
                resultPanelId);
        }

        return new AgentToolResult(
            proposal,
            projection.IsSuccess
                ? AgentToolResultStatus.Succeeded
                : AgentToolResultStatus.Failed,
            projection.StableCode,
            JsonValue(projection.Json));
    }

    private static HostResult<AgentFileActionResult>
        NormalizeRequestedFileActionCancellation(
            HostResult<AgentFileActionResult> result,
            bool cancellationRequested)
    {
        if (!cancellationRequested
            || result is not HostResult<AgentFileActionResult>.Failure
            {
                Error:
                {
                    Code: HostErrorCode.Cancelled,
                    StableCode: "cancelled" or "operation_cancelled",
                },
            } failure)
        {
            return result;
        }

        return HostResult<AgentFileActionResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                "caller_cancelled",
                "The file action was cancelled."),
            failure.CurrentRevision);
    }

    private static AgentFileRequest CreateFileRequest(
        FileAgentIntent intent,
        SessionId sessionId) =>
        intent switch
        {
            FileAgentIntent.List list =>
                new AgentFileRequest.List(
                    sessionId,
                    list.RelativePath),
            FileAgentIntent.Stat stat =>
                new AgentFileRequest.Stat(
                    sessionId,
                    stat.RelativePath),
            FileAgentIntent.Read read =>
                new AgentFileRequest.Read(
                    sessionId,
                    read.RelativePath),
            FileAgentIntent.CreateDirectory createDirectory =>
                new AgentFileRequest.CreateDirectory(
                    sessionId,
                    createDirectory.RelativePath),
            FileAgentIntent.Delete delete =>
                new AgentFileRequest.Delete(
                    sessionId,
                    delete.RelativePath),
            _ => throw new ArgumentOutOfRangeException(
                nameof(intent),
                intent.GetType(),
                "The file intent is unsupported."),
        };

    private static bool IsFileTool(string toolName) =>
        toolName is
            BuiltInAgentTools.FilesList
            or BuiltInAgentTools.FilesStat
            or BuiltInAgentTools.FilesRead
            or BuiltInAgentTools.FilesCreateDirectory
            or BuiltInAgentTools.FilesDelete;

    private static HostResult<AgentFileActionResult>
        FileMutationOutcomeUnknown(long revision) =>
        HostResult<AgentFileActionResult>.Fail(
            new HostError(
                HostErrorCode.EngineFailed,
                FileAgentToolResultJson.FileMutationOutcomeUnknownStableCode,
                "The File Viewer mutation outcome is unknown.",
                Retryable: false),
            revision);
}
