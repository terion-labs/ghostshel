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
    private readonly QuickTerminalDefinitionTracker _definitionTracker;
    private QuickTerminalViewModel _viewModel;
    private QuickTerminalSettings _settings = QuickTerminalSettings.Default;
    private HostAccessibilityPreferences _hostPreferences =
        HostAccessibilityPreferences.Default;
    private MainWindow? _mainWindow;
    private QuickTerminalWindow? _quickWindow;
    private DispatcherTimer? _animationTimer;
    private EventHandler? _animationTick;
    private QuickTerminalWindow? _animationWindow;
    private PixelPoint _animationDestination;
    private Action? _animationCompleted;
    private KeyStroke? _configuredGesture;
    private KeyStroke? _activeGesture;
    private GlobalHotkeyRegistrationResult? _registrationResult;
    private PixelPoint _shownPosition;
    private int _hiddenOffsetPixels;
    private long _settingsRevision = -1;
    private bool _initialized;
    private bool _disposed;

    public QuickTerminalController(
        IGlobalHotkeyService globalHotkey,
        MainWindowViewModel mainWindowViewModel,
        IDefinitionCatalog catalog,
        IConnectionRuntime connectionRuntime,
        IHostAccessibilityPreferencesSource hostAccessibilityPreferences)
    {
        _globalHotkey = globalHotkey ?? throw new ArgumentNullException(nameof(globalHotkey));
        _mainWindowViewModel = mainWindowViewModel
            ?? throw new ArgumentNullException(nameof(mainWindowViewModel));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _connectionRuntime = connectionRuntime
            ?? throw new ArgumentNullException(nameof(connectionRuntime));
        _hostAccessibilityPreferences = hostAccessibilityPreferences
            ?? throw new ArgumentNullException(nameof(hostAccessibilityPreferences));
        _definitionTracker = new QuickTerminalDefinitionTracker(_catalog.Snapshot);
        _viewModel = CreateViewModel();
    }

    public QuickTerminalViewModel ViewModel => _viewModel;

    public bool IsVisible => _quickWindow?.IsVisible == true;

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
        if (!_initialized)
        {
            throw new InvalidOperationException("The Quick Terminal controller has not been initialized.");
        }

        if (IsVisible)
        {
            Hide();
            return;
        }

        var window = GetOrCreateWindow();
        StopAnimation();
        _shownPosition = PositionAtTopOfWorkingArea(window);
        window.ApplySettings(_settings, _hostPreferences);
        _viewModel.ApplyEscapeCapture(_globalHotkey.BeginEscapeCapture());
        try
        {
            if (MotionEnabled)
            {
                var hiddenPosition = AboveWorkingArea(_shownPosition);
                window.Position = hiddenPosition;
                window.Show();
                window.Activate();
                window.FocusTerminal();
                AnimatePosition(
                    window,
                    hiddenPosition,
                    _shownPosition,
                    _settings.AnimationDurationMilliseconds,
                    window.FocusTerminal);
            }
            else
            {
                window.Position = _shownPosition;
                window.Show();
                window.Activate();
                window.FocusTerminal();
            }
        }
        catch
        {
            _globalHotkey.EndEscapeCapture();
            StopAnimation();
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
        if (_quickWindow?.IsVisible != true)
        {
            return;
        }

        var window = _quickWindow;
        StopAnimation();
        if (!MotionEnabled)
        {
            CompleteHide(window);
            return;
        }

        AnimatePosition(
            window,
            window.Position,
            AboveWorkingArea(_shownPosition),
            _settings.AnimationDurationMilliseconds,
            () => CompleteHide(window));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAnimation();
        _catalog.Changed -= OnCatalogChanged;
        _hostAccessibilityPreferences.Changed -= OnHostAccessibilityPreferencesChanged;
        _globalHotkey.Pressed -= OnGlobalHotkeyPressed;
        _globalHotkey.EscapePressed -= OnEscapePressed;
        _globalHotkey.EndEscapeCapture();
        _globalHotkey.Unregister();
        _viewModel.Dispose();
        if (_mainWindow is not null)
        {
            _mainWindow.Closed -= OnMainWindowClosed;
        }

        if (_quickWindow is not null)
        {
            _quickWindow.DismissRequested -= OnDismissRequested;
            _quickWindow.SettingsRequested -= OnSettingsRequested;
            _quickWindow.Activated -= OnQuickWindowActivated;
            _quickWindow.Deactivated -= OnQuickWindowDeactivated;
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
        return _quickWindow;
    }

    private PixelPoint PositionAtTopOfWorkingArea(QuickTerminalWindow window)
    {
        var mainWindow = _mainWindow
            ?? throw new InvalidOperationException("The GhostSHELL main window is unavailable.");
        var screen = _settings.MonitorPolicy switch
        {
            QuickTerminalMonitorPolicy.Primary => mainWindow.Screens.Primary,
            _ => mainWindow.Screens.ScreenFromWindow(mainWindow),
        } ?? mainWindow.Screens.Primary
          ?? throw new InvalidOperationException("No desktop screen is available for Quick Terminal.");
        var workingArea = screen.WorkingArea;
        var scale = screen.Scaling;
        var availableHeight = workingArea.Height / scale;
        window.Width = workingArea.Width / scale;
        window.Height = Math.Min(
            availableHeight,
            Math.Max(window.MinHeight, Math.Round(availableHeight * _settings.HeightFraction)));
        _hiddenOffsetPixels = checked((int)Math.Ceiling(window.Height * scale));
        window.Position = workingArea.Position;
        return workingArea.Position;
    }

    private PixelPoint AboveWorkingArea(PixelPoint shownPosition) =>
        new(shownPosition.X, shownPosition.Y - _hiddenOffsetPixels);

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
                StopAnimation();
                _shownPosition = PositionAtTopOfWorkingArea(window);
                window.Position = _shownPosition;
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
        StopAnimation();
        if (window.IsVisible)
        {
            window.Hide();
        }

        window.Position = _shownPosition;
        if (QuickTerminalRuntimeRules.ShouldResetAfterHide(_settings.RestoreLastSession))
        {
            ResetSession();
        }
    }

    private void ResetSession()
    {
        var previousViewModel = _viewModel;
        var previousRequest = previousViewModel.TerminalRequest;
        if (_quickWindow is { } window)
        {
            window.DismissRequested -= OnDismissRequested;
            window.SettingsRequested -= OnSettingsRequested;
            window.Activated -= OnQuickWindowActivated;
            window.Deactivated -= OnQuickWindowDeactivated;
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

    private void AnimatePosition(
        QuickTerminalWindow window,
        PixelPoint from,
        PixelPoint to,
        int durationMilliseconds,
        Action completed)
    {
        StopAnimation();
        if (durationMilliseconds <= 0)
        {
            window.Position = to;
            completed();
            return;
        }

        var started = Environment.TickCount64;
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        EventHandler tick = (_, _) =>
        {
            var elapsed = Environment.TickCount64 - started;
            var progress = Math.Clamp(elapsed / (double)durationMilliseconds, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            window.Position = new PixelPoint(
                Interpolate(from.X, to.X, eased),
                Interpolate(from.Y, to.Y, eased));
            if (progress < 1)
            {
                return;
            }

            CompleteAnimationImmediately();
        };
        _animationTimer = timer;
        _animationTick = tick;
        _animationWindow = window;
        _animationDestination = to;
        _animationCompleted = completed;
        timer.Tick += tick;
        timer.Start();
    }

    private void StopAnimation()
    {
        if (_animationTimer is { } timer)
        {
            timer.Stop();
            if (_animationTick is not null)
            {
                timer.Tick -= _animationTick;
            }
        }

        _animationTimer = null;
        _animationTick = null;
        _animationWindow = null;
        _animationCompleted = null;
    }

    private void CompleteAnimationImmediately()
    {
        var window = _animationWindow;
        var destination = _animationDestination;
        var completed = _animationCompleted;
        StopAnimation();
        if (window is null || completed is null)
        {
            return;
        }

        window.Position = destination;
        completed();
    }

    private static int Interpolate(int from, int to, double progress) =>
        checked((int)Math.Round(from + ((to - from) * progress)));

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
            CompleteAnimationImmediately();
        }

        _quickWindow?.ApplySettings(_settings, _hostPreferences);
    }

    private void OnGlobalHotkeyPressed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
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
}
