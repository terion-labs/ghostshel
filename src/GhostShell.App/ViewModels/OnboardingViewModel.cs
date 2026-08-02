using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Presents truthful local first-run checks and owns only the explicit completion marker.
/// Definition import and history-retention mutations continue through their existing workflows.
/// </summary>
public sealed class OnboardingViewModel : ObservableObject, IDisposable
{
    public const int CurrentVersion = 1;

    private readonly IOnboardingProgressStore _progressStore;
    private readonly Func<ConnectionProfile?> _localConnection;
    private readonly IConnectionRuntime _connectionRuntime;
    private readonly SecretVaultAvailability _vaultAvailability;
    private readonly IUiThreadDispatcher _uiThreadDispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _startGate = new();
    private OnboardingProgress? _progress;
    private Task _initialization = Task.CompletedTask;
    private bool _started;
    private bool _reviewRequested;
    private bool _isVisible;
    private bool _isBusy;
    private bool _hasFailure;
    private string _statusMessage = "Checking this local profile…";
    private string _localTerminalState = "Checking";
    private string _localTerminalDetail = "Resolving the configured local shell without launching it.";
    private string _credentialVaultState = "Checking";
    private string _credentialVaultDetail =
        "Checking whether credentials can use operating-system protected storage.";
    private bool _disposed;

    public OnboardingViewModel(
        IOnboardingProgressStore progressStore,
        IDefinitionCatalog catalog,
        IConnectionRuntime connectionRuntime,
        SecretVaultAvailability vaultAvailability,
        IUiThreadDispatcher? uiThreadDispatcher = null)
        : this(
            progressStore,
            () => catalog?.Snapshot.Connections
                .Select(item => item.Value)
                .FirstOrDefault(item => item.Endpoint is ConnectionEndpoint.Local),
            connectionRuntime,
            vaultAvailability,
            uiThreadDispatcher)
    {
        ArgumentNullException.ThrowIfNull(catalog);
    }

    internal OnboardingViewModel(
        IOnboardingProgressStore progressStore,
        Func<ConnectionProfile?> localConnection,
        IConnectionRuntime connectionRuntime,
        SecretVaultAvailability vaultAvailability,
        IUiThreadDispatcher? uiThreadDispatcher = null)
    {
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
        _localConnection = localConnection
            ?? throw new ArgumentNullException(nameof(localConnection));
        _connectionRuntime = connectionRuntime
            ?? throw new ArgumentNullException(nameof(connectionRuntime));
        _vaultAvailability = vaultAvailability
            ?? throw new ArgumentNullException(nameof(vaultAvailability));
        _uiThreadDispatcher = uiThreadDispatcher ?? AvaloniaUiThreadDispatcher.Instance;
    }

    public Task Initialization
    {
        get
        {
            lock (_startGate)
            {
                return _initialization;
            }
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set
        {
            if (SetProperty(ref _isVisible, value))
            {
                OnPropertyChanged(nameof(CanInteract));
                OnPropertyChanged(nameof(CanRetry));
                OnPropertyChanged(nameof(CanFinish));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanInteract));
                OnPropertyChanged(nameof(CanRetry));
                OnPropertyChanged(nameof(CanFinish));
            }
        }
    }

    public bool HasFailure
    {
        get => _hasFailure;
        private set
        {
            if (SetProperty(ref _hasFailure, value))
            {
                OnPropertyChanged(nameof(CanRetry));
            }
        }
    }

    public bool CanInteract => IsVisible && !IsBusy;

    public bool CanRetry => IsVisible && HasFailure && !IsBusy;

    public bool CanFinish => IsVisible && _progress is not null && !IsBusy;

    public string Title => _reviewRequested ? "Review local setup" : "Welcome to GhostSHELL";

    public string Introduction => _reviewRequested
        ? "Recheck this profile’s local prerequisites and privacy choices."
        : "Confirm the local shell, credential storage, and session-history privacy before you settle in.";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string LocalTerminalState
    {
        get => _localTerminalState;
        private set => SetProperty(ref _localTerminalState, value);
    }

    public string LocalTerminalDetail
    {
        get => _localTerminalDetail;
        private set => SetProperty(ref _localTerminalDetail, value);
    }

    public string CredentialVaultState
    {
        get => _credentialVaultState;
        private set => SetProperty(ref _credentialVaultState, value);
    }

    public string CredentialVaultDetail
    {
        get => _credentialVaultDetail;
        private set => SetProperty(ref _credentialVaultDetail, value);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_startGate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _initialization = RefreshAsync(_lifetime.Token);
        }
    }

    public void ShowReview()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _reviewRequested = true;
        IsVisible = true;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Introduction));
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(CanFinish));
        if (!_started)
        {
            Start();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            await _operationGate.WaitAsync(operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await PublishAsync(() =>
            {
                IsBusy = true;
                HasFailure = false;
                StatusMessage = "Checking this local profile…";
            }, operation.Token).ConfigureAwait(false);

            var progress = await _progressStore.ReadAsync(operation.Token).ConfigureAwait(false);
            var localTerminal = await InspectLocalTerminalAsync(operation.Token)
                .ConfigureAwait(false);
            var vault = InspectCredentialVault();
            await PublishAsync(() =>
            {
                LocalTerminalState = localTerminal.State;
                LocalTerminalDetail = localTerminal.Detail;
                CredentialVaultState = vault.State;
                CredentialVaultDetail = vault.Detail;
                if (!progress.IsSuccess)
                {
                    _progress = null;
                    IsVisible = true;
                    HasFailure = true;
                    StatusMessage =
                        $"Local setup progress is unavailable ({progress.Error!.Code}). Retry to finish setup.";
                    OnPropertyChanged(nameof(CanFinish));
                    return;
                }

                _progress = progress.Value!;
                IsVisible = _reviewRequested
                    || _progress.CompletedVersion < CurrentVersion;
                HasFailure = localTerminal.HasFailure || vault.HasFailure;
                StatusMessage = HasFailure
                    ? "At least one local prerequisite needs attention. You can still import definitions or review privacy settings."
                    : _progress.CompletedVersion >= CurrentVersion
                        ? "This profile’s first-run setup is complete."
                        : "These checks use local metadata only and did not launch a terminal process.";
                OnPropertyChanged(nameof(CanInteract));
                OnPropertyChanged(nameof(CanFinish));
            }, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            try
            {
                await PublishAsync(() =>
                {
                    _progress = null;
                    IsVisible = true;
                    HasFailure = true;
                    StatusMessage =
                        "Local setup checks could not be completed. No application data was changed.";
                    OnPropertyChanged(nameof(CanFinish));
                }, operation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
            }
        }
        finally
        {
            try
            {
                await PublishAsync(() => IsBusy = false, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            await _operationGate.WaitAsync(operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var progress = _progress;
            if (progress is null)
            {
                await PublishAsync(() =>
                {
                    HasFailure = true;
                    StatusMessage =
                        "First-run progress must be loaded before setup can be finished.";
                }, operation.Token).ConfigureAwait(false);
                return;
            }

            await PublishAsync(() =>
            {
                IsBusy = true;
                HasFailure = false;
                StatusMessage = "Saving first-run progress…";
            }, operation.Token).ConfigureAwait(false);
            var result = await _progressStore.CompleteAsync(
                    CurrentVersion,
                    progress.Revision,
                    operation.Token)
                .ConfigureAwait(false);
            await PublishAsync(() =>
            {
                if (!result.IsSuccess)
                {
                    HasFailure = true;
                    StatusMessage =
                        $"First-run progress could not be saved ({result.Error!.Code}).";
                    return;
                }

                _progress = result.Value!;
                _reviewRequested = false;
                IsVisible = false;
                StatusMessage = "First-run setup complete.";
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Introduction));
                OnPropertyChanged(nameof(CanInteract));
                OnPropertyChanged(nameof(CanFinish));
            }, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            try
            {
                await PublishAsync(() =>
                {
                    HasFailure = true;
                    StatusMessage =
                        "First-run progress could not be saved. No other application data was changed.";
                }, operation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
            }
        }
        finally
        {
            try
            {
                await PublishAsync(() => IsBusy = false, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task<PrerequisitePresentation> InspectLocalTerminalAsync(
        CancellationToken cancellationToken)
    {
        var local = _localConnection();
        if (local is null)
        {
            return new(
                "Needs attention",
                "No local terminal definition is available. Import a setup or create a local connection.",
                HasFailure: true);
        }

        var result = await _connectionRuntime.TestAsync(
                local,
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
        return result switch
        {
            ConnectionRuntimeResult<ConnectionTestReport>.Success =>
                new(
                    "Ready",
                    "The configured local shell is installed and executable. This check did not launch it.",
                    HasFailure: false),
            ConnectionRuntimeResult<ConnectionTestReport>.Failure failure =>
                new(
                    "Needs attention",
                    $"{failure.Error.Message} Open the connection editor to repair it.",
                    HasFailure: true),
            _ => throw new InvalidOperationException(
                "The local terminal check returned an invalid result."),
        };
    }

    private PrerequisitePresentation InspectCredentialVault()
    {
        if (_vaultAvailability.CanPersist)
        {
            return new(
                "Ready",
                "Credential values can use operating-system protected storage. Definitions keep opaque references only.",
                HasFailure: false);
        }

        return _vaultAvailability.Persistence == SecretVaultPersistenceKind.MemoryOnly
            ? new(
                "Memory only",
                "Persistent credential storage is unavailable. Credentials can exist only for this process.",
                HasFailure: true)
            : new(
                "Unavailable",
                "Persistent credentials cannot be saved. Local terminals without stored credentials still work.",
                HasFailure: true);
    }

    private Task PublishAsync(Action action, CancellationToken cancellationToken) =>
        _uiThreadDispatcher.InvokeAsync(
            () =>
            {
                if (!_disposed)
                {
                    action();
                }
            },
            cancellationToken);

    private sealed record PrerequisitePresentation(
        string State,
        string Detail,
        bool HasFailure);
}
