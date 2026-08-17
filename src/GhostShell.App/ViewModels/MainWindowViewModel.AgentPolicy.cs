using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly object _defaultAgentPolicySaveGate = new();
    private AgentPolicy? _pendingDefaultAgentPolicy;
    private Task _defaultAgentPolicySaveTask = Task.CompletedTask;
    private bool _defaultAgentPolicySaveRunning;

    private void QueueDefaultAgentPolicyPersistence(bool onlyWhenMissing)
    {
        if (_shutdownStarted
            || _disposed
            || _agentPolicyCoordinator is null
            || onlyWhenMissing && _agentPolicyCoordinator.Policy is not null
            || !DefaultAgentPolicy.IsValid)
        {
            return;
        }

        AgentPolicy? policy;
        try
        {
            policy = DefaultAgentPolicy.Build();
        }
        catch (ArgumentException)
        {
            return;
        }

        if (policy is null)
        {
            return;
        }

        lock (_defaultAgentPolicySaveGate)
        {
            _pendingDefaultAgentPolicy = policy;
            if (_defaultAgentPolicySaveRunning)
            {
                return;
            }

            _defaultAgentPolicySaveRunning = true;
            _defaultAgentPolicySaveTask = PersistDefaultAgentPoliciesAsync();
        }
    }

    private async Task PersistDefaultAgentPoliciesAsync()
    {
        while (true)
        {
            AgentPolicy? policy;
            lock (_defaultAgentPolicySaveGate)
            {
                policy = _pendingDefaultAgentPolicy;
                _pendingDefaultAgentPolicy = null;
                if (policy is null)
                {
                    _defaultAgentPolicySaveRunning = false;
                    return;
                }
            }

            var result = await _agentPolicyCoordinator!
                .SaveAsync(policy, CancellationToken.None)
                .ConfigureAwait(false);
            if (!result.IsSuccess && !_disposed)
            {
                await _uiThreadDispatcher.InvokeAsync(
                        () => SetError(result.Error!.Message),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private async void OnAgentPolicyCoordinatorChanged(
        object? sender,
        EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        try
        {
            await _uiThreadDispatcher.InvokeAsync(
                    () =>
                    {
                        if (!_disposed)
                        {
                            ActivateWorkspaceAgentChat(RuntimeWorkspace?.Id);
                        }
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
    }

    private Task WaitForDefaultAgentPolicyPersistenceAsync()
    {
        lock (_defaultAgentPolicySaveGate)
        {
            return _defaultAgentPolicySaveTask;
        }
    }
}
