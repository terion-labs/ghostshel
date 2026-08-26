using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Owns validation and application of the session host's authoritative runtime
/// workspace revisions. Feature surfaces may propose a graph, but only this
/// coordinator advances host cursors or accepts a receipt/projection.
/// </summary>
public sealed class RuntimeWorkspaceGraphCoordinator : IDisposable
{
    private readonly ISessionHostClient? _sessionClient;
    private readonly ClientId _clientId;
    private readonly WindowInstanceId _windowId;
    private readonly IUiThreadDispatcher? _uiThreadDispatcher;
    private readonly Func<RuntimeWorkspaceViewModel?> _currentWorkspace;
    private readonly Func<RuntimeWorkspaceViewModel, WorkspaceGraphStreamItem, bool>?
        _applyStreamItem;
    private readonly Action<string> _setError;
    private readonly Action _projectionApplied;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _watchGate = new();
    private readonly List<Task> _watchTasks = [];
    private CancellationTokenSource? _watchCancellation;
    private bool _disposed;

    public RuntimeWorkspaceGraphCoordinator(
        ISessionHostClient sessionClient,
        ClientId clientId,
        WindowInstanceId windowId,
        IUiThreadDispatcher uiThreadDispatcher,
        Func<RuntimeWorkspaceViewModel?> currentWorkspace,
        Func<RuntimeWorkspaceViewModel, WorkspaceGraphStreamItem, bool> applyStreamItem,
        Action<string> setError,
        Action projectionApplied)
    {
        _sessionClient = sessionClient
            ?? throw new ArgumentNullException(nameof(sessionClient));
        _clientId = clientId;
        _windowId = windowId;
        _uiThreadDispatcher = uiThreadDispatcher
            ?? throw new ArgumentNullException(nameof(uiThreadDispatcher));
        _currentWorkspace = currentWorkspace
            ?? throw new ArgumentNullException(nameof(currentWorkspace));
        _applyStreamItem = applyStreamItem
            ?? throw new ArgumentNullException(nameof(applyStreamItem));
        _setError = setError ?? throw new ArgumentNullException(nameof(setError));
        _projectionApplied = projectionApplied
            ?? throw new ArgumentNullException(nameof(projectionApplied));
    }

    internal RuntimeWorkspaceGraphCoordinator(
        WindowInstanceId windowId,
        Func<RuntimeWorkspaceViewModel?> currentWorkspace,
        Action<string> setError,
        Action projectionApplied)
    {
        _clientId = default;
        _windowId = windowId;
        _currentWorkspace = currentWorkspace
            ?? throw new ArgumentNullException(nameof(currentWorkspace));
        _setError = setError ?? throw new ArgumentNullException(nameof(setError));
        _projectionApplied = projectionApplied
            ?? throw new ArgumentNullException(nameof(projectionApplied));
    }

    public CancellationToken LifetimeToken => _lifetime.Token;

    public bool IsStopping => _lifetime.IsCancellationRequested;

    internal SemaphoreSlim SerializationGate => _gate;

    public async ValueTask<RuntimeWorkspaceGraphLease> EnterAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            await _gate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            return new RuntimeWorkspaceGraphLease(_gate, linkedCancellation);
        }
        catch
        {
            linkedCancellation.Dispose();
            throw;
        }
    }

    public void StartWatching(RuntimeWorkspaceViewModel runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sessionClient is null
            || _uiThreadDispatcher is null
            || _applyStreamItem is null)
        {
            throw new InvalidOperationException(
                "Workspace graph watching was not configured for this coordinator.");
        }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        lock (_watchGate)
        {
            if (IsStopping)
            {
                cancellation.Dispose();
                return;
            }

            _watchCancellation = cancellation;
            _watchTasks.Add(WatchAsync(
                runtime,
                runtime.HostSequence,
                cancellation.Token));
        }
    }

    public void StopWatching()
    {
        CancellationTokenSource? cancellation;
        lock (_watchGate)
        {
            cancellation = _watchCancellation;
            _watchCancellation = null;
        }

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    public async Task QuiesceAsync()
    {
        _lifetime.Cancel();
        StopWatching();
        Task[] watches;
        lock (_watchGate)
        {
            watches = [.. _watchTasks];
        }

        await Task.WhenAll(watches).ConfigureAwait(false);
    }

    public bool IsExpectedReceipt(
        HostResult<WorkspaceGraphSnapshot>.Success success,
        WorkspaceInstance proposal,
        long currentRevision,
        long currentSequence) =>
        success.Value.WindowId == _windowId
        && success.ResultingRevision == success.Value.Revision
        && success.ResultingRevision > currentRevision
        && success.Value.LastSequence > currentSequence
        && RuntimeWorkspaceGraphProjection.IntentMatches(
            proposal,
            success.Value.Workspace);

    public bool IsExpectedReconciledReceipt(
        HostResult<WorkspaceGraphSnapshot>.Success success,
        WorkspaceInstance proposal,
        long currentRevision,
        long currentSequence) =>
        success.Value.WindowId == _windowId
        && success.Value.Workspace.Id == proposal.Id
        && success.ResultingRevision == success.Value.Revision
        && success.ResultingRevision > currentRevision
        && success.Value.LastSequence > currentSequence
        && RuntimeWorkspaceGraphProjection.TopologyMatches(
            proposal,
            success.Value.Workspace);

    /// <summary>
    /// Applies a receipt whose window, cursor advance, and requested topology
    /// were validated against its submitted proposal.
    /// </summary>
    public bool TryApplyValidatedReceipt(
        RuntimeWorkspaceViewModel runtime,
        HostResult<WorkspaceGraphSnapshot>.Success success,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var receipt = success.Value;
        var currentIsAtLeastAsNew =
            runtime.HostRevision >= receipt.Revision
            && runtime.HostSequence >= receipt.LastSequence;
        if (currentIsAtLeastAsNew)
        {
            if (RuntimeWorkspaceGraphProjection.TopologyMatches(
                    RuntimeWorkspaceGraphProjection.Capture(runtime),
                    receipt.Workspace))
            {
                return true;
            }

            _setError($"The runtime workspace changed while applying {operation}.");
            return false;
        }

        if (receipt.Revision <= runtime.HostRevision
            || receipt.LastSequence <= runtime.HostSequence)
        {
            _setError($"The session host returned an invalid {operation} cursor.");
            return false;
        }

        try
        {
            runtime.ApplyHostProjection(
                receipt.Workspace,
                receipt.Revision,
                receipt.LastSequence);
            return true;
        }
        catch (InvalidOperationException)
        {
            _setError($"The session host returned a different {operation} graph.");
            return false;
        }
    }

    public bool TryApplyResult(
        RuntimeWorkspaceViewModel expectedWorkspace,
        HostResult<WorkspaceGraphSnapshot> result,
        string operation,
        Func<WorkspaceInstance, bool> requestedFocusMatches)
    {
        ArgumentNullException.ThrowIfNull(expectedWorkspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(requestedFocusMatches);
        if (!ReferenceEquals(_currentWorkspace(), expectedWorkspace))
        {
            return false;
        }

        if (result is HostResult<WorkspaceGraphSnapshot>.Failure failure)
        {
            _setError(
                $"The session host rejected {operation} "
                + $"({failure.Error.StableCode}): {failure.Error.Message}");
            return false;
        }

        var success = (HostResult<WorkspaceGraphSnapshot>.Success)result;
        var currentProjection = RuntimeWorkspaceGraphProjection.Capture(expectedWorkspace);
        var sameCursor =
            success.Value.Revision == expectedWorkspace.HostRevision
            && success.Value.LastSequence == expectedWorkspace.HostSequence;
        var advancedCursor =
            success.Value.Revision > expectedWorkspace.HostRevision
            && success.Value.LastSequence > expectedWorkspace.HostSequence;
        if (success.Value.WindowId != _windowId
            || success.Value.Workspace.Id != expectedWorkspace.Id
            || success.ResultingRevision != success.Value.Revision
            || !requestedFocusMatches(success.Value.Workspace)
            || !(advancedCursor
                || sameCursor && requestedFocusMatches(currentProjection))
            || !RuntimeWorkspaceGraphProjection.TopologyMatches(
                currentProjection,
                success.Value.Workspace))
        {
            _setError($"The session host returned an invalid {operation} receipt.");
            return false;
        }

        try
        {
            expectedWorkspace.ApplyHostProjection(
                success.Value.Workspace,
                success.Value.Revision,
                success.Value.LastSequence);
        }
        catch (InvalidOperationException)
        {
            _setError("The session host returned a different runtime workspace graph.");
            return false;
        }

        _projectionApplied();
        return true;
    }

    public bool TryApplyProjection(
        RuntimeWorkspaceViewModel runtime,
        WindowInstanceId windowId,
        WorkspaceInstance projection,
        long revision,
        long sequence,
        string source)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!ReferenceEquals(_currentWorkspace(), runtime))
        {
            return false;
        }

        if (windowId != _windowId
            || projection.Id != runtime.Id
            || revision < runtime.HostRevision
            || sequence < runtime.HostSequence
            || !RuntimeWorkspaceGraphProjection.TopologyMatches(
                RuntimeWorkspaceGraphProjection.Capture(runtime),
                projection))
        {
            _setError($"The session host returned an invalid {source}.");
            return false;
        }

        try
        {
            runtime.ApplyHostProjection(projection, revision, sequence);
        }
        catch (InvalidOperationException)
        {
            _setError($"The session host returned a different {source} graph.");
            return false;
        }

        _projectionApplied();
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        StopWatching();
        _lifetime.Dispose();
    }

    private async Task WatchAsync(
        RuntimeWorkspaceViewModel runtime,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        var sessionClient = _sessionClient
            ?? throw new InvalidOperationException("Workspace graph watching is unavailable.");
        var dispatcher = _uiThreadDispatcher
            ?? throw new InvalidOperationException("Workspace graph watching is unavailable.");
        var applyStreamItem = _applyStreamItem
            ?? throw new InvalidOperationException("Workspace graph watching is unavailable.");
        try
        {
            var cursor = afterSequence;
            while (!cancellationToken.IsCancellationRequested)
            {
                var restartAfterResynchronization = false;
                await foreach (var item in sessionClient.WatchWorkspaceGraphAsync(
                    new WatchWorkspaceGraphRequest(runtime.Id, cursor),
                    OperationContext.ForHuman(_clientId),
                    cancellationToken).ConfigureAwait(false))
                {
                    using var lease = await EnterAsync(cancellationToken);
                    if (!ReferenceEquals(_currentWorkspace(), runtime))
                    {
                        return;
                    }

                    var accepted = false;
                    await dispatcher.InvokeAsync(
                        () => accepted = applyStreamItem(runtime, item),
                        cancellationToken);
                    if (!accepted)
                    {
                        return;
                    }

                    cursor = runtime.HostSequence;
                    if (item is WorkspaceGraphStreamItem.ResynchronizationRequired)
                    {
                        restartAfterResynchronization = true;
                        break;
                    }
                }

                if (!restartAfterResynchronization)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (NotSupportedException)
        {
            // Compatibility clients can omit workspace watches. Mutation
            // receipts still keep those clients coherent.
        }
        catch (Exception)
        {
            try
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_currentWorkspace(), runtime))
                    {
                        _setError("Live workspace updates are temporarily unavailable.");
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Presentation teardown won the race with the best-effort error.
            }
        }
    }
}

public sealed class RuntimeWorkspaceGraphLease : IDisposable
{
    private readonly SemaphoreSlim _gate;
    private CancellationTokenSource? _linkedCancellation;

    internal RuntimeWorkspaceGraphLease(
        SemaphoreSlim gate,
        CancellationTokenSource linkedCancellation)
    {
        _gate = gate;
        _linkedCancellation = linkedCancellation;
    }

    public CancellationToken Token =>
        _linkedCancellation?.Token
        ?? throw new ObjectDisposedException(nameof(RuntimeWorkspaceGraphLease));

    public void Dispose()
    {
        var cancellation = Interlocked.Exchange(ref _linkedCancellation, null);
        if (cancellation is null)
        {
            return;
        }

        _gate.Release();
        cancellation.Dispose();
    }
}
