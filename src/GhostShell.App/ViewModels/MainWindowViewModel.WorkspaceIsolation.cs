using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly IWorkspaceIsolationProvider? _workspaceIsolationProvider;
    private readonly IWorkspaceIsolationRuntimeInstaller?
        _workspaceIsolationRuntimeInstaller;
    private readonly object _workspaceIsolationGate = new();
    private readonly Dictionary<WorkspaceInstanceId, WorkspaceIsolationLease>
        _workspaceIsolationLeases = [];
    private readonly Dictionary<Guid, WorkspaceIsolationBinding>
        _pendingWorkspaceIsolationBindings = [];
    private readonly Dictionary<WorkspaceInstanceId, Task<WorkspaceIsolationError?>>
        _workspaceIsolationCleanupTasks = [];
    private readonly Dictionary<Guid, Task> _workspaceIsolationPreparationTasks = [];
    private readonly Dictionary<Guid, Task> _workspaceActivationTasks = [];
    private readonly HashSet<WorkspaceInstanceId> _pendingWorkspaceGraphRollbacks = [];
    private Guid? _workspaceIsolationStartupId;
    private WorkspaceId? _workspaceIsolationStartingWorkspaceId;
    private string? _workspaceIsolationStartingWorkspaceName;
    private string? _workspaceIsolationStartingStatus;
    private bool _windowClosePending;

    public bool IsWorkspaceIsolationStarting =>
        _workspaceIsolationStartupId is not null;

    public string WorkspaceIsolationStartingHeading =>
        _workspaceIsolationStartingStatus
        ?? $"Starting {WorkspaceIsolationRuntimeDisplayName}…";

    public string WorkspaceIsolationStartingBody =>
        _workspaceIsolationStartingWorkspaceName is { } name
            ? $"Preparing the persistent isolate for “{name}”."
            : "Preparing the workspace's persistent isolate.";

    private string WorkspaceIsolationRuntimeDisplayName =>
        _workspaceIsolationRuntimeInstaller?.RuntimeDisplayName
        ?? (_workspaceIsolationProvider?.Kind == WorkspaceIsolationProviderKind.AppleContainer
            ? "Apple container"
            : "workspace isolation runtime");

    public bool CanInstallWorkspaceIsolationRuntime =>
        _workspaceIsolationProvider is null
        && _workspaceIsolationRuntimeInstaller is not null;

    public WorkspaceIsolationRuntimeInstallResult InstallWorkspaceIsolationRuntime()
    {
        ClearError();
        if (!CanInstallWorkspaceIsolationRuntime)
        {
            var unavailable = WorkspaceIsolationRuntimeInstallResult.Failure(
                "No workspace isolation runtime installer is available on this host.");
            SetError(unavailable.Error!);
            return unavailable;
        }

        var result = _workspaceIsolationRuntimeInstaller!.BeginInstallation();
        if (!result.Started)
        {
            SetError(result.Error!);
        }

        return result;
    }

    private void BeginWorkspaceIsolationStartup(
        WorkspaceDefinition workspace,
        Guid activationId)
    {
        _workspaceIsolationStartupId = activationId;
        _workspaceIsolationStartingWorkspaceId = workspace.Id;
        _workspaceIsolationStartingWorkspaceName = workspace.Name;
        _workspaceIsolationStartingStatus = null;
        OnPropertyChanged(nameof(IsWorkspaceIsolationStarting));
        OnPropertyChanged(nameof(WorkspaceIsolationStartingHeading));
        OnPropertyChanged(nameof(WorkspaceIsolationStartingBody));
        OnPropertyChanged(nameof(IsWorkspaceCanvasVisible));
        RefreshWorkspaceRuntimeFlags();

        // Navigation changes before runtime preparation is awaited. The
        // requested workspace is therefore the foreground surface while its
        // platform runtime starts, rather than an error appearing over the
        // workspace the user was leaving.
        Route = ShellRoute.Workspace;
    }

    private void CompleteWorkspaceIsolationStartup(Guid activationId)
    {
        if (_workspaceIsolationStartupId != activationId)
        {
            return;
        }

        _workspaceIsolationStartupId = null;
        _workspaceIsolationStartingWorkspaceId = null;
        _workspaceIsolationStartingWorkspaceName = null;
        _workspaceIsolationStartingStatus = null;
        OnPropertyChanged(nameof(IsWorkspaceIsolationStarting));
        OnPropertyChanged(nameof(WorkspaceIsolationStartingHeading));
        OnPropertyChanged(nameof(WorkspaceIsolationStartingBody));
        OnPropertyChanged(nameof(IsWorkspaceCanvasVisible));
        RefreshWorkspaceRuntimeFlags();
    }

    private void ReportWorkspaceIsolationProgress(WorkspaceIsolationProgress progress)
    {
        if (_workspaceIsolationStartupId is null
            || string.Equals(
                _workspaceIsolationStartingStatus,
                progress.Status,
                StringComparison.Ordinal))
        {
            return;
        }

        _workspaceIsolationStartingStatus = progress.Status;
        OnPropertyChanged(nameof(WorkspaceIsolationStartingHeading));
    }

    private ValueTask<WorkspaceIsolationPreparation> PrepareWorkspaceIsolationAsync(
        WorkspaceDefinition workspace,
        CancellationToken cancellationToken)
    {
        if (_shutdownStarted || _runtimeGraphLifetime.IsCancellationRequested)
        {
            SetError("This window is closing, so the workspace cannot be opened.");
            return ValueTask.FromResult(WorkspaceIsolationPreparation.Failed);
        }

        if (!workspace.IsIsolated)
        {
            return ValueTask.FromResult(WorkspaceIsolationPreparation.Host);
        }

        if (_workspaceIsolationProvider is null)
        {
            SetError("Workspace isolation is not available on this platform.");
            return ValueTask.FromResult(WorkspaceIsolationPreparation.Failed);
        }

        var preparationId = Guid.NewGuid();
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_workspaceIsolationGate)
        {
            if (_shutdownStarted || _runtimeGraphLifetime.IsCancellationRequested)
            {
                SetError("This window is closing, so the workspace cannot be opened.");
                return ValueTask.FromResult(WorkspaceIsolationPreparation.Failed);
            }

            _workspaceIsolationPreparationTasks.Add(preparationId, completion.Task);
        }

        return PrepareTrackedWorkspaceIsolationAsync(
            workspace,
            preparationId,
            completion,
            cancellationToken);
    }

    private async ValueTask<WorkspaceIsolationPreparation>
        PrepareTrackedWorkspaceIsolationAsync(
            WorkspaceDefinition workspace,
            Guid preparationId,
            TaskCompletionSource completion,
            CancellationToken cancellationToken)
    {
        try
        {
            return await PrepareWorkspaceIsolationCoreAsync(workspace, cancellationToken);
        }
        catch (OperationCanceledException) when (_runtimeGraphLifetime.IsCancellationRequested)
        {
            return WorkspaceIsolationPreparation.Failed;
        }
        finally
        {
            lock (_workspaceIsolationGate)
            {
                _workspaceIsolationPreparationTasks.Remove(preparationId);
                completion.TrySetResult();
            }
        }
    }

    private async ValueTask<WorkspaceIsolationPreparation>
        PrepareWorkspaceIsolationCoreAsync(
            WorkspaceDefinition workspace,
            CancellationToken cancellationToken)
    {
        var provider = _workspaceIsolationProvider
            ?? throw new InvalidOperationException(
                "Workspace isolation preparation requires a platform provider.");
        var result = await provider.PrepareAsync(
            new WorkspaceIsolationPrepareRequest(
                workspace.Id,
                IsolationMountsOf(workspace),
                workspace.IsolationImageReference),
            new Progress<WorkspaceIsolationProgress>(ReportWorkspaceIsolationProgress),
            cancellationToken);
        if (result is WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure failure)
        {
            if (failure.CleanupValue is { } cleanupBinding)
            {
                lock (_workspaceIsolationGate)
                {
                    _pendingWorkspaceIsolationBindings.TryAdd(
                        cleanupBinding.LeaseId,
                        cleanupBinding);
                }

                if (await ReleasePreparedWorkspaceIsolationAsync(
                        cleanupBinding,
                        CancellationToken.None) is not null)
                {
                    SecretSafeDiagnosticProjection.WriteStandardError(
                        "workspace-isolation.failed-prepare-stop.failed",
                        SecretSafeDiagnosticKind.Unexpected);
                }
            }

            SetError(failure.Error.Message);
            return WorkspaceIsolationPreparation.Failed;
        }

        var binding = ((WorkspaceIsolationResult<WorkspaceIsolationBinding>.Success)result).Value;
        if (binding.WorkspaceId != workspace.Id
            || binding.Provider != provider.Kind)
        {
            lock (_workspaceIsolationGate)
            {
                _pendingWorkspaceIsolationBindings.TryAdd(binding.LeaseId, binding);
            }

            var cleanupError = await ReleasePreparedWorkspaceIsolationAsync(
                binding,
                CancellationToken.None);
            SetError("The workspace isolation runtime returned an invalid workspace binding.");
            if (cleanupError is not null)
            {
                SecretSafeDiagnosticProjection.WriteStandardError(
                    "workspace-isolation.invalid-binding-stop.failed",
                    SecretSafeDiagnosticKind.Unexpected);
            }

            return WorkspaceIsolationPreparation.Failed;
        }

        lock (_workspaceIsolationGate)
        {
            _pendingWorkspaceIsolationBindings.Add(binding.LeaseId, binding);
        }

        return new WorkspaceIsolationPreparation(true, binding);
    }

    private ValueTask<WorkspaceIsolationPreparation> PrepareRecoveredWorkspaceIsolationAsync(
        RuntimeWorkspaceRecoveryPayload recovered,
        CancellationToken cancellationToken)
    {
        if (recovered.HistorySource?.ToHistorySource() is not { } source
            || source.SourceDefinition.Kind != WorkspaceDefinition.Kind)
        {
            if (recovered.IsIsolated)
            {
                SetError(
                    "The recovered isolated workspace has no saved workspace identity. "
                    + "Recovery was stopped to prevent host execution.");
                return ValueTask.FromResult(WorkspaceIsolationPreparation.Failed);
            }

            return ValueTask.FromResult(WorkspaceIsolationPreparation.Host);
        }

        var workspace = _catalog.Snapshot.Workspaces
            .Select(item => item.Value)
            .FirstOrDefault(candidate => candidate.Key == source.SourceDefinition);
        if (workspace is null)
        {
            if (recovered.IsIsolated)
            {
                SetError(
                    "The saved definition for this recovered isolated workspace is unavailable. "
                    + "Recovery was stopped to prevent host execution.");
                return ValueTask.FromResult(WorkspaceIsolationPreparation.Failed);
            }

            return ValueTask.FromResult(WorkspaceIsolationPreparation.Host);
        }

        if (workspace.IsIsolated != recovered.IsIsolated)
        {
            SetError(
                "Workspace isolation changed since this recovery snapshot was written. "
                + "Discard the snapshot and reopen the workspace.");
            return ValueTask.FromResult(WorkspaceIsolationPreparation.Failed);
        }

        if (workspace.IsIsolated
            && !IsolationMountsOf(workspace).SequenceEqual(
                recovered.IsolationMounts?.Select(mount => mount.ToMount()) ?? []))
        {
            SetError(
                "Workspace mounts changed since this recovery snapshot was written. "
                + "Discard the snapshot and reopen the workspace.");
            return ValueTask.FromResult(WorkspaceIsolationPreparation.Failed);
        }

        if (workspace.IsIsolated
            && !string.Equals(
                workspace.IsolationImageReference,
                recovered.IsolationImageReference,
                StringComparison.Ordinal))
        {
            SetError(
                "The workspace runtime image changed since this recovery snapshot was written. "
                + "Discard the snapshot and reopen the workspace.");
            return ValueTask.FromResult(WorkspaceIsolationPreparation.Failed);
        }

        return PrepareWorkspaceIsolationAsync(workspace, cancellationToken);
    }

    private void RegisterWorkspaceConnectionRuntime(RuntimeWorkspaceViewModel workspace)
    {
        if (workspace.IsolationBinding is not { } binding)
        {
            return;
        }

        var provider = _workspaceIsolationProvider
            ?? throw new InvalidOperationException(
                "An isolated runtime workspace requires an isolation provider.");
        var lease = new WorkspaceIsolationLease(
            binding,
            new WorkspaceIsolatedConnectionRuntime(
                _connectionRuntime,
                provider,
                binding));
        lock (_workspaceIsolationGate)
        {
            if (!_pendingWorkspaceIsolationBindings.ContainsKey(binding.LeaseId))
            {
                if (_shutdownStarted || _runtimeGraphLifetime.IsCancellationRequested)
                {
                    throw new OperationCanceledException(_runtimeGraphLifetime.Token);
                }

                throw new InvalidOperationException(
                    "The workspace isolation binding is not owned by this window.");
            }

            _workspaceIsolationLeases.Add(workspace.Id, lease);
            _pendingWorkspaceIsolationBindings.Remove(binding.LeaseId);
        }
    }

    private IConnectionRuntime ConnectionRuntimeFor(WorkspaceInstanceId workspaceId)
    {
        var workspace = _openWorkspaces.FirstOrDefault(candidate => candidate.Id == workspaceId)
            ?? (RuntimeWorkspace?.Id == workspaceId ? RuntimeWorkspace : null);
        if (workspace is not null && !IsolationIntentMatches(workspace))
        {
            return WorkspaceIsolationUnavailableConnectionRuntime.ScopeChanged;
        }

        lock (_workspaceIsolationGate)
        {
            if (_workspaceIsolationLeases.TryGetValue(workspaceId, out var lease))
            {
                return lease.ConnectionRuntime;
            }
        }

        return workspace?.IsolationBinding is null
            ? _connectionRuntime
            : WorkspaceIsolationUnavailableConnectionRuntime.BindingMissing;
    }

    private bool IsolationIntentMatches(RuntimeWorkspaceViewModel runtime)
    {
        if (!_runtimeSources.TryGetValue(runtime.Id, out var source)
            || source.SourceDefinition.Kind != WorkspaceDefinition.Kind
            || _catalog.Snapshot.Workspaces.FirstOrDefault(
                item => item.Value.Key == source.SourceDefinition) is not { } stored)
        {
            return true;
        }

        if (stored.Value.IsIsolated != (runtime.IsolationBinding is not null))
        {
            return false;
        }

        return runtime.IsolationBinding is not { } binding
            || (IsolationMountsOf(stored.Value).SequenceEqual(binding.Mounts)
                && string.Equals(
                    stored.Value.IsolationImageReference,
                    binding.ImageReference,
                    StringComparison.Ordinal));
    }

    private bool WorkspaceIsolationConfigurationMatches(
        WorkspaceDefinition expected)
    {
        var current = _catalog.Snapshot.Workspaces
            .FirstOrDefault(item => item.Value.Key == expected.Key);
        return current is not null
            && current.Value.IsIsolated == expected.IsIsolated
            && IsolationMountsOf(current.Value).SequenceEqual(
                IsolationMountsOf(expected))
            && string.Equals(
                current.Value.IsolationImageReference,
                expected.IsolationImageReference,
                StringComparison.Ordinal);
    }

    private bool RecoveredWorkspaceIsolationConfigurationMatches(
        RuntimeWorkspaceRecoveryPayload recovered)
    {
        if (recovered.HistorySource?.ToHistorySource() is not { } source
            || source.SourceDefinition.Kind != WorkspaceDefinition.Kind)
        {
            return !recovered.IsIsolated;
        }

        var current = _catalog.Snapshot.Workspaces
            .FirstOrDefault(item => item.Value.Key == source.SourceDefinition);
        return current is not null
            && current.Value.IsIsolated == recovered.IsIsolated
            && (!recovered.IsIsolated
                || (IsolationMountsOf(current.Value).SequenceEqual(
                        recovered.IsolationMounts?.Select(mount => mount.ToMount()) ?? [])
                    && string.Equals(
                        current.Value.IsolationImageReference,
                        recovered.IsolationImageReference,
                        StringComparison.Ordinal)));
    }

    private bool IsolationBadgeFor(WorkspaceDefinition definition) =>
        FindOpenWorkspace(definition.Key) is { } runtime
            ? runtime.IsolationBinding is not null
            : definition.IsIsolated;

    private static IReadOnlyList<WorkspaceIsolationMount> IsolationMountsOf(
        WorkspaceDefinition workspace) =>
        [.. workspace.IsolationMounts.Select(mount => new WorkspaceIsolationMount(
            mount.HostPath,
            mount.GuestPath,
            mount.IsReadOnly))];

    private async ValueTask<WorkspaceIsolationError?> ReleaseWorkspaceIsolationAsync(
        RuntimeWorkspaceViewModel workspace,
        CancellationToken cancellationToken) =>
        await ReleaseWorkspaceIsolationAsync(workspace.Id, cancellationToken);

    /// <summary>
    /// Starts the asynchronous part of closing an isolated workspace from sync-only
    /// graph and presentation callbacks. The task remains tracked until shutdown so
    /// every close path observes the same release attempt and shutdown can retry a
    /// failed stop without losing the exact lease.
    /// </summary>
    private Task<WorkspaceIsolationError?> ScheduleWorkspaceIsolationCleanup(
        RuntimeWorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.IsolationBinding is null)
        {
            return Task.FromResult<WorkspaceIsolationError?>(null);
        }

        lock (_workspaceIsolationGate)
        {
            if (_workspaceIsolationCleanupTasks.TryGetValue(workspace.Id, out var existing))
            {
                return existing;
            }

            var cleanup = ReleaseClosedWorkspaceIsolationAsync(workspace.Id);
            _workspaceIsolationCleanupTasks.Add(workspace.Id, cleanup);
            return cleanup;
        }
    }

    private async Task<WorkspaceIsolationError?> ReleaseClosedWorkspaceIsolationAsync(
        WorkspaceInstanceId workspaceId)
    {
        WorkspaceIsolationError? error;
        try
        {
            error = await ReleaseWorkspaceIsolationAsync(
                    workspaceId,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "workspace-isolation.stop.failed",
                exception);
            error = WorkspaceIsolationError.Create(WorkspaceIsolationErrorCode.StopFailed);
        }

        if (error is not null && !_shutdownStarted)
        {
            await _uiThreadDispatcher.InvokeAsync(
                    () => SetError(error.Message),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        return error;
    }

    private async Task AwaitWorkspaceIsolationCleanupAsync()
    {
        Task<WorkspaceIsolationError?>[] cleanupTasks;
        lock (_workspaceIsolationGate)
        {
            cleanupTasks = [.. _workspaceIsolationCleanupTasks.Values];
        }

        if (cleanupTasks.Length > 0)
        {
            await Task.WhenAll(cleanupTasks).ConfigureAwait(false);
        }
    }

    private async Task AwaitWorkspaceIsolationPreparationsAsync()
    {
        Task[] preparationTasks;
        lock (_workspaceIsolationGate)
        {
            preparationTasks = [.. _workspaceIsolationPreparationTasks.Values];
        }

        if (preparationTasks.Length > 0)
        {
            await Task.WhenAll(preparationTasks).ConfigureAwait(false);
        }
    }

    private bool TryBeginWorkspaceActivation(
        out Guid activationId,
        out TaskCompletionSource completion)
    {
        activationId = Guid.NewGuid();
        completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_workspaceIsolationGate)
        {
            if (_shutdownStarted
                || _windowClosePending
                || _runtimeGraphLifetime.IsCancellationRequested)
            {
                return false;
            }

            _workspaceActivationTasks.Add(activationId, completion.Task);
            return true;
        }
    }

    private void CompleteWorkspaceActivation(
        Guid activationId,
        TaskCompletionSource completion)
    {
        lock (_workspaceIsolationGate)
        {
            _workspaceActivationTasks.Remove(activationId);
            completion.TrySetResult();
        }
    }

    private async Task AwaitWorkspaceActivationsAsync()
    {
        Task[] activationTasks;
        lock (_workspaceIsolationGate)
        {
            activationTasks = [.. _workspaceActivationTasks.Values];
        }

        if (activationTasks.Length > 0)
        {
            await Task.WhenAll(activationTasks).ConfigureAwait(false);
        }
    }

    private Task[] BeginWindowCloseActivationDrain()
    {
        lock (_workspaceIsolationGate)
        {
            _windowClosePending = true;
            return [.. _workspaceActivationTasks.Values];
        }
    }

    internal void ResumeAfterWindowCloseAttempt()
    {
        lock (_workspaceIsolationGate)
        {
            if (!_shutdownStarted)
            {
                _windowClosePending = false;
            }
        }
    }

    private void MarkWorkspaceGraphRegistrationAttempt(WorkspaceInstanceId workspaceId)
    {
        lock (_workspaceIsolationGate)
        {
            _pendingWorkspaceGraphRollbacks.Add(workspaceId);
        }
    }

    private void RetainWorkspaceGraph(WorkspaceInstanceId workspaceId)
    {
        lock (_workspaceIsolationGate)
        {
            _pendingWorkspaceGraphRollbacks.Remove(workspaceId);
        }
    }

    private bool RequiresWorkspaceGraphRollback(WorkspaceInstanceId workspaceId)
    {
        lock (_workspaceIsolationGate)
        {
            return _pendingWorkspaceGraphRollbacks.Contains(workspaceId);
        }
    }

    private async Task<bool> TryRollbackWorkspaceGraphAsync(
        WorkspaceInstanceId workspaceId)
    {
        HostResult<CloseScopeResult> result;
        try
        {
            result = await RuntimeGraph.CloseAsync(
                    CloseScopeRequest.Workspace(workspaceId, CloseDecision.Confirm),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "runtime-graph.registration-rollback.failed",
                exception);
            return false;
        }

        if (result is not HostResult<CloseScopeResult>.Success
            {
                Value: CloseScopeResult.Completed completed,
            }
            || completed.Scope != CloseScopeKind.Workspace
            || !string.Equals(
                completed.TargetId,
                workspaceId.Value,
                StringComparison.Ordinal)
            || completed.Sessions.Any(session => session.Outcome is not (
                SessionCloseOutcome.GracefullyClosed
                or SessionCloseOutcome.ForceTerminated
                or SessionCloseOutcome.AlreadyClosed)))
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "runtime-graph.registration-rollback.rejected",
                SecretSafeDiagnosticKind.Unexpected);
            return false;
        }

        lock (_workspaceIsolationGate)
        {
            _pendingWorkspaceGraphRollbacks.Remove(workspaceId);
        }

        return true;
    }

    private async Task RetryWorkspaceGraphRollbacksAsync()
    {
        WorkspaceInstanceId[] workspaceIds;
        lock (_workspaceIsolationGate)
        {
            workspaceIds = [.. _pendingWorkspaceGraphRollbacks];
        }

        foreach (var workspaceId in workspaceIds)
        {
            _ = await TryRollbackWorkspaceGraphAsync(workspaceId).ConfigureAwait(false);
        }
    }

    private async ValueTask<WorkspaceIsolationError?> ReleaseWorkspaceIsolationAsync(
        WorkspaceInstanceId workspaceId,
        CancellationToken cancellationToken)
    {
        WorkspaceIsolationLease? lease;
        lock (_workspaceIsolationGate)
        {
            _workspaceIsolationLeases.Remove(workspaceId, out lease);
        }

        if (lease is null)
        {
            return null;
        }

        var error = await StopWorkspaceIsolationAsync(lease.Binding, cancellationToken);
        if (error is not null)
        {
            lock (_workspaceIsolationGate)
            {
                _workspaceIsolationLeases.TryAdd(workspaceId, lease);
            }
        }

        return error;
    }

    private async ValueTask<WorkspaceIsolationError?> ReleasePreparedWorkspaceIsolationAsync(
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken)
    {
        var wasPending = false;
        WorkspaceInstanceId? workspaceId = null;
        WorkspaceIsolationLease? lease = null;
        lock (_workspaceIsolationGate)
        {
            wasPending = _pendingWorkspaceIsolationBindings.Remove(binding.LeaseId);
            var owned = _workspaceIsolationLeases.FirstOrDefault(
                item => item.Value.Binding.LeaseId == binding.LeaseId);
            if (owned.Value is not null)
            {
                workspaceId = owned.Key;
                lease = owned.Value;
                _workspaceIsolationLeases.Remove(owned.Key);
            }
        }

        if (!wasPending && lease is null)
        {
            return null;
        }

        var error = await StopWorkspaceIsolationAsync(binding, cancellationToken);
        if (error is not null)
        {
            lock (_workspaceIsolationGate)
            {
                if (workspaceId is { } runtimeId && lease is not null)
                {
                    _workspaceIsolationLeases.TryAdd(runtimeId, lease);
                }
                else if (wasPending)
                {
                    _pendingWorkspaceIsolationBindings.TryAdd(binding.LeaseId, binding);
                }
            }
        }

        return error;
    }

    private async ValueTask<WorkspaceIsolationError?> StopWorkspaceIsolationAsync(
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken)
    {
        var provider = _workspaceIsolationProvider
            ?? throw new InvalidOperationException(
                "An isolated runtime workspace requires an isolation provider.");
        try
        {
            var result = await provider.StopAsync(binding, cancellationToken);
            return result is WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure failure
                ? failure.Error
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return WorkspaceIsolationError.Create(WorkspaceIsolationErrorCode.Cancelled);
        }
        catch (Exception exception)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "workspace-isolation.stop.failed",
                exception);
            return WorkspaceIsolationError.Create(WorkspaceIsolationErrorCode.StopFailed);
        }
    }

    private async Task<IReadOnlyList<WorkspaceIsolationError>> ReleaseAllWorkspaceIsolationAsync()
    {
        WorkspaceInstanceId[] workspaceIds;
        WorkspaceIsolationBinding[] pending;
        lock (_workspaceIsolationGate)
        {
            workspaceIds = [.. _workspaceIsolationLeases.Keys.Where(
                workspaceId => !_pendingWorkspaceGraphRollbacks.Contains(workspaceId))];
            pending = [.. _pendingWorkspaceIsolationBindings.Values];
        }

        var errors = new List<WorkspaceIsolationError>();
        foreach (var workspaceId in workspaceIds)
        {
            if (await ReleaseWorkspaceIsolationAsync(workspaceId, CancellationToken.None)
                is { } error)
            {
                errors.Add(error);
            }
        }

        foreach (var binding in pending)
        {
            if (await ReleasePreparedWorkspaceIsolationAsync(binding, CancellationToken.None)
                is { } error)
            {
                errors.Add(error);
            }
        }

        return errors;
    }

    internal static bool SharesExecutionScope(
        RuntimeWorkspaceViewModel source,
        RuntimeWorkspaceViewModel destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        return (source.IsolationBinding, destination.IsolationBinding) switch
        {
            (null, null) => true,
            ({ } sourceBinding, { } destinationBinding) =>
                sourceBinding.Provider == destinationBinding.Provider
                && string.Equals(
                    sourceBinding.ResourceName,
                    destinationBinding.ResourceName,
                    StringComparison.Ordinal),
            _ => false,
        };
    }

    private sealed record WorkspaceIsolationLease(
        WorkspaceIsolationBinding Binding,
        IConnectionRuntime ConnectionRuntime);

    private readonly record struct WorkspaceIsolationPreparation(
        bool Succeeded,
        WorkspaceIsolationBinding? Binding)
    {
        public static WorkspaceIsolationPreparation Host { get; } = new(true, null);

        public static WorkspaceIsolationPreparation Failed { get; } = new(false, null);
    }
}
