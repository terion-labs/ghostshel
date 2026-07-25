using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    public async ValueTask<HostResult<AgentTerminalActionResult>>
        RunAgentTerminalActionAsync(
            AgentAuthorizationId authorizationId,
            AgentTerminalAction action,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_agentTerminalActionComposer is null
            || _agentAuthorizationConsumer is null)
        {
            return Unsupported<AgentTerminalActionResult>(
                "The governed terminal-agent execution bridge is not composed.",
                revision: 0);
        }

        AgentTerminalDispatch? dispatch = null;
        AgentActionPermit? permit = null;
        HostResult<AgentTerminalActionResult>? preDispatchFailure = null;
        long revision = 0;
        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<AgentTerminalActionResult>(revision);
        }

        try
        {
            ThrowIfDisposed();
            var sessionId = GetRequestSessionId(action.Request);
            var exactContextResult = ResolveExactAgentContext(
                action.Proposal.Target);
            if (exactContextResult is HostResult<AgentContextSnapshot>.Failure contextFailure)
            {
                return HostResult<AgentTerminalActionResult>.Fail(
                    contextFailure.Error,
                    contextFailure.CurrentRevision);
            }

            var exactContext =
                ((HostResult<AgentContextSnapshot>.Success)exactContextResult).Value;
            revision = exactContext.Revision;
            var exactSessionPanel = exactContext.Panels
                .SingleOrDefault(panel => panel.SessionId == sessionId);
            if (exactSessionPanel?.SessionRevision is not long expectedSessionRevision)
            {
                return InvalidAgentTerminalAction(
                    "The exact terminal context has no matching live session revision.",
                    revision);
            }
            if (!TryGetSession(sessionId, out var session))
            {
                return NotFound<AgentTerminalActionResult>("session", revision);
            }

            AgentActionExecutionBinding binding;
            try
            {
                binding = _agentTerminalActionComposer.BindForExecution(
                    action,
                    exactContext);
                dispatch = CaptureAgentTerminalDispatch(
                    action.Request,
                    session,
                    revision,
                    expectedSessionRevision);
            }
            catch (AgentTerminalDispatchException exception)
            {
                return HostResult<AgentTerminalActionResult>.Fail(
                    exception.Error,
                    revision);
            }
            catch (ArgumentException)
            {
                return InvalidAgentTerminalAction(
                    "The prepared action no longer matches the exact live terminal target.",
                    revision);
            }
            catch (InvalidOperationException)
            {
                return InvalidAgentTerminalAction(
                    "The prepared action no longer matches its typed terminal request.",
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
                return MapAuthorizationFailure(denied.Error, revision);
            }

            permit = ((AgentPermitResult.Granted)permitResult).Permit;
            if (action.Request is AgentTerminalRequest.Resize
                && dispatch.ScopeCancellation.IsCancellationRequested)
            {
                preDispatchFailure =
                    HostResult<AgentTerminalActionResult>.Fail(
                        new HostError(
                            HostErrorCode.Cancelled,
                            "attachment_revoked",
                            "The exact interactive attachment was revoked."),
                        revision);
            }
            else if (action.Request is AgentTerminalRequest.Resize resize
                && !dispatch.Session.IsInteractiveAttachmentOwner(
                    resize.Value.AttachmentId,
                    permit.Authorization.ApprovingClientId))
            {
                preDispatchFailure =
                    InvalidAgentTerminalAction(
                        "The exact interactive attachment is not owned by the approving client.",
                        revision);
            }

            if (RequiresOneActionInputLease(action.Request))
            {
                var leaseResult = dispatch.Session.AcquireOneActionAgentLease(
                    permit.Authorization);
                if (leaseResult is HostResult<OneActionAgentLease>.Failure leaseFailure)
                {
                    preDispatchFailure =
                        HostResult<AgentTerminalActionResult>.Fail(
                            leaseFailure.Error,
                            leaseFailure.CurrentRevision);
                }
                else
                {
                    var lease =
                        ((HostResult<OneActionAgentLease>.Success)leaseResult).Value;
                    dispatch = dispatch with
                    {
                        ScopeCancellation = lease.CancellationToken,
                        OneActionLeaseId = lease.Id,
                    };
                }
            }
        }
        catch (AgentTerminalDispatchException exception) when (permit is null)
        {
            return HostResult<AgentTerminalActionResult>.Fail(
                exception.Error,
                revision);
        }
        catch (AgentTerminalDispatchException exception)
        {
            preDispatchFailure = HostResult<AgentTerminalActionResult>.Fail(
                exception.Error,
                revision);
        }
        catch (OperationCanceledException) when (permit is null)
        {
            return Cancelled<AgentTerminalActionResult>(revision);
        }
        catch (OperationCanceledException)
        {
            preDispatchFailure =
                Cancelled<AgentTerminalActionResult>(revision);
        }
        catch (ObjectDisposedException) when (permit is null)
        {
            return Cancelled<AgentTerminalActionResult>(revision);
        }
        catch (ObjectDisposedException)
        {
            preDispatchFailure =
                Cancelled<AgentTerminalActionResult>(revision);
        }
        catch (Exception) when (permit is null)
        {
            return HostResult<AgentTerminalActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The terminal authorization broker is unavailable.",
                    retryable: true),
                revision);
        }
        catch (Exception)
        {
            preDispatchFailure = HostResult<AgentTerminalActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The one-action terminal input lease could not be acquired.",
                    retryable: true),
                revision);
        }
        finally
        {
            _sessionGraphGate.Release();
        }

        if (preDispatchFailure is not null)
        {
            return await CompletePreDispatchFailureAsync(
                    dispatch!,
                    permit!,
                    preDispatchFailure,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await DispatchAndCompleteAgentTerminalActionAsync(
                dispatch!,
                permit!,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static AgentTerminalDispatch CaptureAgentTerminalDispatch(
        AgentTerminalRequest request,
        HostedSession session,
        long revision,
        long expectedSessionRevision)
    {
        var snapshot = session.Snapshot().Descriptor;
        if (snapshot.Lifecycle != SessionLifecycle.Active)
        {
            throw DispatchFailure(
                HostErrorCode.SessionClosed,
                "The exact terminal session is no longer active.");
        }

        if (session.Engine.Kind != PanelKind.Terminal)
        {
            throw DispatchFailure(
                HostErrorCode.CapabilityNotSupported,
                "The exact session is not a terminal.");
        }

        var runtimeCancellation = session.CaptureRuntimeAuthority();
        switch (request)
        {
            case AgentTerminalRequest.ReadScreen:
            case AgentTerminalRequest.WaitForText:
            case AgentTerminalRequest.WaitForChange:
            case AgentTerminalRequest.WaitForStable:
                return new AgentTerminalDispatch(
                    request,
                    session,
                    RequireAutomation(session),
                    Process: null,
                    runtimeCancellation,
                    CancellationToken.None,
                    "operation_cancelled",
                    OneActionLeaseId: null,
                    revision);
            case AgentTerminalRequest.SendText:
            case AgentTerminalRequest.SendKey:
            case AgentTerminalRequest.Interrupt:
                return InputDispatch(
                    request,
                    session,
                    runtimeCancellation,
                    revision);
            case AgentTerminalRequest.SendChord:
                RequireAgentChordCapabilities(session);
                return InputDispatch(
                    request,
                    session,
                    runtimeCancellation,
                    revision);
            case AgentTerminalRequest.Paste:
                RequireAgentPasteCapabilities(session);
                return InputDispatch(
                    request,
                    session,
                    runtimeCancellation,
                    revision);
            case AgentTerminalRequest.SendMouse:
                RequireAgentMouseCapabilities(session);
                return InputDispatch(
                    request,
                    session,
                    runtimeCancellation,
                    revision);
            case AgentTerminalRequest.Resize resize:
                if (!session.Engine.Capabilities.Contains(
                        SessionCapabilities.TerminalResize))
                {
                    throw DispatchFailure(
                        HostErrorCode.CapabilityNotSupported,
                        "The terminal no longer supports resize.");
                }

                if (!session.TryCaptureResizeAttachmentAuthority(
                        resize.Value.AttachmentId,
                        resize.Value.Viewport,
                        expectedSessionRevision,
                        out var attachmentCancellation))
                {
                    throw DispatchFailure(
                        HostErrorCode.NotFound,
                        "The exact interactive terminal attachment is unavailable or changed.");
                }

                if (session.Engine is not ITerminalProcess process)
                {
                    throw DispatchFailure(
                        HostErrorCode.CapabilityNotSupported,
                        "The terminal does not expose its process lifecycle port.");
                }

                return new AgentTerminalDispatch(
                    request,
                    session,
                    Automation: null,
                    process,
                    runtimeCancellation,
                    attachmentCancellation,
                    "attachment_revoked",
                    OneActionLeaseId: null,
                    revision,
                    ExpectedSessionRevision: expectedSessionRevision);
            default:
                throw DispatchFailure(
                    HostErrorCode.InvalidRequest,
                    "The terminal-agent request kind is unsupported.");
        }
    }

    private static AgentTerminalDispatch InputDispatch(
        AgentTerminalRequest request,
        HostedSession session,
        CancellationToken runtimeCancellation,
        long revision)
    {
        return new AgentTerminalDispatch(
            request,
            session,
            RequireAutomation(session),
            Process: null,
            runtimeCancellation,
            CancellationToken.None,
            "input_lease_revoked",
            OneActionLeaseId: null,
            revision);
    }

    private static ITerminalAutomation RequireAutomation(HostedSession session) =>
        session.Engine as ITerminalAutomation
        ?? throw DispatchFailure(
            HostErrorCode.CapabilityNotSupported,
            "The terminal does not expose its automation port.");

    private static void RequireAgentMouseCapabilities(HostedSession session)
    {
        var capabilities = session.Engine.Capabilities;
        if (!capabilities.Contains(SessionCapabilities.TerminalMouse)
            || !capabilities.Contains(
                SessionCapabilities.TerminalAgentInputBarrier))
        {
            throw DispatchFailure(
                HostErrorCode.CapabilityNotSupported,
                "The terminal cannot safely accept governed mouse input.");
        }
    }

    private static void RequireAgentPasteCapabilities(HostedSession session)
    {
        if (!HasAgentPasteCapabilities(session))
        {
            throw DispatchFailure(
                HostErrorCode.CapabilityNotSupported,
                "The terminal cannot safely accept governed paste input.");
        }
    }

    private static void RequireAgentChordCapabilities(HostedSession session)
    {
        if (!HasAgentChordCapabilities(session))
        {
            throw DispatchFailure(
                HostErrorCode.CapabilityNotSupported,
                "The terminal cannot safely accept governed character chords.");
        }
    }

    private static bool HasAgentChordCapabilities(HostedSession session)
    {
        var capabilities = session.Engine.Capabilities;
        return capabilities.Contains(SessionCapabilities.TerminalSendChord)
            && capabilities.Contains(
                SessionCapabilities.TerminalAgentInputBarrier);
    }

    private static bool HasAgentPasteCapabilities(HostedSession session)
    {
        var capabilities = session.Engine.Capabilities;
        return capabilities.Contains(SessionCapabilities.TerminalPaste)
            && capabilities.Contains(
                SessionCapabilities.TerminalAgentInputBarrier);
    }

    private static bool RequiresOneActionInputLease(
        AgentTerminalRequest request) =>
        request is AgentTerminalRequest.SendText
            or AgentTerminalRequest.Paste
            or AgentTerminalRequest.SendKey
            or AgentTerminalRequest.SendChord
            or AgentTerminalRequest.SendMouse
            or AgentTerminalRequest.Interrupt;

    private async ValueTask<HostResult<AgentTerminalActionResult>>
        CompletePreDispatchFailureAsync(
            AgentTerminalDispatch dispatch,
            AgentActionPermit permit,
            HostResult<AgentTerminalActionResult> failure,
            CancellationToken callerCancellation)
    {
        try
        {
            var hostFailure =
                (HostResult<AgentTerminalActionResult>.Failure)failure;
            var cancelled = permit.CancellationToken.IsCancellationRequested
                || hostFailure.Error.Code == HostErrorCode.Cancelled;
            var stableCode = permit.CancellationToken.IsCancellationRequested
                ? "authority_revoked"
                : callerCancellation.IsCancellationRequested
                    ? "caller_cancelled"
                    : hostFailure.Error.StableCode;
            var completion = Completion(
                permit,
                cancelled
                    ? AgentActionOutcome.Cancelled
                    : AgentActionOutcome.Failed,
                stableCode);
            var normalizedFailure = NormalizeAgentTerminalCancellationResult(
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
        finally
        {
            ReleaseOneActionLease(dispatch, permit);
        }
    }

    private async ValueTask<HostResult<AgentTerminalActionResult>>
        DispatchAndCompleteAgentTerminalActionAsync(
            AgentTerminalDispatch dispatch,
            AgentActionPermit permit,
            CancellationToken callerCancellation)
    {
        try
        {
            using var executionCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    callerCancellation,
                    permit.CancellationToken,
                    dispatch.RuntimeCancellation,
                    dispatch.ScopeCancellation);
            HostResult<AgentTerminalActionResult> result;
            if (executionCancellation.IsCancellationRequested)
            {
                result = Cancelled<AgentTerminalActionResult>(dispatch.Revision);
            }
            else
            {
                result = await DispatchAgentTerminalActionAsync(
                        dispatch,
                        permit.Authorization.ApprovingClientId,
                        permit.Authorization.Source,
                        executionCancellation.Token)
                    .ConfigureAwait(false);
            }

            var completion = CreateAgentTerminalCompletion(
                result,
                dispatch,
                permit,
                callerCancellation);
            var normalizedResult = NormalizeAgentTerminalCancellationResult(
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
        finally
        {
            ReleaseOneActionLease(dispatch, permit);
        }
    }

    private static async ValueTask<HostResult<AgentTerminalActionResult>>
        DispatchAgentTerminalActionAsync(
            AgentTerminalDispatch dispatch,
            ClientId approvingClientId,
            AgentAuthorizationSource authorizationSource,
            CancellationToken cancellationToken)
    {
        try
        {
            return dispatch.Request switch
            {
                AgentTerminalRequest.ReadScreen =>
                    await ReadAgentTerminalScreenAsync(
                            dispatch,
                            cancellationToken)
                        .ConfigureAwait(false),
                AgentTerminalRequest.SendText sendText =>
                    await SendTextAsync(
                            dispatch,
                            sendText.Text,
                            cancellationToken)
                        .ConfigureAwait(false),
                AgentTerminalRequest.Paste paste =>
                    await PasteAsync(
                            dispatch,
                            paste.Text,
                            authorizationSource,
                            cancellationToken)
                        .ConfigureAwait(false),
                AgentTerminalRequest.SendKey sendKey =>
                    await SendKeyAsync(
                            dispatch,
                            sendKey.KeyStroke,
                            cancellationToken)
                        .ConfigureAwait(false),
                AgentTerminalRequest.SendChord sendChord =>
                    await SendChordAsync(
                            dispatch,
                            sendChord.Chord,
                            authorizationSource,
                            cancellationToken)
                        .ConfigureAwait(false),
                AgentTerminalRequest.SendMouse sendMouse =>
                    await SendMouseAsync(
                            dispatch,
                            sendMouse.MouseInput,
                            cancellationToken)
                        .ConfigureAwait(false),
                AgentTerminalRequest.WaitForText wait =>
                    HostResult<AgentTerminalActionResult>.Succeed(
                        new AgentTerminalActionResult.Wait(
                            await dispatch.Automation!
                                .WaitForTextAsync(
                                    wait.Value.Wait,
                                    cancellationToken)
                                .ConfigureAwait(false)),
                        dispatch.Revision),
                AgentTerminalRequest.WaitForChange wait =>
                    HostResult<AgentTerminalActionResult>.Succeed(
                        new AgentTerminalActionResult.Wait(
                            await dispatch.Automation!
                                .WaitForChangeAsync(
                                    wait.Value.Wait,
                                    cancellationToken)
                                .ConfigureAwait(false)),
                        dispatch.Revision),
                AgentTerminalRequest.WaitForStable wait =>
                    HostResult<AgentTerminalActionResult>.Succeed(
                        new AgentTerminalActionResult.Wait(
                            await dispatch.Automation!
                                .WaitForStableAsync(
                                    wait.Value.Wait,
                                    cancellationToken)
                                .ConfigureAwait(false)),
                        dispatch.Revision),
                AgentTerminalRequest.Interrupt =>
                    await InterruptAsync(dispatch, cancellationToken)
                        .ConfigureAwait(false),
                AgentTerminalRequest.Resize resize =>
                    await ResizeAsync(
                            dispatch,
                            resize.Value,
                            approvingClientId,
                            cancellationToken)
                        .ConfigureAwait(false),
                _ => InvalidAgentTerminalAction(
                    "The terminal-agent request kind is unsupported.",
                    dispatch.Revision),
            };
        }
        catch (OperationCanceledException)
        {
            return Cancelled<AgentTerminalActionResult>(dispatch.Revision);
        }
        catch (Exception)
        {
            return HostResult<AgentTerminalActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The terminal engine could not complete the governed action."),
                dispatch.Revision);
        }
    }

    private static async ValueTask<HostResult<AgentTerminalActionResult>>
        ReadAgentTerminalScreenAsync(
            AgentTerminalDispatch dispatch,
            CancellationToken cancellationToken)
    {
        var snapshot = await dispatch.Automation!
            .ReadScreenAsync(cancellationToken)
            .ConfigureAwait(false);
        dispatch.Session.UpdateTerminalWorkingDirectory(snapshot.WorkingDirectory);
        return HostResult<AgentTerminalActionResult>.Succeed(
            new AgentTerminalActionResult.Screen(snapshot),
            dispatch.Session.Snapshot().Descriptor.Revision);
    }

    private static async ValueTask<HostResult<AgentTerminalActionResult>>
        SendTextAsync(
            AgentTerminalDispatch dispatch,
            string text,
            CancellationToken cancellationToken)
    {
        await dispatch.Automation!
            .WriteAsync(text, cancellationToken)
            .ConfigureAwait(false);
        return Completed(dispatch.Revision);
    }

    private static async ValueTask<HostResult<AgentTerminalActionResult>>
        SendKeyAsync(
            AgentTerminalDispatch dispatch,
            TerminalKeyStroke keyStroke,
            CancellationToken cancellationToken)
    {
        await dispatch.Automation!
            .SendKeyAsync(keyStroke, cancellationToken)
            .ConfigureAwait(false);
        return Completed(dispatch.Revision);
    }

    private static async ValueTask<HostResult<AgentTerminalActionResult>>
        SendChordAsync(
            AgentTerminalDispatch dispatch,
            TerminalCharacterChord chord,
            AgentAuthorizationSource authorizationSource,
            CancellationToken cancellationToken)
    {
        if (!HasAgentChordCapabilities(dispatch.Session))
        {
            return HostResult<AgentTerminalActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.CapabilityNotSupported,
                    "The terminal can no longer safely accept governed character chords."),
                dispatch.Revision);
        }

        if (authorizationSource is not (
                AgentAuthorizationSource.HumanApproval
                or AgentAuthorizationSource.YoloPolicy))
        {
            return HostResult<AgentTerminalActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.ConfirmationRequired,
                    "Governed terminal character chords require explicit human approval "
                    + "or run-local YOLO."),
                dispatch.Revision);
        }

        await dispatch.Automation!
            .SendChordAsync(chord, cancellationToken)
            .ConfigureAwait(false);
        return Completed(dispatch.Revision);
    }

    private static async ValueTask<HostResult<AgentTerminalActionResult>>
        PasteAsync(
            AgentTerminalDispatch dispatch,
            string text,
            AgentAuthorizationSource authorizationSource,
            CancellationToken cancellationToken)
    {
        if (!HasAgentPasteCapabilities(dispatch.Session))
        {
            return HostResult<AgentTerminalActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.CapabilityNotSupported,
                    "The terminal can no longer safely accept governed paste input."),
                dispatch.Revision);
        }

        if (authorizationSource is not (
                AgentAuthorizationSource.HumanApproval
                or AgentAuthorizationSource.YoloPolicy))
        {
            return HostResult<AgentTerminalActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.ConfirmationRequired,
                    "Governed terminal paste requires explicit human approval or run-local YOLO."),
                dispatch.Revision);
        }

        var result = await dispatch.Automation!
            .PasteAsync(
                new TerminalPasteInput(text, ConfirmedUnsafe: true),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Sent && !result.RequiresConfirmation)
        {
            return Completed(dispatch.Revision);
        }

        if (!result.Sent && result.RequiresConfirmation)
        {
            return HostResult<AgentTerminalActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.ConfirmationRequired,
                    "Unsafe terminal paste requires explicit human approval or run-local YOLO."),
                dispatch.Revision);
        }

        return HostResult<AgentTerminalActionResult>.Fail(
            new HostError(
                HostErrorCode.EngineFailed,
                "invalid_paste_result",
                "The terminal returned an invalid governed paste result."),
            dispatch.Revision);
    }

    private static async ValueTask<HostResult<AgentTerminalActionResult>>
        SendMouseAsync(
            AgentTerminalDispatch dispatch,
            TerminalMouseInput mouseInput,
            CancellationToken cancellationToken)
    {
        await dispatch.Automation!
            .SendMouseAsync(mouseInput, cancellationToken)
            .ConfigureAwait(false);
        return Completed(dispatch.Revision);
    }

    private static async ValueTask<HostResult<AgentTerminalActionResult>>
        InterruptAsync(
            AgentTerminalDispatch dispatch,
            CancellationToken cancellationToken)
    {
        await dispatch.Automation!
            .InterruptAsync(cancellationToken)
            .ConfigureAwait(false);
        return Completed(dispatch.Revision);
    }

    private static async ValueTask<HostResult<AgentTerminalActionResult>>
        ResizeAsync(
            AgentTerminalDispatch dispatch,
            TerminalResizeRequest request,
            ClientId approvingClientId,
            CancellationToken cancellationToken)
    {
        var expectedSessionRevision = dispatch.ExpectedSessionRevision
            ?? throw new InvalidOperationException(
                "A governed resize requires an exact session revision.");
        await dispatch.Session
            .WaitForResizeAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (!dispatch.Session.CanExecuteAgentResize(
                    request.AttachmentId,
                    request.Viewport,
                    approvingClientId,
                    expectedSessionRevision,
                    dispatch.ScopeCancellation))
            {
                return HostResult<AgentTerminalActionResult>.Fail(
                    new HostError(
                        HostErrorCode.Cancelled,
                        "attachment_revoked",
                        "The exact interactive attachment changed before resize."),
                    dispatch.Revision);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await dispatch.Process!
                .ResizeAsync(request.Viewport, cancellationToken)
                .ConfigureAwait(false);
            var update = dispatch.Session.UpdateAgentResizeViewport(
                request.AttachmentId,
                request.Viewport,
                approvingClientId,
                dispatch.ScopeCancellation);
            return update switch
            {
                HostResult<Unit>.Success =>
                    Completed(dispatch.Revision),
                HostResult<Unit>.Failure failure =>
                    HostResult<AgentTerminalActionResult>.Fail(
                        failure.Error,
                        failure.CurrentRevision),
                _ => throw new InvalidOperationException(
                    "A terminal viewport update returned an unknown result."),
            };
        }
        finally
        {
            dispatch.Session.ReleaseResize();
        }
    }

    private AgentActionCompletion CreateAgentTerminalCompletion(
        HostResult<AgentTerminalActionResult> result,
        AgentTerminalDispatch dispatch,
        AgentActionPermit permit,
        CancellationToken callerCancellation)
    {
        var (outcome, stableCode) = result switch
        {
            HostResult<AgentTerminalActionResult>.Failure failure
                when failure.Error.Code == HostErrorCode.Cancelled =>
                (
                    AgentActionOutcome.Cancelled,
                    CancellationCode(
                        dispatch,
                        permit,
                        callerCancellation)),
            HostResult<AgentTerminalActionResult>.Failure failure =>
                (AgentActionOutcome.Failed, failure.Error.StableCode),
            HostResult<AgentTerminalActionResult>.Success
            {
                Value: AgentTerminalActionResult.Wait
                {
                    Outcome.Kind: TerminalWaitOutcomeKind.Cancelled,
                },
            } =>
                (
                    AgentActionOutcome.Cancelled,
                    CancellationCode(
                        dispatch,
                        permit,
                        callerCancellation)),
            HostResult<AgentTerminalActionResult>.Success success =>
                (
                    AgentActionOutcome.Succeeded,
                    SuccessCode(success.Value)),
            _ => throw new InvalidOperationException(
                "A terminal-agent dispatch returned an unknown result."),
        };
        var finishedAt = _timeProvider.GetUtcNow();
        if (finishedAt < permit.StartedAtUtc)
        {
            finishedAt = permit.StartedAtUtc;
        }

        return new AgentActionCompletion(outcome, stableCode, finishedAt);
    }

    private static HostResult<AgentTerminalActionResult>
        NormalizeAgentTerminalCancellationResult(
            HostResult<AgentTerminalActionResult> result,
            AgentActionCompletion completion,
            long revision)
    {
        if (completion.Outcome != AgentActionOutcome.Cancelled)
        {
            return result;
        }

        return HostResult<AgentTerminalActionResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                completion.StableCode ?? "operation_cancelled",
                "The governed terminal action was cancelled."),
            revision);
    }

    private static void ReleaseOneActionLease(
        AgentTerminalDispatch dispatch,
        AgentActionPermit permit)
    {
        if (dispatch.OneActionLeaseId is { } leaseId)
        {
            dispatch.Session.ReleaseOneActionAgentLease(
                leaseId,
                permit.Authorization.ActorId);
        }
    }

    private static string CancellationCode(
        AgentTerminalDispatch dispatch,
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

        if (dispatch.ScopeCancellation.IsCancellationRequested)
        {
            return dispatch.ScopeRevocationCode;
        }

        return callerCancellation.IsCancellationRequested
            ? "caller_cancelled"
            : "operation_cancelled";
    }

    private static string SuccessCode(AgentTerminalActionResult result) =>
        result switch
        {
            AgentTerminalActionResult.Completed => "ok",
            AgentTerminalActionResult.Screen => "screen_read",
            AgentTerminalActionResult.Wait wait => wait.Outcome.Kind switch
            {
                TerminalWaitOutcomeKind.Matched => "wait_matched",
                TerminalWaitOutcomeKind.Changed => "wait_changed",
                TerminalWaitOutcomeKind.Stable => "wait_stable",
                TerminalWaitOutcomeKind.Timeout => "wait_timeout",
                TerminalWaitOutcomeKind.SessionEnded => "session_ended",
                TerminalWaitOutcomeKind.Cancelled => "operation_cancelled",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(result),
                    wait.Outcome.Kind,
                    "The terminal wait outcome is unsupported."),
            },
            _ => throw new ArgumentOutOfRangeException(
                nameof(result),
                result.GetType(),
                "The terminal-agent result kind is unsupported."),
        };

    private static SessionId GetRequestSessionId(AgentTerminalRequest request) =>
        request switch
        {
            AgentTerminalRequest.ReadScreen read => read.SessionId,
            AgentTerminalRequest.SendText sendText => sendText.SessionId,
            AgentTerminalRequest.Paste paste => paste.SessionId,
            AgentTerminalRequest.SendKey sendKey => sendKey.SessionId,
            AgentTerminalRequest.SendChord sendChord => sendChord.SessionId,
            AgentTerminalRequest.SendMouse sendMouse => sendMouse.SessionId,
            AgentTerminalRequest.WaitForText wait => wait.Value.SessionId,
            AgentTerminalRequest.WaitForChange wait => wait.Value.SessionId,
            AgentTerminalRequest.WaitForStable wait => wait.Value.SessionId,
            AgentTerminalRequest.Interrupt interrupt => interrupt.SessionId,
            AgentTerminalRequest.Resize resize => resize.Value.SessionId,
            _ => throw DispatchFailure(
                HostErrorCode.InvalidRequest,
                "The terminal-agent request kind is unsupported."),
        };

    private static HostResult<AgentTerminalActionResult> Completed(long revision) =>
        HostResult<AgentTerminalActionResult>.Succeed(
            new AgentTerminalActionResult.Completed(),
            revision);

    private static HostResult<AgentTerminalActionResult> InvalidAgentTerminalAction(
        string message,
        long revision) =>
        HostResult<AgentTerminalActionResult>.Fail(
            HostError.Create(HostErrorCode.InvalidRequest, message),
            revision);

    private static HostResult<AgentTerminalActionResult> MapAuthorizationFailure(
        AgentAuthorizationError error,
        long revision)
    {
        var hostError = error.Code switch
        {
            AgentAuthorizationErrorCode.AuthorizationExpired
                or AgentAuthorizationErrorCode.ApprovalExpired =>
                HostError.Create(
                    HostErrorCode.DeadlineExceeded,
                    "The one-action terminal authorization has expired."),
            AgentAuthorizationErrorCode.Cancelled
                or AgentAuthorizationErrorCode.RunCancelled =>
                HostError.Create(
                    HostErrorCode.Cancelled,
                    "The terminal-agent action was cancelled."),
            AgentAuthorizationErrorCode.AuditUnavailable =>
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The terminal-agent audit trail is unavailable.",
                    retryable: true),
            _ => HostError.Create(
                HostErrorCode.InvalidRequest,
                "The exact one-action terminal authorization was rejected."),
        };
        return HostResult<AgentTerminalActionResult>.Fail(hostError, revision);
    }

    private static AgentTerminalDispatchException DispatchFailure(
        HostErrorCode code,
        string message) =>
        new(HostError.Create(code, message));

    private sealed record AgentTerminalDispatch(
        AgentTerminalRequest Request,
        HostedSession Session,
        ITerminalAutomation? Automation,
        ITerminalProcess? Process,
        CancellationToken RuntimeCancellation,
        CancellationToken ScopeCancellation,
        string ScopeRevocationCode,
        InputLeaseId? OneActionLeaseId,
        long Revision,
        long? ExpectedSessionRevision = null);

    private sealed class AgentTerminalDispatchException(HostError error)
        : Exception(error.Message)
    {
        public HostError Error { get; } = error;
    }
}
