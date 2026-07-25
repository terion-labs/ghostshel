using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    public async ValueTask<HostResult<SessionSnapshot>> EnsureBrowserSessionAsync(
        EnsureBrowserSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.InitialAddress);
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();

        var fingerprint = Fingerprint(
            ApplicationOperations.BrowserOpen,
            request.SessionId.Value,
            request.Owner.PanelId.Value,
            request.InitialAddress.ToString());
        if (TryReplay(context, fingerprint, 0, out HostResult<SessionSnapshot>? replay))
        {
            return replay;
        }

        var invalid = ValidateContext<SessionSnapshot>(context, cancellationToken, 0);
        if (invalid is not null)
        {
            return invalid;
        }

        if (_browserPanelFactory is null)
        {
            return Unsupported<SessionSnapshot>(
                "This session host has no browser-panel session factory.",
                0);
        }

        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<SessionSnapshot>(0);
        }

        try
        {
            ThrowIfDisposed();
            if (WorkspaceGraphFailure<SessionSnapshot>(
                    _workspaceGraphs.ValidateSessionOwner(
                        request.Owner,
                        PanelKind.Browser)) is { } ownerFailure)
            {
                return ownerFailure;
            }

            if (TryGetSession(request.SessionId, out var existing))
            {
                var existingSnapshot = existing.Snapshot();
                if (existingSnapshot.Descriptor.Owner != request.Owner
                    || existing.Engine.Kind != PanelKind.Browser)
                {
                    return HostResult<SessionSnapshot>.Fail(
                        HostError.Create(
                            HostErrorCode.InvalidRequest,
                            "The requested session ID already belongs to another panel or session kind."),
                        existingSnapshot.Descriptor.Revision);
                }

                if (existingSnapshot.Descriptor.Lifecycle is
                    SessionLifecycle.Closed or SessionLifecycle.Failed)
                {
                    return ClosedSession<SessionSnapshot>(
                        existingSnapshot.Descriptor.Revision);
                }

                if (WorkspaceGraphFailure<SessionSnapshot>(
                        _workspaceGraphs.LinkSession(
                            request.Owner,
                            PanelKind.Browser,
                            request.SessionId)) is { } existingLinkFailure)
                {
                    return existingLinkFailure;
                }

                var existingResult = HostResult<SessionSnapshot>.Succeed(
                    existingSnapshot,
                    existingSnapshot.Descriptor.Revision);
                StoreReplay(context, fingerprint, existingResult);
                return existingResult;
            }

            IBrowserPanelSession engine;
            PanelSessionSnapshot engineSnapshot;
            using var operationCancellation = HostedOperationCancellation.Create(
                context,
                cancellationToken,
                _timeProvider);
            try
            {
                engine = await _browserPanelFactory
                    .CreateAsync(
                        request.SessionId,
                        request.InitialAddress,
                        operationCancellation.Token)
                    .ConfigureAwait(false);
                if (!_browserPanelCapabilities.Values.SequenceEqual(
                        engine.Capabilities.Values,
                        StringComparer.Ordinal))
                {
                    await engine.DisposeAsync().ConfigureAwait(false);
                    return HostResult<SessionSnapshot>.Fail(
                        HostError.Create(
                            HostErrorCode.EngineFailed,
                            "The browser session capability profile does not match its factory."),
                        0);
                }

                engineSnapshot = await engine
                    .SnapshotAsync(operationCancellation.Token)
                    .ConfigureAwait(false);
                if (operationCancellation.DeadlineElapsed)
                {
                    await engine.DisposeAsync().ConfigureAwait(false);
                    return DeadlineExceeded<SessionSnapshot>(0);
                }
            }
            catch (OperationCanceledException)
            {
                return operationCancellation.DeadlineElapsed
                    ? DeadlineExceeded<SessionSnapshot>(0)
                    : Cancelled<SessionSnapshot>(0);
            }
            catch (Exception exception)
            {
                return EngineFailure<SessionSnapshot>(exception, 0);
            }

            var hosted = new HostedSession(
                engine,
                request.Owner,
                request.Title,
                engineSnapshot,
                _eventRetention,
                _timeProvider);
            lock (_gate)
            {
                _sessions.Add(request.SessionId, hosted);
            }

            if (WorkspaceGraphFailure<SessionSnapshot>(
                    _workspaceGraphs.LinkSession(
                        request.Owner,
                        PanelKind.Browser,
                        request.SessionId)) is { } linkFailure)
            {
                return await RemoveRejectedSessionAsync(hosted, linkFailure)
                    .ConfigureAwait(false);
            }

            var snapshot = hosted.Snapshot();
            var result = HostResult<SessionSnapshot>.Succeed(
                snapshot,
                snapshot.Descriptor.Revision);
            StoreReplay(context, fingerprint, result);
            return result;
        }
        finally
        {
            _sessionGraphGate.Release();
        }
    }

    public async ValueTask<HostResult<Unit>> AttachBrowserRendererAsync(
        AttachBrowserRendererRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Renderer);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetSession(request.SessionId, out var session))
        {
            return NotFound<Unit>("session", 0);
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<Unit>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!session.IsInteractiveAttachmentOwner(request.AttachmentId, context.Actor))
        {
            return HostResult<Unit>.Fail(
                HostError.Create(
                    HostErrorCode.LeaseDenied,
                    "The browser renderer requires the exact interactive human attachment."),
                revision);
        }

        if (session.Engine.Kind != PanelKind.Browser
            || session.Engine is not IBrowserRendererAttachment rendererAttachment)
        {
            return Unsupported<Unit>("The session is not a browser.", revision);
        }

        try
        {
            await rendererAttachment
                .AttachRendererAsync(request.Renderer, cancellationToken)
                .ConfigureAwait(false);
            var engineSnapshot = await session.Engine
                .SnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!session.ApplyEngineSnapshot(engineSnapshot))
            {
                session.RecordStateChange("Browser renderer attached.");
            }

            return HostResult<Unit>.Succeed(
                Unit.Value,
                session.Snapshot().Descriptor.Revision);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<Unit>(revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<Unit>(exception, revision);
        }
    }

    public ValueTask<HostResult<BrowserResult<BrowserSessionState>>> ReadBrowserStateAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        ExecuteBrowserOperationAsync(
            sessionId,
            ApplicationOperations.BrowserReadState,
            string.Empty,
            SessionCapabilities.BrowserReadState,
            context,
            cancellationToken,
            changesState: false,
            static (browser, token) =>
            {
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(
                    BrowserResult<BrowserSessionState>.Success(browser.State));
            });

    public ValueTask<HostResult<BrowserResult<BrowserSessionState>>> NavigateBrowserAsync(
        BrowserNavigateRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Address);
        return ExecuteBrowserOperationAsync(
            request.SessionId,
            ApplicationOperations.BrowserNavigate,
            request.Address.ToString(),
            SessionCapabilities.BrowserNavigate,
            context,
            cancellationToken,
            changesState: true,
            (browser, token) => browser.NavigateAsync(request.Address, token));
    }

    public ValueTask<HostResult<BrowserResult<BrowserSessionState>>> GoBackBrowserAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        ExecuteBrowserOperationAsync(
            sessionId,
            ApplicationOperations.BrowserBack,
            string.Empty,
            SessionCapabilities.BrowserBack,
            context,
            cancellationToken,
            changesState: true,
            static (browser, token) => browser.GoBackAsync(token));

    public ValueTask<HostResult<BrowserResult<BrowserSessionState>>> GoForwardBrowserAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        ExecuteBrowserOperationAsync(
            sessionId,
            ApplicationOperations.BrowserForward,
            string.Empty,
            SessionCapabilities.BrowserForward,
            context,
            cancellationToken,
            changesState: true,
            static (browser, token) => browser.GoForwardAsync(token));

    public ValueTask<HostResult<BrowserResult<BrowserSessionState>>> ReloadBrowserAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        ExecuteBrowserOperationAsync(
            sessionId,
            ApplicationOperations.BrowserReload,
            string.Empty,
            SessionCapabilities.BrowserReload,
            context,
            cancellationToken,
            changesState: true,
            static (browser, token) => browser.ReloadAsync(token));

    public ValueTask<HostResult<BrowserResult<BrowserSessionState>>> StopBrowserAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        ExecuteBrowserOperationAsync(
            sessionId,
            ApplicationOperations.BrowserStop,
            string.Empty,
            SessionCapabilities.BrowserStop,
            context,
            cancellationToken,
            changesState: true,
            static (browser, token) => browser.StopAsync(token));

    private async ValueTask<HostResult<BrowserResult<BrowserSessionState>>>
        ExecuteBrowserOperationAsync(
            SessionId sessionId,
            string operationName,
            string operationKey,
            string requiredCapability,
            OperationContext context,
            CancellationToken cancellationToken,
            bool changesState,
            Func<
                IBrowserPanelSession,
                CancellationToken,
                ValueTask<BrowserResult<BrowserSessionState>>> operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        var useIdempotencyGate = changesState && context.IdempotencyKey is not null;
        if (useIdempotencyGate)
        {
            try
            {
                await _idempotentBrowserOperationGate
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Cancelled<BrowserResult<BrowserSessionState>>(
                    CurrentRevision(sessionId));
            }
        }

        try
        {
            if (!TryGetBrowserPanel(
                    sessionId,
                    out var session,
                    out var browser,
                    out HostResult<BrowserResult<BrowserSessionState>>? failure))
            {
                return failure;
            }

            var revision = session.Snapshot().Descriptor.Revision;
            var invalid = ValidateContext<BrowserResult<BrowserSessionState>>(
                context,
                cancellationToken,
                revision);
            if (invalid is not null)
            {
                return invalid;
            }

            if (RevisionConflict(
                    context,
                    session,
                    out HostResult<BrowserResult<BrowserSessionState>>? conflict))
            {
                return conflict;
            }

            if (context.Actor is not
                {
                    Kind: ActorKind.Human,
                    ClientId: { } clientId,
                }
                || context.Actor.Id.Value != clientId.Value
                || !session.TryCaptureBrowserAttachmentAuthority(
                    clientId,
                    revision,
                    out _,
                    out var attachmentAuthority))
            {
                return HostResult<BrowserResult<BrowserSessionState>>.Fail(
                    HostError.Create(
                        HostErrorCode.LeaseDenied,
                        "Browser operations require the current interactive human attachment."),
                    revision);
            }

            var fingerprint = Fingerprint(
                operationName,
                sessionId.Value,
                operationKey);
            if (changesState
                && TryReplay(
                    context,
                    fingerprint,
                    revision,
                    out HostResult<BrowserResult<BrowserSessionState>>? replay))
            {
                return replay;
            }

            if (!browser.Capabilities.Contains(requiredCapability))
            {
                return Unsupported<BrowserResult<BrowserSessionState>>(
                    $"The browser engine does not support {operationName}.",
                    revision);
            }

            using var attachmentCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    attachmentAuthority);
            using var operationCancellation = HostedOperationCancellation.Create(
                context,
                attachmentCancellation.Token,
                _timeProvider);
            try
            {
                var browserResult = await operation(browser, operationCancellation.Token)
                    .ConfigureAwait(false);
                if (!browserResult.IsSuccess
                    && attachmentAuthority.IsCancellationRequested)
                {
                    return HostResult<BrowserResult<BrowserSessionState>>.Fail(
                        new HostError(
                            HostErrorCode.Cancelled,
                            "attachment_revoked",
                            "The interactive browser attachment was revoked."),
                        revision);
                }

                if (!browserResult.IsSuccess
                    && cancellationToken.IsCancellationRequested)
                {
                    return Cancelled<BrowserResult<BrowserSessionState>>(revision);
                }

                if (!browserResult.IsSuccess
                    && operationCancellation.DeadlineElapsed)
                {
                    return DeadlineExceeded<BrowserResult<BrowserSessionState>>(revision);
                }

                if (changesState && browserResult.IsSuccess)
                {
                    var engineSnapshot = await browser
                        .SnapshotAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    if (!session.ApplyEngineSnapshot(engineSnapshot))
                    {
                        session.RecordStateChange($"{operationName} completed.");
                    }
                }

                var resultingRevision = session.Snapshot().Descriptor.Revision;
                var result =
                    HostResult<BrowserResult<BrowserSessionState>>.Succeed(
                        browserResult,
                        resultingRevision);
                if (changesState)
                {
                    StoreReplay(context, fingerprint, result);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                if (attachmentAuthority.IsCancellationRequested)
                {
                    return HostResult<BrowserResult<BrowserSessionState>>.Fail(
                        new HostError(
                            HostErrorCode.Cancelled,
                            "attachment_revoked",
                            "The interactive browser attachment was revoked."),
                        revision);
                }

                return operationCancellation.DeadlineElapsed
                    ? DeadlineExceeded<BrowserResult<BrowserSessionState>>(revision)
                    : Cancelled<BrowserResult<BrowserSessionState>>(revision);
            }
            catch (Exception exception)
            {
                return EngineFailure<BrowserResult<BrowserSessionState>>(
                    exception,
                    revision);
            }
        }
        finally
        {
            if (useIdempotencyGate)
            {
                _idempotentBrowserOperationGate.Release();
            }
        }
    }

    private bool TryGetBrowserPanel<T>(
        SessionId sessionId,
        out HostedSession session,
        out IBrowserPanelSession browser,
        out HostResult<T> failure)
    {
        if (!TryGetSession(sessionId, out session))
        {
            browser = null!;
            failure = NotFound<T>("session", 0);
            return false;
        }

        var snapshot = session.Snapshot();
        if (snapshot.Descriptor.Lifecycle is SessionLifecycle.Closed or SessionLifecycle.Failed)
        {
            browser = null!;
            failure = HostResult<T>.Fail(
                HostError.Create(
                    HostErrorCode.SessionClosed,
                    "The browser session is closed."),
                snapshot.Descriptor.Revision);
            return false;
        }

        if (session.Engine is not IBrowserPanelSession browserSession
            || session.Engine.Kind != PanelKind.Browser)
        {
            browser = null!;
            failure = Unsupported<T>(
                "The requested session does not expose browser operations.",
                snapshot.Descriptor.Revision);
            return false;
        }

        browser = browserSession;
        failure = null!;
        return true;
    }
}
