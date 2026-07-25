using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    private const string InvalidAgentFileResultCode = "file_result_invalid";
    private const string NonTextAgentFileResultCode = "file_preview_not_text";
    private const string SensitiveAgentFileResultCode = "file_content_sensitive";
    private const string FileMutationOutcomeUnknownCode =
        "file_mutation_outcome_unknown";

    private static readonly UTF8Encoding StrictAgentFileUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public async ValueTask<HostResult<AgentFileActionResult>>
        RunAgentFileActionAsync(
            AgentAuthorizationId authorizationId,
            AgentFileAction action,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_agentFileActionComposer is null
            || _agentAuthorizationConsumer is null)
        {
            return Unsupported<AgentFileActionResult>(
                "The governed File Viewer execution bridge is not composed.",
                revision: 0);
        }

        AgentFileDispatch? dispatch = null;
        AgentActionPermit? permit = null;
        HostResult<AgentFileActionResult>? preDispatchFailure = null;
        long revision = 0;
        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<AgentFileActionResult>(revision);
        }

        try
        {
            ThrowIfDisposed();
            var sessionId = GetAgentFileRequestSessionId(action.Request);
            var exactContextResult = ResolveExactAgentContext(
                action.Proposal.Target);
            if (exactContextResult
                is HostResult<AgentContextSnapshot>.Failure contextFailure)
            {
                return HostResult<AgentFileActionResult>.Fail(
                    contextFailure.Error,
                    contextFailure.CurrentRevision);
            }

            var exactContext =
                ((HostResult<AgentContextSnapshot>.Success)exactContextResult).Value;
            revision = exactContext.Revision;
            var exactSessionPanel = exactContext.Panels
                .SingleOrDefault(panel => panel.SessionId == sessionId);
            if (exactSessionPanel?.SessionRevision
                is not long expectedSessionRevision)
            {
                return InvalidAgentFileAction(
                    "The exact File Viewer context has no matching live session revision.",
                    revision);
            }

            if (!TryGetSession(sessionId, out var session))
            {
                return NotFound<AgentFileActionResult>("session", revision);
            }

            AgentActionExecutionBinding binding;
            try
            {
                binding = _agentFileActionComposer.BindForExecution(
                    action,
                    exactContext);
                dispatch = CaptureAgentFileDispatch(
                    action.Request,
                    session,
                    revision,
                    expectedSessionRevision);
            }
            catch (AgentFileDispatchException exception)
            {
                return HostResult<AgentFileActionResult>.Fail(
                    exception.Error,
                    revision);
            }
            catch (ArgumentException)
            {
                return InvalidAgentFileAction(
                    "The prepared action no longer matches the exact live File Viewer target.",
                    revision);
            }
            catch (InvalidOperationException)
            {
                return InvalidAgentFileAction(
                    "The prepared action no longer matches its typed file request.",
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
                return MapAgentFileAuthorizationFailure(
                    denied.Error,
                    revision);
            }

            permit = ((AgentPermitResult.Granted)permitResult).Permit;
            preDispatchFailure = RevalidateAgentFileDispatch(
                action,
                dispatch,
                permit,
                binding,
                revision);
        }
        catch (AgentFileDispatchException exception) when (permit is null)
        {
            return HostResult<AgentFileActionResult>.Fail(
                exception.Error,
                revision);
        }
        catch (AgentFileDispatchException exception)
        {
            preDispatchFailure = HostResult<AgentFileActionResult>.Fail(
                exception.Error,
                revision);
        }
        catch (OperationCanceledException) when (permit is null)
        {
            return Cancelled<AgentFileActionResult>(revision);
        }
        catch (OperationCanceledException)
        {
            preDispatchFailure = Cancelled<AgentFileActionResult>(revision);
        }
        catch (ObjectDisposedException) when (permit is null)
        {
            return Cancelled<AgentFileActionResult>(revision);
        }
        catch (ObjectDisposedException)
        {
            preDispatchFailure = Cancelled<AgentFileActionResult>(revision);
        }
        catch (Exception) when (permit is null)
        {
            return HostResult<AgentFileActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The File Viewer authorization broker is unavailable.",
                    retryable: true),
                revision);
        }
        catch (Exception)
        {
            preDispatchFailure = HostResult<AgentFileActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The governed file action could not be prepared.",
                    retryable: true),
                revision);
        }
        finally
        {
            _sessionGraphGate.Release();
        }

        if (preDispatchFailure is not null)
        {
            return await CompleteAgentFilePreDispatchFailureAsync(
                    dispatch!,
                    permit!,
                    preDispatchFailure,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await DispatchAndCompleteAgentFileActionAsync(
                dispatch!,
                permit!,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private HostResult<AgentFileActionResult>? RevalidateAgentFileDispatch(
        AgentFileAction action,
        AgentFileDispatch dispatch,
        AgentActionPermit permit,
        AgentActionExecutionBinding consumedBinding,
        long revision)
    {
        if (!HasAgentFileAuthorization(
                permit.Authorization,
                dispatch.Request))
        {
            return InvalidAgentFileAction(
                "The consumed authorization does not grant the exact governed file tool.",
                revision);
        }

        if (IsAgentFileMutation(dispatch.Request)
            && permit.Authorization.Source is not (
                AgentAuthorizationSource.HumanApproval
                or AgentAuthorizationSource.YoloPolicy))
        {
            return InvalidAgentFileAction(
                "Governed file mutations require explicit human approval or run-local YOLO.",
                revision);
        }

        var contextResult = ResolveExactAgentContext(action.Proposal.Target);
        if (contextResult is HostResult<AgentContextSnapshot>.Failure failure)
        {
            return HostResult<AgentFileActionResult>.Fail(
                failure.Error,
                failure.CurrentRevision);
        }

        var currentContext =
            ((HostResult<AgentContextSnapshot>.Success)contextResult).Value;
        AgentActionExecutionBinding currentBinding;
        try
        {
            currentBinding = _agentFileActionComposer!.BindForExecution(
                action,
                currentContext);
        }
        catch (ArgumentException)
        {
            return InvalidAgentFileAction(
                "The exact File Viewer target changed while authorization was consumed.",
                revision);
        }
        catch (InvalidOperationException)
        {
            return InvalidAgentFileAction(
                "The prepared file request changed while authorization was consumed.",
                revision);
        }

        if (!AgentFileBindingsMatch(consumedBinding, currentBinding)
            || !AuthorizationMatchesBinding(
                permit.Authorization,
                currentBinding))
        {
            return InvalidAgentFileAction(
                "The exact File Viewer execution binding changed before dispatch.",
                revision);
        }

        var currentPanel = currentContext.Panels.SingleOrDefault(
            panel => panel.SessionId == dispatch.Session.Id);
        if (currentPanel?.SessionRevision != dispatch.ExpectedSessionRevision)
        {
            return InvalidAgentFileAction(
                "The exact File Viewer session changed before dispatch.",
                revision);
        }

        if (!HasLiveAgentFileCapability(dispatch))
        {
            return HostResult<AgentFileActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.CapabilityNotSupported,
                    "The File Viewer no longer supports the governed operation."),
                revision);
        }

        if (!dispatch.Session.CanExecuteAgentFileAction(
                dispatch.Files,
                dispatch.Metadata,
                dispatch.ExpectedSessionRevision,
                dispatch.RequiredSessionCapability,
                dispatch.RequiredProviderCapability,
                dispatch.RuntimeCancellation))
        {
            return InvalidAgentFileAction(
                "The exact File Viewer scope or capability changed before dispatch.",
                revision);
        }

        return null;
    }

    private static AgentFileDispatch CaptureAgentFileDispatch(
        AgentFileRequest request,
        HostedSession session,
        long revision,
        long expectedSessionRevision)
    {
        var snapshot = session.Snapshot().Descriptor;
        if (snapshot.Lifecycle != SessionLifecycle.Active)
        {
            throw AgentFileDispatchFailure(
                HostErrorCode.SessionClosed,
                "The exact File Viewer session is no longer active.");
        }

        if (session.Engine is not IFilePanelSession files
            || session.Engine.Kind != PanelKind.FileViewer)
        {
            throw AgentFileDispatchFailure(
                HostErrorCode.CapabilityNotSupported,
                "The exact session is not a File Viewer.");
        }

        var metadata = snapshot.FileMetadata
            ?? throw AgentFileDispatchFailure(
                HostErrorCode.InvalidRequest,
                "The exact File Viewer session has no trusted file scope.");
        if (files.Metadata != metadata)
        {
            throw AgentFileDispatchFailure(
                HostErrorCode.InvalidRequest,
                "The File Viewer trusted scope no longer matches its hosted session.");
        }

        var requiredSessionCapability =
            RequiredAgentFileSessionCapability(request);
        var requiredProviderCapability =
            RequiredAgentFileProviderCapability(request);
        if (!files.Capabilities.Contains(requiredSessionCapability)
            || !metadata.Capabilities.HasFlag(requiredProviderCapability))
        {
            throw AgentFileDispatchFailure(
                HostErrorCode.CapabilityNotSupported,
                "The File Viewer no longer supports the governed operation.");
        }

        FilePanelLocation location;
        try
        {
            location = AgentFileActionComposer.ResolveLocation(
                metadata,
                GetAgentFileRelativePath(request));
        }
        catch (ArgumentException)
        {
            throw AgentFileDispatchFailure(
                HostErrorCode.InvalidRequest,
                "The governed relative file path or trusted root is invalid.");
        }

        var maximumPageSize =
            AgentFileActionComposer.GetEffectiveListPageSize(metadata);
        var maximumPreviewBytes =
            AgentFileActionComposer.GetEffectiveReadMaximumBytes(metadata);
        return new AgentFileDispatch(
            request,
            session,
            files,
            metadata,
            location,
            requiredSessionCapability,
            requiredProviderCapability,
            maximumPageSize,
            maximumPreviewBytes,
            session.CaptureRuntimeAuthority(),
            expectedSessionRevision,
            revision);
    }

    private async ValueTask<HostResult<AgentFileActionResult>>
        CompleteAgentFilePreDispatchFailureAsync(
            AgentFileDispatch dispatch,
            AgentActionPermit permit,
            HostResult<AgentFileActionResult> failure,
            CancellationToken callerCancellation)
    {
        var hostFailure = (HostResult<AgentFileActionResult>.Failure)failure;
        var cancelled = permit.CancellationToken.IsCancellationRequested
            || dispatch.RuntimeCancellation.IsCancellationRequested
            || hostFailure.Error.Code == HostErrorCode.Cancelled;
        var completion = Completion(
            permit,
            cancelled
                ? AgentActionOutcome.Cancelled
                : AgentActionOutcome.Failed,
            cancelled
                ? AgentFileCancellationCode(
                    dispatch,
                    permit,
                    callerCancellation)
                : hostFailure.Error.StableCode);
        var normalizedFailure = NormalizeAgentFileCancellationResult(
            failure,
            completion,
            dispatch.Revision);
        return await CompleteConsumedAgentActionAsync(
                permit,
                completion,
                normalizedFailure,
                dispatch.Revision)
            .ConfigureAwait(false);
    }

    private async ValueTask<HostResult<AgentFileActionResult>>
        DispatchAndCompleteAgentFileActionAsync(
            AgentFileDispatch dispatch,
            AgentActionPermit permit,
            CancellationToken callerCancellation)
    {
        using var executionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellation,
                permit.CancellationToken,
                dispatch.RuntimeCancellation);
        HostResult<AgentFileActionResult> result;
        if (executionCancellation.IsCancellationRequested)
        {
            result = Cancelled<AgentFileActionResult>(dispatch.Revision);
        }
        else if (!HasLiveAgentFileCapability(dispatch))
        {
            result = HostResult<AgentFileActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.CapabilityNotSupported,
                    "The File Viewer no longer supports the governed operation."),
                dispatch.Revision);
        }
        else if (!dispatch.Session.CanExecuteAgentFileAction(
                     dispatch.Files,
                     dispatch.Metadata,
                     dispatch.ExpectedSessionRevision,
                     dispatch.RequiredSessionCapability,
                     dispatch.RequiredProviderCapability,
                     dispatch.RuntimeCancellation))
        {
            result = InvalidAgentFileAction(
                "The exact File Viewer scope or capability changed before dispatch.",
                dispatch.Revision);
        }
        else
        {
            result = await DispatchAgentFileActionAsync(
                    dispatch,
                    executionCancellation.Token)
                .ConfigureAwait(false);
        }

        var completion = CreateAgentFileCompletion(
            result,
            dispatch,
            permit,
            callerCancellation);
        var normalizedResult = NormalizeAgentFileCancellationResult(
            result,
            completion,
            dispatch.Revision);
        return await CompleteConsumedAgentActionAsync(
                permit,
                completion,
                normalizedResult,
                dispatch.Revision)
            .ConfigureAwait(false);
    }

    private static async ValueTask<HostResult<AgentFileActionResult>>
        DispatchAgentFileActionAsync(
            AgentFileDispatch dispatch,
            CancellationToken cancellationToken)
    {
        try
        {
            HostResult<AgentFileActionResult> result =
                dispatch.Request switch
                {
                    AgentFileRequest.List =>
                        await ListAgentFilesAsync(
                                dispatch,
                                cancellationToken)
                            .ConfigureAwait(false),
                    AgentFileRequest.Stat =>
                        await StatAgentFileAsync(
                                dispatch,
                                cancellationToken)
                            .ConfigureAwait(false),
                    AgentFileRequest.Read =>
                        await ReadAgentFileAsync(
                                dispatch,
                                cancellationToken)
                            .ConfigureAwait(false),
                    AgentFileRequest.CreateDirectory =>
                        await CreateAgentDirectoryAsync(
                                dispatch,
                                cancellationToken)
                            .ConfigureAwait(false),
                    AgentFileRequest.Delete =>
                        await DeleteAgentFileAsync(
                                dispatch,
                                cancellationToken)
                            .ConfigureAwait(false),
                    _ => InvalidAgentFileAction(
                        "The governed file request kind is unsupported.",
                        dispatch.Revision),
                };

            if (IsAgentFileMutation(dispatch.Request))
            {
                return result;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Cancelled<AgentFileActionResult>(dispatch.Revision);
            }

            if (result is HostResult<AgentFileActionResult>.Success
                && !dispatch.Session.CanExecuteAgentFileAction(
                    dispatch.Files,
                    dispatch.Metadata,
                    dispatch.ExpectedSessionRevision,
                    dispatch.RequiredSessionCapability,
                    dispatch.RequiredProviderCapability,
                    dispatch.RuntimeCancellation))
            {
                return InvalidAgentFileAction(
                    "The exact File Viewer scope changed while the operation was running.",
                    dispatch.Revision);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return IsAgentFileMutation(dispatch.Request)
                ? FileMutationOutcomeUnknown(dispatch.Revision)
                : Cancelled<AgentFileActionResult>(dispatch.Revision);
        }
        catch (Exception)
        {
            return IsAgentFileMutation(dispatch.Request)
                ? FileMutationOutcomeUnknown(dispatch.Revision)
                : HostResult<AgentFileActionResult>.Fail(
                    HostError.Create(
                        HostErrorCode.EngineFailed,
                        "The File Viewer engine could not complete the governed action."),
                    dispatch.Revision);
        }
    }

    private static async ValueTask<HostResult<AgentFileActionResult>>
        ListAgentFilesAsync(
            AgentFileDispatch dispatch,
            CancellationToken cancellationToken)
    {
        var providerResult = await dispatch.Files
            .ListAsync(
                new FilePanelListRequest(
                    dispatch.Location,
                    dispatch.MaximumPageSize,
                    ContinuationToken: null,
                    ShowHidden: false),
                cancellationToken)
            .ConfigureAwait(false);
        if (!providerResult.IsSuccess)
        {
            return MapAgentFileProviderFailure(
                providerResult.Error!,
                dispatch.Revision);
        }

        var page = providerResult.Value!;
        if (page.Entries.Length > dispatch.MaximumPageSize)
        {
            return InvalidAgentFileProviderResult(dispatch.Revision);
        }

        var entries = new List<FilePanelEntry>(page.Entries.Length);
        foreach (var entry in page.Entries)
        {
            if (!TryNormalizeListedEntry(
                    entry,
                    dispatch.Location,
                    dispatch.Metadata.TrustedRoot,
                    out var normalized))
            {
                return InvalidAgentFileProviderResult(dispatch.Revision);
            }

            entries.Add(normalized!);
        }

        return HostResult<AgentFileActionResult>.Succeed(
            new AgentFileActionResult.Page(
                new FilePanelPage(entries, continuationToken: null)),
            dispatch.Session.Snapshot().Descriptor.Revision);
    }

    private static async ValueTask<HostResult<AgentFileActionResult>>
        StatAgentFileAsync(
            AgentFileDispatch dispatch,
            CancellationToken cancellationToken)
    {
        var providerResult = await dispatch.Files
            .StatAsync(dispatch.Location, cancellationToken)
            .ConfigureAwait(false);
        if (!providerResult.IsSuccess)
        {
            return MapAgentFileProviderFailure(
                providerResult.Error!,
                dispatch.Revision);
        }

        if (!TryNormalizeExactEntry(
                providerResult.Value!,
                dispatch.Location,
                dispatch.Metadata.TrustedRoot,
                out var normalized))
        {
            return InvalidAgentFileProviderResult(dispatch.Revision);
        }

        return HostResult<AgentFileActionResult>.Succeed(
            new AgentFileActionResult.Entry(normalized!),
            dispatch.Session.Snapshot().Descriptor.Revision);
    }

    private static async ValueTask<HostResult<AgentFileActionResult>>
        ReadAgentFileAsync(
            AgentFileDispatch dispatch,
            CancellationToken cancellationToken)
    {
        var providerResult = await dispatch.Files
            .PreviewAsync(
                new FilePanelPreviewRequest(
                    dispatch.Location,
                    dispatch.MaximumPreviewBytes),
                cancellationToken)
            .ConfigureAwait(false);
        if (!providerResult.IsSuccess)
        {
            return MapAgentFileProviderFailure(
                providerResult.Error!,
                dispatch.Revision);
        }

        var preview = providerResult.Value!;
        if (preview.Kind is not (
                FilePanelPreviewKind.Text
                or FilePanelPreviewKind.StructuredText))
        {
            return HostResult<AgentFileActionResult>.Fail(
                new HostError(
                    HostErrorCode.CapabilityNotSupported,
                    NonTextAgentFileResultCode,
                    "The governed file read accepts text previews only."),
                dispatch.Revision);
        }

        if (preview.Content.Length > dispatch.MaximumPreviewBytes
            || !LocationsMatchIgnoringVersion(
                preview.Location,
                dispatch.Location)
            || !IsAtOrBelowTrustedRoot(
                preview.Location,
                dispatch.Metadata.TrustedRoot)
            || !TryNormalizeMediaType(
                preview.MediaType,
                out var mediaType))
        {
            return InvalidAgentFileProviderResult(dispatch.Revision);
        }

        string text;
        try
        {
            text = StrictAgentFileUtf8.GetString(preview.Content.Span);
        }
        catch (DecoderFallbackException)
        {
            return InvalidAgentFileProviderResult(dispatch.Revision);
        }

        if (AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(text))
        {
            return HostResult<AgentFileActionResult>.Fail(
                new HostError(
                    HostErrorCode.InvalidRequest,
                    SensitiveAgentFileResultCode,
                    "The governed file read withheld credential-shaped content."),
                dispatch.Revision);
        }

        var normalized = new FilePanelPreview(
            dispatch.Location,
            preview.Kind,
            mediaType!,
            preview.Content.Span,
            preview.IsTruncated);
        return HostResult<AgentFileActionResult>.Succeed(
            new AgentFileActionResult.Preview(normalized),
            dispatch.Session.Snapshot().Descriptor.Revision);
    }

    private static async ValueTask<HostResult<AgentFileActionResult>>
        CreateAgentDirectoryAsync(
            AgentFileDispatch dispatch,
            CancellationToken cancellationToken)
    {
        var providerResult = await dispatch.Files
            .CreateDirectoryAsync(
                new FilePanelCreateDirectoryRequest(
                    dispatch.Location,
                    FilePanelMutationPrecondition.MustNotExist),
                cancellationToken)
            .ConfigureAwait(false);
        if (!providerResult.IsSuccess)
        {
            return FileMutationOutcomeUnknown(dispatch.Revision);
        }

        if (!TryNormalizeExactEntry(
                providerResult.Value!,
                dispatch.Location,
                dispatch.Metadata.TrustedRoot,
                out var normalized)
            || normalized!.Kind != FilePanelEntryKind.Directory)
        {
            return FileMutationOutcomeUnknown(dispatch.Revision);
        }

        return HostResult<AgentFileActionResult>.Succeed(
            new AgentFileActionResult.CreatedDirectory(normalized),
            dispatch.Revision);
    }

    private static async ValueTask<HostResult<AgentFileActionResult>>
        DeleteAgentFileAsync(
            AgentFileDispatch dispatch,
            CancellationToken cancellationToken)
    {
        var providerResult = await dispatch.Files
            .DeleteAsync(
                new FilePanelDeleteRequest(
                    dispatch.Location,
                    Recursive: false,
                    FilePanelMutationPrecondition.MustExist),
                cancellationToken)
            .ConfigureAwait(false);
        if (!providerResult.IsSuccess)
        {
            return FileMutationOutcomeUnknown(dispatch.Revision);
        }

        var receipt = providerResult.Value!;
        if (!LocationsMatchIgnoringVersion(
                receipt.DeletedLocation,
                dispatch.Location)
            || !IsAtOrBelowTrustedRoot(
                receipt.DeletedLocation,
                dispatch.Metadata.TrustedRoot))
        {
            return FileMutationOutcomeUnknown(dispatch.Revision);
        }

        return HostResult<AgentFileActionResult>.Succeed(
            new AgentFileActionResult.Deleted(
                new FilePanelDeleteReceipt(
                    dispatch.Location.WithVersion(version: null),
                    WasDirectory: false)),
            dispatch.Revision);
    }

    private AgentActionCompletion CreateAgentFileCompletion(
        HostResult<AgentFileActionResult> result,
        AgentFileDispatch dispatch,
        AgentActionPermit permit,
        CancellationToken callerCancellation)
    {
        var (outcome, stableCode) = result switch
        {
            HostResult<AgentFileActionResult>.Failure failure
                when failure.Error.Code == HostErrorCode.Cancelled =>
                (
                    AgentActionOutcome.Cancelled,
                    AgentFileCancellationCode(
                        dispatch,
                        permit,
                        callerCancellation)),
            HostResult<AgentFileActionResult>.Failure failure =>
                (AgentActionOutcome.Failed, failure.Error.StableCode),
            HostResult<AgentFileActionResult>.Success success =>
                (
                    AgentActionOutcome.Succeeded,
                    AgentFileSuccessCode(success.Value)),
            _ => throw new InvalidOperationException(
                "A governed file dispatch returned an unknown result."),
        };
        return Completion(permit, outcome, stableCode);
    }

    private static HostResult<AgentFileActionResult>
        NormalizeAgentFileCancellationResult(
            HostResult<AgentFileActionResult> result,
            AgentActionCompletion completion,
            long revision)
    {
        if (completion.Outcome != AgentActionOutcome.Cancelled)
        {
            return result;
        }

        return HostResult<AgentFileActionResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                completion.StableCode ?? "operation_cancelled",
                "The governed file action was cancelled."),
            revision);
    }

    private static string AgentFileCancellationCode(
        AgentFileDispatch dispatch,
        AgentActionPermit permit,
        CancellationToken callerCancellation)
    {
        if (permit.CancellationToken.IsCancellationRequested)
        {
            return "authority_revoked";
        }

        if (dispatch.RuntimeCancellation.IsCancellationRequested)
        {
            return "session_revoked";
        }

        return callerCancellation.IsCancellationRequested
            ? "caller_cancelled"
            : "operation_cancelled";
    }

    private static string AgentFileSuccessCode(
        AgentFileActionResult result) =>
        result switch
        {
            AgentFileActionResult.Page => "files_listed",
            AgentFileActionResult.Entry => "file_stated",
            AgentFileActionResult.Preview => "file_read",
            AgentFileActionResult.CreatedDirectory => "directory_created",
            AgentFileActionResult.Deleted => "file_deleted",
            _ => throw new ArgumentOutOfRangeException(
                nameof(result),
                result.GetType(),
                "The governed file result kind is unsupported."),
        };

    private static bool TryNormalizeListedEntry(
        FilePanelEntry entry,
        FilePanelLocation listedLocation,
        FilePanelLocation trustedRoot,
        out FilePanelEntry? normalized)
    {
        normalized = null;
        if (entry is null
            || entry.IsHidden
            || entry.Location.Address
                is not FilePanelAddress.Hierarchical entryAddress
            || listedLocation.Address
                is not FilePanelAddress.Hierarchical listedAddress
            || entryAddress.Path.Segments.Length
                != listedAddress.Path.Segments.Length + 1
            || !HasPathPrefix(
                entryAddress.Path,
                listedAddress.Path)
            || !IsAtOrBelowTrustedRoot(entry.Location, trustedRoot))
        {
            return false;
        }

        var name = entryAddress.Path.Segments[^1].Value;
        if (!string.Equals(entry.Name, name, StringComparison.Ordinal)
            || !IsSafeAgentFileName(name))
        {
            return false;
        }

        var location = new FilePanelLocation(
            listedLocation.ProviderProfileId,
            listedLocation.Authority,
            new FilePanelAddress.Hierarchical(entryAddress.Path),
            version: null);
        normalized = new FilePanelEntry(
            location,
            name,
            entry.Kind,
            entry.Size,
            entry.LastModifiedAt,
            IsHidden: false);
        return true;
    }

    private static bool TryNormalizeExactEntry(
        FilePanelEntry entry,
        FilePanelLocation requestedLocation,
        FilePanelLocation trustedRoot,
        out FilePanelEntry? normalized)
    {
        normalized = null;
        if (entry is null
            || !LocationsMatchIgnoringVersion(
                entry.Location,
                requestedLocation)
            || !IsAtOrBelowTrustedRoot(entry.Location, trustedRoot)
            || !IsSafeAgentFileName(entry.Name))
        {
            return false;
        }

        if (requestedLocation.Address
                is FilePanelAddress.Hierarchical requestedAddress
            && requestedAddress.Path.Name is { } requestedName
            && !string.Equals(
                entry.Name,
                requestedName.Value,
                StringComparison.Ordinal))
        {
            return false;
        }

        normalized = new FilePanelEntry(
            requestedLocation,
            entry.Name,
            entry.Kind,
            entry.Size,
            entry.LastModifiedAt,
            entry.IsHidden);
        return true;
    }

    private static bool TryNormalizeMediaType(
        string value,
        out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value)
            || Encoding.UTF8.GetByteCount(value)
                > AgentFileActionComposer.MaximumAgentMediaTypeBytes
            || value.Any(char.IsControl))
        {
            return false;
        }

        var separator = value.IndexOf(';', StringComparison.Ordinal);
        var mediaType = (separator < 0 ? value : value[..separator]).Trim();
        var slash = mediaType.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0
            || slash == mediaType.Length - 1
            || mediaType.AsSpan(slash + 1).IndexOf('/') >= 0
            || mediaType.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not (
                    '!' or '#' or '$' or '&' or '^' or '_'
                    or '.' or '+' or '-'
                    or '/')))
        {
            return false;
        }

        normalized = mediaType.ToLowerInvariant();
        return true;
    }

    private static bool IsSafeAgentFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || value.Any(character =>
                character is '\0' or '/' or '\\'
                || char.IsControl(character)
                || char.GetUnicodeCategory(character) is
                    UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator))
        {
            return false;
        }

        try
        {
            return StrictAgentFileUtf8.GetByteCount(value)
                <= AgentFileActionComposer.MaximumAgentFileNameBytes
                && !AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(
                    value);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsAtOrBelowTrustedRoot(
        FilePanelLocation location,
        FilePanelLocation trustedRoot)
    {
        if (!string.Equals(
                location.ProviderProfileId,
                trustedRoot.ProviderProfileId,
                StringComparison.Ordinal)
            || !string.Equals(
                location.Authority,
                trustedRoot.Authority,
                StringComparison.Ordinal)
            || location.Address
                is not FilePanelAddress.Hierarchical locationAddress
            || trustedRoot.Address
                is not FilePanelAddress.Hierarchical rootAddress)
        {
            return false;
        }

        return HasPathPrefix(locationAddress.Path, rootAddress.Path);
    }

    private static bool LocationsMatchIgnoringVersion(
        FilePanelLocation left,
        FilePanelLocation right) =>
        string.Equals(
            left.ProviderProfileId,
            right.ProviderProfileId,
            StringComparison.Ordinal)
        && string.Equals(
            left.Authority,
            right.Authority,
            StringComparison.Ordinal)
        && left.Address is FilePanelAddress.Hierarchical leftAddress
        && right.Address is FilePanelAddress.Hierarchical rightAddress
        && leftAddress.Path.Equals(rightAddress.Path);

    private static bool HasPathPrefix(
        FilePanelPath path,
        FilePanelPath prefix)
    {
        if (path.Segments.Length < prefix.Segments.Length)
        {
            return false;
        }

        for (var index = 0; index < prefix.Segments.Length; index++)
        {
            if (path.Segments[index] != prefix.Segments[index])
            {
                return false;
            }
        }

        return true;
    }

    private static HostResult<AgentFileActionResult>
        MapAgentFileProviderFailure(
            FilePanelError error,
            long revision)
    {
        var (code, stableCode, retryable) = error.Code switch
        {
            FilePanelErrorCode.UnsupportedCapability =>
                (
                    HostErrorCode.CapabilityNotSupported,
                    "file_capability_not_supported",
                    false),
            FilePanelErrorCode.NotFound =>
                (HostErrorCode.NotFound, "file_not_found", false),
            FilePanelErrorCode.Cancelled =>
                (HostErrorCode.Cancelled, "operation_cancelled", false),
            FilePanelErrorCode.Offline =>
                (HostErrorCode.EngineFailed, "file_provider_offline", true),
            FilePanelErrorCode.AuthenticationRequired =>
                (
                    HostErrorCode.EngineFailed,
                    "file_authentication_required",
                    false),
            FilePanelErrorCode.IoFailure
                or FilePanelErrorCode.SharingViolation
                or FilePanelErrorCode.UnexpectedEndOfStream =>
                (HostErrorCode.EngineFailed, "file_provider_failed", true),
            FilePanelErrorCode.LimitExceeded
                or FilePanelErrorCode.RangeNotSatisfiable =>
                (HostErrorCode.InvalidRequest, "file_limit_exceeded", false),
            FilePanelErrorCode.AccessDenied =>
                (HostErrorCode.InvalidRequest, "file_access_denied", false),
            _ =>
                (HostErrorCode.InvalidRequest, "file_operation_rejected", false),
        };
        return HostResult<AgentFileActionResult>.Fail(
            new HostError(
                code,
                stableCode,
                "The File Viewer provider rejected the governed action.",
                retryable),
            revision);
    }

    private static HostResult<AgentFileActionResult>
        InvalidAgentFileProviderResult(long revision) =>
        HostResult<AgentFileActionResult>.Fail(
            new HostError(
                HostErrorCode.EngineFailed,
                InvalidAgentFileResultCode,
                "The File Viewer provider returned an invalid governed result."),
            revision);

    private static HostResult<AgentFileActionResult>
        FileMutationOutcomeUnknown(long revision) =>
        HostResult<AgentFileActionResult>.Fail(
            new HostError(
                HostErrorCode.EngineFailed,
                FileMutationOutcomeUnknownCode,
                "The File Viewer mutation outcome is unknown.",
                Retryable: false),
            revision);

    private static bool HasAgentFileAuthorization(
        AgentActionAuthorization authorization,
        AgentFileRequest request)
    {
        var requiredTool = RequiredAgentFileTool(request);
        var requiredCapability = IsAgentFileMutation(request)
            ? AgentCapability.EditFiles
            : AgentCapability.ReadFiles;
        return string.Equals(
                authorization.ToolName,
                requiredTool,
                StringComparison.Ordinal)
            && BuiltInAgentTools.Catalog.TryGet(
                authorization.ToolName,
                out var descriptor)
            && descriptor!.Capability == requiredCapability;
    }

    private static bool IsAgentFileMutation(AgentFileRequest request) =>
        request is AgentFileRequest.CreateDirectory
            or AgentFileRequest.Delete;

    private static bool HasLiveAgentFileCapability(
        AgentFileDispatch dispatch)
    {
        try
        {
            return dispatch.Files.Capabilities.Contains(
                    dispatch.RequiredSessionCapability)
                && dispatch.Files.Metadata.Capabilities.HasFlag(
                    dispatch.RequiredProviderCapability);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool AuthorizationMatchesBinding(
        AgentActionAuthorization authorization,
        AgentActionExecutionBinding binding) =>
        authorization.ActionId == binding.ActionId
        && authorization.RunId == binding.RunId
        && authorization.ActorId == binding.ActorId
        && string.Equals(
            authorization.ToolName,
            binding.ToolName,
            StringComparison.Ordinal)
        && authorization.TargetIdentity == binding.TargetIdentity
        && authorization.TargetFingerprint == binding.TargetFingerprint
        && authorization.ArgumentDigest == binding.ArgumentDigest
        && authorization.PolicyGeneration == binding.PolicyGeneration;

    private static bool AgentFileBindingsMatch(
        AgentActionExecutionBinding left,
        AgentActionExecutionBinding right) =>
        left.ActionId == right.ActionId
        && left.RunId == right.RunId
        && left.ActorId == right.ActorId
        && string.Equals(
            left.ToolName,
            right.ToolName,
            StringComparison.Ordinal)
        && left.Target == right.Target
        && left.TargetIdentity == right.TargetIdentity
        && left.TargetFingerprint == right.TargetFingerprint
        && left.ArgumentDigest == right.ArgumentDigest
        && left.PolicyGeneration == right.PolicyGeneration;

    private static string RequiredAgentFileTool(AgentFileRequest request) =>
        request switch
        {
            AgentFileRequest.List => BuiltInAgentTools.FilesList,
            AgentFileRequest.Stat => BuiltInAgentTools.FilesStat,
            AgentFileRequest.Read => BuiltInAgentTools.FilesRead,
            AgentFileRequest.CreateDirectory =>
                BuiltInAgentTools.FilesCreateDirectory,
            AgentFileRequest.Delete => BuiltInAgentTools.FilesDelete,
            _ => throw AgentFileDispatchFailure(
                HostErrorCode.InvalidRequest,
                "The governed file request kind is unsupported."),
        };

    private static string RequiredAgentFileSessionCapability(
        AgentFileRequest request) =>
        request switch
        {
            AgentFileRequest.List => SessionCapabilities.FilesList,
            AgentFileRequest.Stat => SessionCapabilities.FilesStat,
            AgentFileRequest.Read => SessionCapabilities.FilesPreview,
            AgentFileRequest.CreateDirectory =>
                SessionCapabilities.FilesCreateDirectory,
            AgentFileRequest.Delete => SessionCapabilities.FilesDelete,
            _ => throw AgentFileDispatchFailure(
                HostErrorCode.InvalidRequest,
                "The governed file request kind is unsupported."),
        };

    private static FilePanelCapability RequiredAgentFileProviderCapability(
        AgentFileRequest request) =>
        request switch
        {
            AgentFileRequest.List => FilePanelCapability.List,
            AgentFileRequest.Stat => FilePanelCapability.Stat,
            AgentFileRequest.Read => FilePanelCapability.RangedRead,
            AgentFileRequest.CreateDirectory =>
                FilePanelCapability.CreateDirectory
                | FilePanelCapability.GovernedCreateDirectory,
            AgentFileRequest.Delete =>
                FilePanelCapability.Delete
                | FilePanelCapability.GovernedDelete,
            _ => throw AgentFileDispatchFailure(
                HostErrorCode.InvalidRequest,
                "The governed file request kind is unsupported."),
        };

    private static SessionId GetAgentFileRequestSessionId(
        AgentFileRequest request) =>
        request switch
        {
            AgentFileRequest.List list => list.SessionId,
            AgentFileRequest.Stat stat => stat.SessionId,
            AgentFileRequest.Read read => read.SessionId,
            AgentFileRequest.CreateDirectory createDirectory =>
                createDirectory.SessionId,
            AgentFileRequest.Delete delete => delete.SessionId,
            _ => throw AgentFileDispatchFailure(
                HostErrorCode.InvalidRequest,
                "The governed file request kind is unsupported."),
        };

    private static ImmutableArray<FilePanelPathSegment> GetAgentFileRelativePath(
        AgentFileRequest request) =>
        request switch
        {
            AgentFileRequest.List list => list.RelativePath,
            AgentFileRequest.Stat stat => stat.RelativePath,
            AgentFileRequest.Read read => read.RelativePath,
            AgentFileRequest.CreateDirectory createDirectory =>
                createDirectory.RelativePath,
            AgentFileRequest.Delete delete => delete.RelativePath,
            _ => throw AgentFileDispatchFailure(
                HostErrorCode.InvalidRequest,
                "The governed file request kind is unsupported."),
        };

    private static HostResult<AgentFileActionResult>
        InvalidAgentFileAction(
            string message,
            long revision) =>
        HostResult<AgentFileActionResult>.Fail(
            HostError.Create(HostErrorCode.InvalidRequest, message),
            revision);

    private static HostResult<AgentFileActionResult>
        MapAgentFileAuthorizationFailure(
            AgentAuthorizationError error,
            long revision)
    {
        var hostError = error.Code switch
        {
            AgentAuthorizationErrorCode.AuthorizationExpired
                or AgentAuthorizationErrorCode.ApprovalExpired =>
                HostError.Create(
                    HostErrorCode.DeadlineExceeded,
                    "The one-action file authorization has expired."),
            AgentAuthorizationErrorCode.Cancelled
                or AgentAuthorizationErrorCode.RunCancelled =>
                HostError.Create(
                    HostErrorCode.Cancelled,
                    "The governed file action was cancelled."),
            AgentAuthorizationErrorCode.AuditUnavailable =>
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The file-agent audit trail is unavailable.",
                    retryable: true),
            _ => HostError.Create(
                HostErrorCode.InvalidRequest,
                "The exact one-action file authorization was rejected."),
        };
        return HostResult<AgentFileActionResult>.Fail(
            hostError,
            revision);
    }

    private static AgentFileDispatchException AgentFileDispatchFailure(
        HostErrorCode code,
        string message) =>
        new(HostError.Create(code, message));

    private sealed record AgentFileDispatch(
        AgentFileRequest Request,
        HostedSession Session,
        IFilePanelSession Files,
        FileSessionMetadata Metadata,
        FilePanelLocation Location,
        string RequiredSessionCapability,
        FilePanelCapability RequiredProviderCapability,
        int MaximumPageSize,
        long MaximumPreviewBytes,
        CancellationToken RuntimeCancellation,
        long ExpectedSessionRevision,
        long Revision);

    private sealed class AgentFileDispatchException(HostError error)
        : Exception(error.Message)
    {
        public HostError Error { get; } = error;
    }
}
