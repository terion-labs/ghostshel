using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    public async ValueTask<HostResult<SessionSnapshot>> EnsureDockerSessionAsync(
        EnsureDockerSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();
        var binding = request.Target.Binding;
        var fingerprint = Fingerprint(
            ApplicationOperations.DockerOpen,
            request.SessionId.Value,
            request.Owner.PanelId.Value,
            binding.ConnectionId.Value,
            binding.BindingRevision.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            binding.ConnectionKind.ToString());
        if (TryReplay(context, fingerprint, 0, out HostResult<SessionSnapshot>? replay))
        {
            return replay;
        }

        var invalid = ValidateContext<SessionSnapshot>(context, cancellationToken, 0);
        if (invalid is not null)
        {
            return invalid;
        }

        if (_dockerPanelFactory is null)
        {
            return Unsupported<SessionSnapshot>(
                "This session host has no Docker session factory.",
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
                        PanelKind.Docker)) is { } ownerFailure)
            {
                return ownerFailure;
            }

            if (TryGetSession(request.SessionId, out var existing))
            {
                var existingSnapshot = existing.Snapshot();
                if (existingSnapshot.Descriptor.Owner != request.Owner
                    || existing.Engine is not IDockerPanelSession docker
                    || existing.Engine.Kind != PanelKind.Docker
                    || docker.Binding != binding)
                {
                    return HostResult<SessionSnapshot>.Fail(
                        HostError.Create(
                            HostErrorCode.InvalidRequest,
                            "The requested session ID belongs to another panel or Docker binding."),
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
                            PanelKind.Docker,
                            request.SessionId)) is { } linkFailure)
                {
                    return linkFailure;
                }

                var existingResult = HostResult<SessionSnapshot>.Succeed(
                    existingSnapshot,
                    existingSnapshot.Descriptor.Revision);
                StoreReplay(context, fingerprint, existingResult);
                return existingResult;
            }

            IDockerPanelSession? engine = null;
            PanelSessionSnapshot engineSnapshot;
            using var operationCancellation = HostedOperationCancellation.Create(
                context,
                cancellationToken,
                _timeProvider);
            try
            {
                engine = await _dockerPanelFactory
                    .CreateAsync(
                        request.SessionId,
                        request.Target,
                        operationCancellation.Token)
                    .ConfigureAwait(false);
                if (engine.Id != request.SessionId
                    || engine.Kind != PanelKind.Docker
                    || engine.Binding != binding)
                {
                    await engine.DisposeAsync().ConfigureAwait(false);
                    return HostResult<SessionSnapshot>.Fail(
                        HostError.Create(
                            HostErrorCode.EngineFailed,
                            "The Docker engine returned an invalid session."),
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
                if (engine is not null)
                {
                    await engine.DisposeAsync().ConfigureAwait(false);
                }

                return operationCancellation.DeadlineElapsed
                    ? DeadlineExceeded<SessionSnapshot>(0)
                    : Cancelled<SessionSnapshot>(0);
            }
            catch (Exception)
            {
                if (engine is not null)
                {
                    await engine.DisposeAsync().ConfigureAwait(false);
                }

                return HostResult<SessionSnapshot>.Fail(
                    new HostError(
                        HostErrorCode.EngineFailed,
                        "docker_open_failed",
                        "The Docker session could not be opened."),
                    0);
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
                        PanelKind.Docker,
                        request.SessionId)) is { } rejected)
            {
                return await RemoveRejectedSessionAsync(hosted, rejected)
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
}
