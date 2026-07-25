using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    public ValueTask<HostResult<SessionSnapshot>> EnsureStatisticsSessionAsync(
        EnsureStatisticsSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return EnsureMonitorSessionAsync(
            request.SessionId,
            request.Owner,
            request.Title,
            PanelKind.Statistics,
            ApplicationOperations.StatisticsOpen,
            cancellationToken => CreateStatisticsEngineAsync(
                request.SessionId,
                cancellationToken),
            context,
            cancellationToken);
    }

    public ValueTask<HostResult<SessionSnapshot>> EnsureProcessMonitorSessionAsync(
        EnsureProcessMonitorSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return EnsureMonitorSessionAsync(
            request.SessionId,
            request.Owner,
            request.Title,
            PanelKind.ProcessMonitor,
            ApplicationOperations.ProcessesOpen,
            cancellationToken => CreateProcessMonitorEngineAsync(
                request.SessionId,
                cancellationToken),
            context,
            cancellationToken);
    }

    public ValueTask<HostResult<MonitorPanelResult<SystemStatisticsSnapshot>>> ReadStatisticsAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteMonitorOperationAsync<
            IStatisticsPanelSession,
            SystemStatisticsSnapshot>(
            sessionId,
            context,
            cancellationToken,
            "statistics",
            static (session, token) => session.ReadStatisticsAsync(token));
    }

    public ValueTask<HostResult<MonitorPanelResult<ProcessMonitorSnapshot>>> ListProcessesAsync(
        ProcessMonitorHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Query);
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteMonitorOperationAsync<
            IProcessMonitorPanelSession,
            ProcessMonitorSnapshot>(
            request.SessionId,
            context,
            cancellationToken,
            "process-monitor",
            (session, token) => session.ListProcessesAsync(request.Query, token));
    }

    private async ValueTask<HostResult<SessionSnapshot>> EnsureMonitorSessionAsync(
        SessionId sessionId,
        SessionOwner owner,
        string title,
        PanelKind kind,
        string operationName,
        Func<CancellationToken, ValueTask<IPanelSession>> createEngine,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var fingerprint = Fingerprint(
            operationName,
            sessionId.Value,
            owner.PanelId.Value);
        if (TryReplay(context, fingerprint, 0, out HostResult<SessionSnapshot>? replay))
        {
            return replay;
        }

        var invalid = ValidateContext<SessionSnapshot>(context, cancellationToken, 0);
        if (invalid is not null)
        {
            return invalid;
        }

        if (_systemMonitorFactory is null)
        {
            return Unsupported<SessionSnapshot>(
                "This session host has no system-monitor session factory.",
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
                    _workspaceGraphs.ValidateSessionOwner(owner, kind)) is { } ownerFailure)
            {
                return ownerFailure;
            }

            if (TryGetSession(sessionId, out var existing))
            {
                var existingSnapshot = existing.Snapshot();
                if (existingSnapshot.Descriptor.Owner != owner
                    || existing.Engine.Kind != kind)
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
                        _workspaceGraphs.LinkSession(owner, kind, sessionId)) is { } linkFailure)
                {
                    return linkFailure;
                }

                var existingResult = HostResult<SessionSnapshot>.Succeed(
                    existingSnapshot,
                    existingSnapshot.Descriptor.Revision);
                StoreReplay(context, fingerprint, existingResult);
                return existingResult;
            }

            IPanelSession? createdEngine = null;
            PanelSessionSnapshot engineSnapshot;
            using var operationCancellation = MonitorOperationCancellation.Create(
                context,
                cancellationToken,
                _timeProvider);
            try
            {
                createdEngine = await createEngine(operationCancellation.Token).ConfigureAwait(false);
                if (createdEngine.Kind != kind || createdEngine.Id != sessionId)
                {
                    await createdEngine.DisposeAsync().ConfigureAwait(false);
                    return HostResult<SessionSnapshot>.Fail(
                        HostError.Create(
                            HostErrorCode.EngineFailed,
                            "The monitoring engine returned an invalid session."),
                        0);
                }

                engineSnapshot = await createdEngine
                    .SnapshotAsync(operationCancellation.Token)
                    .ConfigureAwait(false);
                if (operationCancellation.DeadlineElapsed)
                {
                    await createdEngine.DisposeAsync().ConfigureAwait(false);
                    return DeadlineExceeded<SessionSnapshot>(0);
                }
            }
            catch (OperationCanceledException)
            {
                await DisposeRejectedMonitorAsync(createdEngine).ConfigureAwait(false);
                return operationCancellation.DeadlineElapsed
                    ? DeadlineExceeded<SessionSnapshot>(0)
                    : Cancelled<SessionSnapshot>(0);
            }
            catch (Exception)
            {
                await DisposeRejectedMonitorAsync(createdEngine).ConfigureAwait(false);
                return MonitoringEngineFailure<SessionSnapshot>(0);
            }

            var engine = createdEngine;
            var hosted = new HostedSession(
                engine,
                owner,
                title,
                engineSnapshot,
                _eventRetention,
                _timeProvider);
            lock (_gate)
            {
                _sessions.Add(sessionId, hosted);
            }

            if (WorkspaceGraphFailure<SessionSnapshot>(
                    _workspaceGraphs.LinkSession(owner, kind, sessionId)) is { } rejected)
            {
                return await RemoveRejectedSessionAsync(hosted, rejected).ConfigureAwait(false);
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

    private async ValueTask<HostResult<MonitorPanelResult<T>>> ExecuteMonitorOperationAsync<
        TSession,
        T>(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken,
        string monitorName,
        Func<TSession, CancellationToken, ValueTask<MonitorPanelResult<T>>> operation)
        where TSession : class, IPanelSession
    {
        if (!TryGetMonitorSession(
                sessionId,
                monitorName,
                out HostedSession hosted,
                out TSession monitor,
                out HostResult<MonitorPanelResult<T>> failure))
        {
            return failure;
        }

        var revision = hosted.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<MonitorPanelResult<T>>(
            context,
            cancellationToken,
            revision);
        if (invalid is not null)
        {
            return invalid;
        }

        if (RevisionConflict(
                context,
                hosted,
                out HostResult<MonitorPanelResult<T>>? conflict))
        {
            return conflict;
        }

        using var operationCancellation = MonitorOperationCancellation.Create(
            context,
            cancellationToken,
            _timeProvider);
        try
        {
            var monitorResult = await operation(monitor, operationCancellation.Token)
                .ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return Cancelled<MonitorPanelResult<T>>(revision);
            }

            if (operationCancellation.DeadlineElapsed)
            {
                return DeadlineExceeded<MonitorPanelResult<T>>(revision);
            }

            return HostResult<MonitorPanelResult<T>>.Succeed(monitorResult, revision);
        }
        catch (OperationCanceledException)
        {
            return operationCancellation.DeadlineElapsed
                ? DeadlineExceeded<MonitorPanelResult<T>>(revision)
                : Cancelled<MonitorPanelResult<T>>(revision);
        }
        catch (Exception)
        {
            return MonitoringEngineFailure<MonitorPanelResult<T>>(revision);
        }
    }

    private bool TryGetMonitorSession<TSession, TResult>(
        SessionId sessionId,
        string monitorName,
        out HostedSession hosted,
        out TSession monitor,
        out HostResult<TResult> failure)
        where TSession : class, IPanelSession
    {
        if (!TryGetSession(sessionId, out hosted))
        {
            monitor = null!;
            failure = NotFound<TResult>("session", 0);
            return false;
        }

        var snapshot = hosted.Snapshot();
        if (snapshot.Descriptor.Lifecycle is SessionLifecycle.Closed or SessionLifecycle.Failed)
        {
            monitor = null!;
            failure = ClosedSession<TResult>(snapshot.Descriptor.Revision);
            return false;
        }

        if (hosted.Engine is not TSession typed)
        {
            monitor = null!;
            failure = Unsupported<TResult>(
                $"The requested session does not expose {monitorName} operations.",
                snapshot.Descriptor.Revision);
            return false;
        }

        monitor = typed;
        failure = null!;
        return true;
    }

    private async ValueTask<IPanelSession> CreateStatisticsEngineAsync(
        SessionId sessionId,
        CancellationToken cancellationToken) =>
        await _systemMonitorFactory!
            .CreateStatisticsAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<IPanelSession> CreateProcessMonitorEngineAsync(
        SessionId sessionId,
        CancellationToken cancellationToken) =>
        await _systemMonitorFactory!
            .CreateProcessMonitorAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

    private static HostResult<T> MonitoringEngineFailure<T>(long revision) =>
        HostResult<T>.Fail(
            HostError.Create(
                HostErrorCode.EngineFailed,
                "The system monitor could not complete the operation."),
            revision);

    private static async ValueTask DisposeRejectedMonitorAsync(IPanelSession? engine)
    {
        if (engine is null)
        {
            return;
        }

        try
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original startup failure remains the stable result. Disposal is best-effort
            // because no hosted reference survives this path.
        }
    }

    private sealed class MonitorOperationCancellation : IDisposable
    {
        private readonly CancellationTokenSource? _deadline;
        private readonly CancellationTokenSource? _linked;

        private MonitorOperationCancellation(
            CancellationToken token,
            CancellationTokenSource? deadline,
            CancellationTokenSource? linked)
        {
            Token = token;
            _deadline = deadline;
            _linked = linked;
        }

        public CancellationToken Token { get; }

        public bool DeadlineElapsed => _deadline?.IsCancellationRequested == true;

        public static MonitorOperationCancellation Create(
            OperationContext context,
            CancellationToken cancellationToken,
            TimeProvider timeProvider)
        {
            if (context.DeadlineUtc is not { } deadlineUtc)
            {
                return new MonitorOperationCancellation(cancellationToken, null, null);
            }

            var remaining = deadlineUtc - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                var elapsed = new CancellationTokenSource();
                elapsed.Cancel();
                return new MonitorOperationCancellation(elapsed.Token, elapsed, null);
            }

            var deadline = new CancellationTokenSource(remaining, timeProvider);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);
            return new MonitorOperationCancellation(linked.Token, deadline, linked);
        }

        public void Dispose()
        {
            _linked?.Dispose();
            _deadline?.Dispose();
        }
    }
}
