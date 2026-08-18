using System.Runtime.CompilerServices;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    public async ValueTask<HostResult<WorkspaceGraphSnapshot>> RegisterWorkspaceGraphAsync(
        RegisterWorkspaceGraphRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();
        var revision = _workspaceGraphs.CurrentRegistrationRevision(
            request.WindowId,
            request.Workspace.Id);
        var invalid = ValidateContext<WorkspaceGraphSnapshot>(
            context,
            cancellationToken,
            revision);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<WorkspaceGraphSnapshot>(revision);
        }

        try
        {
            ThrowIfDisposed();
            revision = _workspaceGraphs.CurrentRegistrationRevision(
                request.WindowId,
                request.Workspace.Id);
            invalid = ValidateContext<WorkspaceGraphSnapshot>(
                context,
                cancellationToken,
                revision);
            if (invalid is not null)
            {
                return invalid;
            }

            HostedSession[] sessions;
            lock (_gate)
            {
                sessions = [.. _sessions.Values];
            }

            var liveSessions = sessions
                .Select(session => new LiveWorkspaceSession(
                    session.Snapshot().Descriptor,
                    session.Role))
                .Where(session => session.Descriptor.Lifecycle is
                    SessionLifecycle.Starting or
                    SessionLifecycle.Active or
                    SessionLifecycle.Closing)
                .ToArray();
            return _workspaceGraphs.RegisterOrReplace(
                request,
                context.Actor.ClientId,
                context.ExpectedRevision,
                liveSessions);
        }
        finally
        {
            _sessionGraphGate.Release();
        }
    }

    public ValueTask<HostResult<WorkspaceGraphSnapshot>> GetWorkspaceGraphAsync(
        WorkspaceInstanceId workspaceId,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();
        var revision = _workspaceGraphs.CurrentRevision(workspaceId);
        var invalid = ValidateContext<WorkspaceGraphSnapshot>(
            context,
            cancellationToken,
            revision);
        return ValueTask.FromResult(invalid ?? _workspaceGraphs.Get(workspaceId));
    }

    public ValueTask<HostResult<Unit>> UnregisterWorkspaceGraphAsync(
        UnregisterWorkspaceGraphRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();
        var revision = _workspaceGraphs.CurrentRevision(request.WorkspaceId);
        var invalid = ValidateContext<Unit>(context, cancellationToken, revision);
        return ValueTask.FromResult(
            invalid ?? _workspaceGraphs.Unregister(
                request,
                context.Actor.ClientId,
                context.ExpectedRevision));
    }

    public ValueTask<HostResult<WorkspaceGraphSnapshot>> ActivateWorkspaceTabAsync(
        ActivateWorkspaceTabRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();
        var revision = _workspaceGraphs.CurrentRevision(request.WorkspaceId);
        var invalid = ValidateContext<WorkspaceGraphSnapshot>(
            context,
            cancellationToken,
            revision);
        return ValueTask.FromResult(
            invalid ?? _workspaceGraphs.ActivateTab(request, context.ExpectedRevision));
    }

    public ValueTask<HostResult<WorkspaceGraphSnapshot>> ActivateWorkspacePanelAsync(
        ActivateWorkspacePanelRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();
        var revision = _workspaceGraphs.CurrentRevision(request.WorkspaceId);
        var invalid = ValidateContext<WorkspaceGraphSnapshot>(
            context,
            cancellationToken,
            revision);
        return ValueTask.FromResult(
            invalid ?? _workspaceGraphs.ActivatePanel(request, context.ExpectedRevision));
    }

    public async IAsyncEnumerable<WorkspaceGraphStreamItem> WatchWorkspaceGraphAsync(
        WatchWorkspaceGraphRequest request,
        OperationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();
        if (ValidateContext<Unit>(context, cancellationToken, 0) is not null
            || !_workspaceGraphs.TryGetWatchSource(request.WorkspaceId, out var graph))
        {
            yield break;
        }

        await foreach (var item in graph
            .WatchAsync(request.AfterSequence, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return item;
        }
    }
}
