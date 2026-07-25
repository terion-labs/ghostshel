using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed class QuickTerminalViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private EnsureTerminalSessionRequest? _terminalRequest;
    private bool _isInitializing;
    private string _terminalUnavailableMessage = string.Empty;
    private string _shortcutStatus = "GLOBAL SHORTCUT · REGISTERING";
    private string _shortcutDetail =
        $"GhostSHELL is registering {QuickTerminalHotkeyText.Example} for Quick Terminal.";
    private string _shortcutStatusBrush = "#8B8B91";
    private string _escapeStatus = "ESC · HIDE";
    private string _escapeDetail = "Escape hides Quick Terminal.";

    public QuickTerminalViewModel(
        MainWindowViewModel mainWindow,
        IDefinitionCatalog catalog,
        IConnectionRuntime connectionRuntime)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(connectionRuntime);

        SessionClient = mainWindow.SessionClient;
        ClientId = mainWindow.ClientId;
        WindowId = WindowInstanceId.New();

        var selection = QuickTerminalDefinitionSelection.Resolve(catalog.Snapshot);
        var localConnection = selection.Connection?.Value;
        var terminalProfile = selection.TerminalProfile?.Value;
        var terminalKeymap = selection.TerminalKeymap?.Value;

        if (localConnection is null)
        {
            ConnectionName = "No local connection";
            ProfileName = terminalProfile?.Name ?? "Platform defaults";
            TerminalUnavailableMessage =
                "Quick Terminal needs a saved local connection. Add one in Settings and this window will reconnect automatically.";
            Initialization = Task.CompletedTask;
            return;
        }

        var panelId = PanelInstanceId.New();
        ConnectionName = localConnection.Name;
        ProfileName = terminalProfile?.Name ?? "Platform defaults";
        IsInitializing = true;
        Initialization = InitializeAsync(
            connectionRuntime,
            localConnection,
            new SessionOwner(
                HostMode.Desktop,
                WindowId,
                WorkspaceInstanceId.New(),
                TabInstanceId.New(),
                panelId),
            terminalProfile is null
                ? null
                : TerminalRenderProfileSnapshot.FromProfile(terminalProfile),
            terminalKeymap is null
                ? null
                : TerminalKeymapSnapshot.FromProfile(terminalKeymap),
            _lifetime.Token);
    }

    public ISessionHostClient SessionClient { get; }

    public ClientId ClientId { get; }

    /// <summary>
    /// Identifies the independent native Quick Terminal window. It must not share
    /// the main window's authoritative workspace graph ownership boundary.
    /// </summary>
    public WindowInstanceId WindowId { get; }

    public EnsureTerminalSessionRequest? TerminalRequest
    {
        get => _terminalRequest;
        private set
        {
            if (!ReferenceEquals(_terminalRequest, value))
            {
                _terminalRequest = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTerminalAvailable));
                OnPropertyChanged(nameof(ShowTerminalPlaceholder));
            }
        }
    }

    public Task Initialization { get; }

    public bool IsTerminalAvailable => TerminalRequest is not null;

    public bool IsInitializing
    {
        get => _isInitializing;
        private set
        {
            if (SetProperty(ref _isInitializing, value))
            {
                OnPropertyChanged(nameof(ShowTerminalPlaceholder));
                OnPropertyChanged(nameof(TerminalPlaceholderTitle));
            }
        }
    }

    public bool ShowTerminalPlaceholder => !IsTerminalAvailable;

    public string TerminalPlaceholderTitle => IsInitializing
        ? "Preparing Quick Terminal"
        : "Quick Terminal unavailable";

    public string TerminalUnavailableMessage
    {
        get => _terminalUnavailableMessage;
        private set => SetProperty(ref _terminalUnavailableMessage, value);
    }

    public string ConnectionName { get; }

    public string ProfileName { get; }

    public string ShortcutStatus
    {
        get => _shortcutStatus;
        private set => SetProperty(ref _shortcutStatus, value);
    }

    public string ShortcutDetail
    {
        get => _shortcutDetail;
        private set => SetProperty(ref _shortcutDetail, value);
    }

    public string ShortcutStatusBrush
    {
        get => _shortcutStatusBrush;
        private set => SetProperty(ref _shortcutStatusBrush, value);
    }

    public string EscapeStatus
    {
        get => _escapeStatus;
        private set => SetProperty(ref _escapeStatus, value);
    }

    public string EscapeDetail
    {
        get => _escapeDetail;
        private set => SetProperty(ref _escapeDetail, value);
    }

    public void ApplyRegistration(
        KeyStroke configuredGesture,
        KeyStroke? activeGesture,
        GlobalHotkeyRegistrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var configured = QuickTerminalHotkeyText.Format(configuredGesture);
        switch (result)
        {
            case GlobalHotkeyRegistrationResult.Success:
                ShortcutStatus = $"{configured.ToUpperInvariant()} · READY";
                ShortcutDetail = $"{configured} toggles Quick Terminal from any application.";
                ShortcutStatusBrush = "#3FB950";
                break;
            case GlobalHotkeyRegistrationResult.Failure failure:
                ShortcutStatus = activeGesture is null
                    ? failure.Error.Code switch
                    {
                        GlobalHotkeyRegistrationErrorCode.Conflict =>
                            $"{configured.ToUpperInvariant()} · CONFLICT",
                        GlobalHotkeyRegistrationErrorCode.Unsupported =>
                            "GLOBAL SHORTCUT · UNAVAILABLE",
                        _ => "GLOBAL SHORTCUT · ERROR",
                    }
                    : $"{QuickTerminalHotkeyText.Format(activeGesture.Value).ToUpperInvariant()} · FALLBACK";
                ShortcutDetail = activeGesture is null
                    ? failure.Error.Message
                    : $"{failure.Error.Message} {QuickTerminalHotkeyText.Format(activeGesture.Value)} remains active.";
                ShortcutStatusBrush = "#FFB224";
                break;
        }
    }

    public void ApplyEscapeCapture(GlobalHotkeyRegistrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        switch (result)
        {
            case GlobalHotkeyRegistrationResult.Success:
                EscapeStatus = "ESC · HIDE";
                EscapeDetail = "Escape hides Quick Terminal without ending its terminal session.";
                break;
            case GlobalHotkeyRegistrationResult.Failure failure:
                EscapeStatus = "ESC · UNAVAILABLE";
                EscapeDetail = failure.Error.Message;
                break;
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task InitializeAsync(
        IConnectionRuntime connectionRuntime,
        ConnectionProfile connection,
        SessionOwner owner,
        TerminalRenderProfileSnapshot? renderProfile,
        TerminalKeymapSnapshot? keymap,
        CancellationToken cancellationToken)
    {
        var progress = new Progress<ConnectionProgress>(item =>
        {
            if (IsInitializing)
            {
                TerminalUnavailableMessage = item.Message;
            }
        });
        try
        {
            var result = await connectionRuntime.PlanOpenAsync(
                connection,
                progress,
                cancellationToken);
            if (result is ConnectionRuntimeResult<ConnectionOpenPlan>.Failure failure)
            {
                TerminalUnavailableMessage = failure.Error.Message;
                return;
            }

            var plan = ((ConnectionRuntimeResult<ConnectionOpenPlan>.Success)result).Value;
            if (plan.RequiresSecretBroker)
            {
                TerminalUnavailableMessage =
                    "This local profile requires secret environment delivery, which is unavailable until the secure credential broker is installed.";
                return;
            }

            var launch = plan.Launch.WithPresentationProfiles(
                renderProfile,
                keymap);
            TerminalRequest = new EnsureTerminalSessionRequest(
                SessionId.New(),
                owner,
                "Quick Terminal",
                launch);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            TerminalUnavailableMessage = "The connection runtime could not prepare Quick Terminal.";
        }
        finally
        {
            IsInitializing = false;
        }
    }
}
