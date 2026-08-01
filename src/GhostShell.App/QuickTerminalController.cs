using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using GhostShell.App.ViewModels;
using GhostShell.App.Views;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App;

public sealed class QuickTerminalController : IDisposable
{
    private static readonly KeyStroke DefaultGesture = QuickTerminalSettings.Default.Hotkey;
    private readonly IGlobalHotkeyService _globalHotkey;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly IDefinitionCatalog _catalog;
    private readonly IConnectionRuntime _connectionRuntime;
    private readonly IHostAccessibilityPreferencesSource _hostAccessibilityPreferences;
    private readonly IActiveWindowBoundsSource _activeWindowBounds;
    private readonly QuickTerminalDefinitionTracker _definitionTracker;
    private QuickTerminalViewModel _viewModel;
    private QuickTerminalSettings _settings = QuickTerminalSettings.Default;
    private HostAccessibilityPreferences _hostPreferences =
        HostAccessibilityPreferences.Default;
    private MainWindow? _mainWindow;
    private QuickTerminalWindow? _quickWindow;
    private IDisposable? _animationCompletion;
    private readonly QuickTerminalTransitionTimeline _transition = new();
    private KeyStroke? _configuredGesture;
    private KeyStroke? _activeGesture;
    private GlobalHotkeyRegistrationResult? _registrationResult;
    private long _settingsRevision = -1;
    private bool _initialized;
    private bool _isShuttingDown;
    private bool _disposed;

    public QuickTerminalController(
        IGlobalHotkeyService globalHotkey,
        MainWindowViewModel mainWindowViewModel,
        IDefinitionCatalog catalog,
        IConnectionRuntime connectionRuntime,
        IHostAccessibilityPreferencesSource hostAccessibilityPreferences,
        IActiveWindowBoundsSource activeWindowBounds)
    {
        _globalHotkey = globalHotkey ?? throw new ArgumentNullException(nameof(globalHotkey));
        _mainWindowViewModel = mainWindowViewModel
            ?? throw new ArgumentNullException(nameof(mainWindowViewModel));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _connectionRuntime = connectionRuntime
            ?? throw new ArgumentNullException(nameof(connectionRuntime));
        _hostAccessibilityPreferences = hostAccessibilityPreferences
            ?? throw new ArgumentNullException(nameof(hostAccessibilityPreferences));
        _activeWindowBounds = activeWindowBounds
            ?? throw new ArgumentNullException(nameof(activeWindowBounds));
        _definitionTracker = new QuickTerminalDefinitionTracker(_catalog.Snapshot);
        _viewModel = CreateViewModel();
    }

    public QuickTerminalViewModel ViewModel => _viewModel;

    public bool IsVisible => _transition.State != QuickTerminalVisibilityState.Hidden
        && _quickWindow?.IsVisible == true;

    public void Initialize(MainWindow mainWindow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(mainWindow);
        if (_initialized)
        {
            throw new InvalidOperationException("The Quick Terminal controller is already initialized.");
        }

        _initialized = true;
        _mainWindow = mainWindow;
        _mainWindow.Closing += OnMainWindowClosing;
        _mainWindow.Closed += OnMainWindowClosed;
        _globalHotkey.Pressed += OnGlobalHotkeyPressed;
        _globalHotkey.EscapePressed += OnEscapePressed;
        _catalog.Changed += OnCatalogChanged;
        _hostAccessibilityPreferences.Changed += OnHostAccessibilityPreferencesChanged;
        ApplyHostAccessibilityPreferences();
        ApplySettingsFromCatalog();
    }

    public void Toggle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isShuttingDown)
        {
            return;
        }

        if (!_initialized)
        {
            throw new InvalidOperationException("The Quick Terminal controller has not been initialized.");
        }

        if (_transition.State is QuickTerminalVisibilityState.Visible
            or QuickTerminalVisibilityState.Showing)
        {
            Hide();
            return;
        }

        var window = GetOrCreateWindow();
        var progress = PauseTransition(window);
        if (_transition.State == QuickTerminalVisibilityState.Hidden)
        {
            progress = 0;
        }

        PositionAtTopOfWorkingArea(window);
        _viewModel.ApplyEscapeCapture(_globalHotkey.BeginEscapeCapture());
        try
        {
            if (!window.IsVisible)
            {
                window.PrepareReveal(progress);
                window.Show();
            }
            else
            {
                window.SetRevealProgress(progress);
            }

            window.ApplyBackdrop();
            window.Activate();
            window.FocusTerminal();
            StartTransition(window, progress, 1);
        }
        catch
        {
            _globalHotkey.EndEscapeCapture();
            PauseTransition(window);
            _transition.Reset();
            if (window.IsVisible)
            {
                window.Hide();
            }

            throw;
        }
    }

    public void Hide()
    {
        _globalHotkey.EndEscapeCapture();
        if (_quickWindow?.IsVisible != true
            || _transition.State is QuickTerminalVisibilityState.Hidden
                or QuickTerminalVisibilityState.Hiding)
        {
            return;
        }

        var window = _quickWindow;
        var progress = PauseTransition(window);
        StartTransition(window, progress, 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelTransition();
        _catalog.Changed -= OnCatalogChanged;
        _hostAccessibilityPreferences.Changed -= OnHostAccessibilityPreferencesChanged;
        _globalHotkey.Pressed -= OnGlobalHotkeyPressed;
        _globalHotkey.EscapePressed -= OnEscapePressed;
        _globalHotkey.EndEscapeCapture();
        _globalHotkey.Unregister();
        _viewModel.Dispose();
        if (_mainWindow is not null)
        {
            _mainWindow.Closing -= OnMainWindowClosing;
            _mainWindow.Closed -= OnMainWindowClosed;
        }

        if (_quickWindow is not null)
        {
            _quickWindow.DismissRequested -= OnDismissRequested;
            _quickWindow.SettingsRequested -= OnSettingsRequested;
            _quickWindow.Activated -= OnQuickWindowActivated;
            _quickWindow.Deactivated -= OnQuickWindowDeactivated;
            _quickWindow.Closed -= OnQuickWindowClosed;
            _quickWindow.ClosePermanently();
            _quickWindow = null;
        }

        _mainWindow = null;
    }

    private bool MotionEnabled => QuickTerminalPresentationPolicy.ShouldAnimate(
        _settings,
        _hostPreferences);

    private QuickTerminalViewModel CreateViewModel() => new(
        _mainWindowViewModel,
        _catalog,
        _connectionRuntime);

    private QuickTerminalWindow GetOrCreateWindow()
    {
        if (_quickWindow is not null)
        {
            return _quickWindow;
        }

        _quickWindow = new QuickTerminalWindow
        {
            DataContext = _viewModel,
        };
        _quickWindow.ApplySettings(_settings, _hostPreferences);
        _quickWindow.DismissRequested += OnDismissRequested;
        _quickWindow.SettingsRequested += OnSettingsRequested;
        _quickWindow.Activated += OnQuickWindowActivated;
        _quickWindow.Deactivated += OnQuickWindowDeactivated;
        _quickWindow.Closed += OnQuickWindowClosed;
        return _quickWindow;
    }

    private void PositionAtTopOfWorkingArea(QuickTerminalWindow window)
    {
        var mainWindow = _mainWindow
            ?? throw new InvalidOperationException("The GhostSHELL main window is unavailable.");
        var mainWindowScreen = mainWindow.Screens.ScreenFromWindow(mainWindow);
        var activeWindowBounds = _settings.MonitorPolicy ==
            QuickTerminalMonitorPolicy.ActiveWindow
                ? _activeWindowBounds.TryGetBounds()
                : null;
        var activeWindowScreen = activeWindowBounds is { } bounds
            ? mainWindow.Screens.ScreenFromBounds(bounds)
            : null;
        var screen = QuickTerminalScreenResolver.Resolve(
            mainWindowScreen,
            mainWindow.Screens.Primary,
            activeWindowScreen,
            _settings.MonitorPolicy)
          ?? throw new InvalidOperationException("No desktop screen is available for Quick Terminal.");
        var workingArea = screen.WorkingArea;
        var scale = screen.Scaling;
        var availableHeight = workingArea.Height / scale;
        window.Width = workingArea.Width / scale;
        window.Height = Math.Min(
            availableHeight,
            Math.Max(window.MinHeight, Math.Round(availableHeight * _settings.HeightFraction)));
        window.Position = workingArea.Position;
    }

    private void ApplySettingsFromCatalog()
    {
        if (_disposed)
        {
            return;
        }

        var previousRestorePolicy = _settings.RestoreLastSession;
        var terminalDefinitionsChanged = _definitionTracker.Update(_catalog.Snapshot);
        var storedSettings = _catalog.Snapshot.QuickTerminalSettings
            .OrderByDescending(item => item.Value.Id == QuickTerminalSettings.DefaultId)
            .ThenBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var nextSettings = storedSettings?.Value ?? QuickTerminalSettings.Default;
        var nextRevision = storedSettings?.Revision ?? 0;
        var definitionChanged = _settings.Id != nextSettings.Id
            || _settingsRevision != nextRevision;
        _settings = nextSettings;
        _settingsRevision = nextRevision;

        if (definitionChanged
            || _configuredGesture != _settings.Hotkey
            || _registrationResult is null)
        {
            RegisterConfiguredHotkey();
        }
        else
        {
            PublishRegistration();
        }

        var shouldResetSession = QuickTerminalRuntimeRules
            .ShouldResetForDefinitionOrPolicyChange(
                terminalDefinitionsChanged,
                previousRestorePolicy,
                _settings.RestoreLastSession,
                IsVisible);
        if (_quickWindow is { } window)
        {
            window.ApplySettings(_settings, _hostPreferences);
            if (window.IsVisible)
            {
                var target = _transition.State == QuickTerminalVisibilityState.Hiding
                    ? 0
                    : 1;
                var wasTransitioning = _transition.State is
                    QuickTerminalVisibilityState.Showing
                    or QuickTerminalVisibilityState.Hiding;
                var progress = PauseTransition(window);
                PositionAtTopOfWorkingArea(window);
                window.ApplyBackdrop();
                window.SetRevealProgress(progress);
                if (wasTransitioning)
                {
                    StartTransition(window, progress, target);
                }
            }
        }

        if (shouldResetSession)
        {
            var reopenAfterReset = terminalDefinitionsChanged && IsVisible;
            ResetSession();
            if (reopenAfterReset)
            {
                Toggle();
            }
        }
    }

    private void RegisterConfiguredHotkey()
    {
        _configuredGesture = _settings.Hotkey;
        _registrationResult = _globalHotkey.Register(_settings.Hotkey);
        _activeGesture = _registrationResult is GlobalHotkeyRegistrationResult.Success
            ? _settings.Hotkey
            : null;

        if (_registrationResult is GlobalHotkeyRegistrationResult.Failure
            && _settings.Hotkey != DefaultGesture)
        {
            var fallback = _globalHotkey.Register(DefaultGesture);
            if (fallback is GlobalHotkeyRegistrationResult.Success)
            {
                _activeGesture = DefaultGesture;
            }
        }

        PublishRegistration();
    }

    private void PublishRegistration()
    {
        if (_registrationResult is null)
        {
            return;
        }

        _viewModel.ApplyRegistration(_settings.Hotkey, _activeGesture, _registrationResult);
        _mainWindowViewModel.ApplyQuickTerminalRegistration(
            _settings.Hotkey,
            _activeGesture,
            _registrationResult);
    }

    private void CompleteHide(QuickTerminalWindow window)
    {
        if (window.IsVisible)
        {
            window.Hide();
        }

        window.PrepareReveal(0);
        if (QuickTerminalRuntimeRules.ShouldResetAfterHide(_settings.RestoreLastSession))
        {
            ResetSession();
        }
    }

    private void ResetSession()
    {
        ResetTransition();
        var previousViewModel = _viewModel;
        var previousRequest = previousViewModel.TerminalRequest;
        if (_quickWindow is { } window)
        {
            window.DismissRequested -= OnDismissRequested;
            window.SettingsRequested -= OnSettingsRequested;
            window.Activated -= OnQuickWindowActivated;
            window.Deactivated -= OnQuickWindowDeactivated;
            window.Closed -= OnQuickWindowClosed;
            window.ClosePermanently();
            _quickWindow = null;
        }

        previousViewModel.Dispose();
        _viewModel = CreateViewModel();
        PublishRegistration();
        if (previousRequest is not null)
        {
            _ = CloseDiscardedSessionAsync(previousRequest.SessionId, previousViewModel.ClientId);
        }
    }

    private async Task CloseDiscardedSessionAsync(SessionId sessionId, ClientId clientId)
    {
        try
        {
            var result = await _mainWindowViewModel.SessionClient.CloseAsync(
                CloseScopeRequest.Session(sessionId, CloseDecision.Confirm),
                OperationContext.ForHuman(clientId),
                CancellationToken.None);
            if (result is HostResult<CloseScopeResult>.Failure failure)
            {
                Trace.TraceError(
                    "The session host could not close a discarded Quick Terminal session: {0}",
                    failure.Error.StableCode);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "Unable to close a Quick Terminal session that was configured not to restore: {0}",
                exception);
        }
    }

    private void StartTransition(
        QuickTerminalWindow window,
        double from,
        double to)
    {
        from = Math.Clamp(from, 0, 1);
        to = Math.Clamp(to, 0, 1);
        CancelCompletionTimer();
        var generation = _transition.Begin(
            from,
            to,
            MotionEnabled ? _settings.AnimationDurationMilliseconds : 0,
            Environment.TickCount64);
        if (_transition.DurationMilliseconds <= 0)
        {
            CompleteTransition(generation);
            return;
        }

        var duration = TimeSpan.FromMilliseconds(_transition.DurationMilliseconds);
        window.AnimateReveal(from, to, duration);
        _animationCompletion = DispatcherTimer.RunOnce(
            () => CompleteTransition(generation),
            duration,
            DispatcherPriority.Render);
    }

    private double PauseTransition(QuickTerminalWindow? window)
    {
        var progress = _transition.Pause(Environment.TickCount64);
        CancelCompletionTimer();
        window?.SetRevealProgress(progress);
        return progress;
    }

    private void CancelTransition()
    {
        CancelCompletionTimer();
        _transition.Cancel();
    }

    private void ResetTransition()
    {
        CancelCompletionTimer();
        _transition.Reset();
    }

    private void CancelCompletionTimer()
    {
        _animationCompletion?.Dispose();
        _animationCompletion = null;
    }

    private void CompleteTransition(long generation)
    {
        if (!_transition.TryComplete(generation))
        {
            return;
        }

        CancelCompletionTimer();
        var window = _quickWindow;
        var destination = _transition.Progress;
        window?.SetRevealProgress(destination);
        if (window is null)
        {
            _transition.Reset();
            return;
        }

        if (_transition.State == QuickTerminalVisibilityState.Visible)
        {
            window.FocusTerminal();
            return;
        }

        CompleteHide(window);
    }

    private void CompleteTransitionImmediately()
    {
        if (_animationCompletion is null)
        {
            return;
        }

        CompleteTransition(_transition.Generation);
    }

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(ApplySettingsFromCatalog);
    }

    private void OnHostAccessibilityPreferencesChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(ApplyHostAccessibilityPreferences);
    }

    private void ApplyHostAccessibilityPreferences()
    {
        if (_disposed)
        {
            return;
        }

        var next = _hostAccessibilityPreferences.Current;
        if (_hostPreferences == next)
        {
            return;
        }

        var mustFinishAnimation = !_hostPreferences.ReducedMotion && next.ReducedMotion;
        _hostPreferences = next;
        if (mustFinishAnimation)
        {
            CompleteTransitionImmediately();
        }

        if (_quickWindow is { } window)
        {
            window.ApplySettings(_settings, _hostPreferences);
            if (window.IsVisible)
            {
                window.ApplyBackdrop();
            }
        }
    }

    private void OnGlobalHotkeyPressed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && !_isShuttingDown)
            {
                Toggle();
            }
        });
    }

    private void OnDismissRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Hide();
    }

    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Hide();
        _mainWindowViewModel.ShowSettings(SettingsPage.QuickTerminal);
        if (_mainWindow is not { } mainWindow)
        {
            return;
        }

        if (!mainWindow.IsVisible)
        {
            mainWindow.Show();
        }

        mainWindow.Activate();
    }

    private void OnQuickWindowActivated(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_disposed && IsVisible)
        {
            _viewModel.ApplyEscapeCapture(_globalHotkey.BeginEscapeCapture());
        }
    }

    private void OnQuickWindowDeactivated(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _globalHotkey.EndEscapeCapture();
    }

    private void OnQuickWindowClosed(object? sender, EventArgs e)
    {
        _ = e;
        if (!ReferenceEquals(sender, _quickWindow))
        {
            return;
        }

        if (sender is not QuickTerminalWindow window)
        {
            return;
        }

        window.DismissRequested -= OnDismissRequested;
        window.SettingsRequested -= OnSettingsRequested;
        window.Activated -= OnQuickWindowActivated;
        window.Deactivated -= OnQuickWindowDeactivated;
        window.Closed -= OnQuickWindowClosed;
        ResetTransition();
        _quickWindow = null;
        _globalHotkey.EndEscapeCapture();
    }

    private void OnEscapePressed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && IsVisible)
            {
                var action = QuickTerminalRuntimeRules.ResolveEscape(
                    IsVisible,
                    _quickWindow?.TryCancelPendingInteraction() == true);
                if (action != QuickTerminalEscapeAction.Hide)
                {
                    return;
                }

                Hide();
            }
        });
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispose();
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _ = sender;
        _ = e;
        _isShuttingDown = true;
        CancelTransition();
        _globalHotkey.EndEscapeCapture();
        _globalHotkey.Unregister();
    }
}
