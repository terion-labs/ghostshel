using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Controls;

/// <summary>
/// Selects the platform terminal presentation while preserving one XAML-facing control
/// contract. Durable definitions and view models never branch on the operating system.
/// </summary>
public sealed class TerminalPresentationHost : ContentControl
{
    private static readonly IBrush StartingBrush = Brush.Parse("#8B8B91");

    public static readonly StyledProperty<ISessionHostClient?> SessionClientProperty =
        AvaloniaProperty.Register<TerminalPresentationHost, ISessionHostClient?>(
            nameof(SessionClient));

    public static readonly StyledProperty<EnsureTerminalSessionRequest?> SessionRequestProperty =
        AvaloniaProperty.Register<TerminalPresentationHost, EnsureTerminalSessionRequest?>(
            nameof(SessionRequest));

    public static readonly StyledProperty<ClientId?> ClientIdProperty =
        AvaloniaProperty.Register<TerminalPresentationHost, ClientId?>(nameof(ClientId));

    public static readonly StyledProperty<TerminalStartupCommandDispatcher?> StartupCommandDispatcherProperty =
        AvaloniaProperty.Register<TerminalPresentationHost, TerminalStartupCommandDispatcher?>(
            nameof(StartupCommandDispatcher));

    public static readonly StyledProperty<TerminalStartupCommandDispatchState?>
        StartupCommandDispatchStateProperty =
            AvaloniaProperty.Register<
                TerminalPresentationHost,
                TerminalStartupCommandDispatchState?>(
                nameof(StartupCommandDispatchState));

    public static readonly DirectProperty<TerminalPresentationHost, bool> IsLiveProperty =
        AvaloniaProperty.RegisterDirect<TerminalPresentationHost, bool>(
            nameof(IsLive),
            control => control.IsLive);

    public static readonly DirectProperty<TerminalPresentationHost, string?> InitializationErrorProperty =
        AvaloniaProperty.RegisterDirect<TerminalPresentationHost, string?>(
            nameof(InitializationError),
            control => control.InitializationError);

    public static readonly DirectProperty<TerminalPresentationHost, string> StatusTextProperty =
        AvaloniaProperty.RegisterDirect<TerminalPresentationHost, string>(
            nameof(StatusText),
            control => control.StatusText);

    public static readonly DirectProperty<TerminalPresentationHost, IBrush> StatusBrushProperty =
        AvaloniaProperty.RegisterDirect<TerminalPresentationHost, IBrush>(
            nameof(StatusBrush),
            control => control.StatusBrush);

    public static readonly DirectProperty<TerminalPresentationHost, bool> ShowFallbackProperty =
        AvaloniaProperty.RegisterDirect<TerminalPresentationHost, bool>(
            nameof(ShowFallback),
            control => control.ShowFallback);

    public static readonly DirectProperty<TerminalPresentationHost, string> StatusMessageProperty =
        AvaloniaProperty.RegisterDirect<TerminalPresentationHost, string>(
            nameof(StatusMessage),
            control => control.StatusMessage);

    private readonly Control _presentation;
    private readonly TerminalSessionHost? _nativeHost;
    private readonly ManagedTerminalSessionHost? _managedHost;
    private bool _isLive;
    private string? _initializationError;
    private string _statusText = "STARTING";
    private IBrush _statusBrush = StartingBrush;
    private bool _showFallback;
    private string _statusMessage = string.Empty;

    public TerminalPresentationHost()
        : this(TerminalPresentationSelector.SelectCurrent())
    {
    }

    internal TerminalPresentationHost(TerminalPresentationKind presentationKind)
    {
        PresentationKind = presentationKind;
        Focusable = true;
        AutomationProperties.SetName(this, "Interactive terminal");
        AutomationProperties.SetHelpText(
            this,
            "Terminal input and output for the active panel. Terminal status changes are announced politely.");
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        if (presentationKind == TerminalPresentationKind.Native)
        {
            _nativeHost = new TerminalSessionHost();
            _presentation = _nativeHost;
            _nativeHost.SessionSnapshotChanged += OnSessionSnapshotChanged;
            _nativeHost.SessionInitializationFailed += OnSessionInitializationFailed;
            _nativeHost.StartupCommandDispatchCompleted += OnStartupCommandDispatchCompleted;
            _nativeHost.ApplicationKeyPressed += OnApplicationKeyPressed;
        }
        else
        {
            _managedHost = new ManagedTerminalSessionHost();
            _presentation = _managedHost;
            _managedHost.SessionSnapshotChanged += OnSessionSnapshotChanged;
            _managedHost.SessionInitializationFailed += OnSessionInitializationFailed;
            _managedHost.StartupCommandDispatchCompleted += OnStartupCommandDispatchCompleted;
        }

        _presentation.PropertyChanged += OnPresentationPropertyChanged;
        Content = _presentation;
        SynchronizeChildProperties();
        SynchronizeState();
    }

    public event EventHandler<TerminalSessionSnapshotEventArgs>? SessionSnapshotChanged;

    public event EventHandler<TerminalSessionFailureEventArgs>? SessionInitializationFailed;

    public event EventHandler<TerminalStartupCommandDispatchEventArgs>?
        StartupCommandDispatchCompleted;

    public event EventHandler<NativeRendererKeyInputEventArgs>? ApplicationKeyPressed;

    public ISessionHostClient? SessionClient
    {
        get => GetValue(SessionClientProperty);
        set => SetValue(SessionClientProperty, value);
    }

    public EnsureTerminalSessionRequest? SessionRequest
    {
        get => GetValue(SessionRequestProperty);
        set => SetValue(SessionRequestProperty, value);
    }

    public ClientId? ClientId
    {
        get => GetValue(ClientIdProperty);
        set => SetValue(ClientIdProperty, value);
    }

    public TerminalStartupCommandDispatcher? StartupCommandDispatcher
    {
        get => GetValue(StartupCommandDispatcherProperty);
        set => SetValue(StartupCommandDispatcherProperty, value);
    }

    public TerminalStartupCommandDispatchState? StartupCommandDispatchState
    {
        get => GetValue(StartupCommandDispatchStateProperty);
        set => SetValue(StartupCommandDispatchStateProperty, value);
    }

    public bool IsLive
    {
        get => _isLive;
        private set => SetAndRaise(IsLiveProperty, ref _isLive, value);
    }

    public string? InitializationError
    {
        get => _initializationError;
        private set => SetAndRaise(InitializationErrorProperty, ref _initializationError, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetAndRaise(StatusTextProperty, ref _statusText, value);
    }

    public IBrush StatusBrush
    {
        get => _statusBrush;
        private set => SetAndRaise(StatusBrushProperty, ref _statusBrush, value);
    }

    public bool ShowFallback
    {
        get => _showFallback;
        private set => SetAndRaise(ShowFallbackProperty, ref _showFallback, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetAndRaise(StatusMessageProperty, ref _statusMessage, value);
    }

    internal TerminalPresentationKind PresentationKind { get; }

    internal Control Presentation => _presentation;

    public ValueTask SendTextAsync(
        string text,
        CancellationToken cancellationToken = default) => _nativeHost is not null
        ? _nativeHost.SendTextAsync(text, cancellationToken)
        : _managedHost!.SendTextAsync(text, cancellationToken);

    /// <summary>
    /// Replays application-prefix input directly to the PTY. This deliberately bypasses the
    /// terminal keymap because those physical strokes were already resolved by the shell.
    /// </summary>
    internal async ValueTask<bool> ReplayApplicationKeyStrokesAsync(
        IReadOnlyList<KeyStroke>? strokes,
        CancellationToken cancellationToken = default)
    {
        if (strokes is null || strokes.Count == 0)
        {
            return true;
        }

        var replay = new (TerminalKeyStroke? KeyStroke, string Text)[strokes.Count];
        for (var index = 0; index < strokes.Count; index++)
        {
            if (!ManagedTerminalInput.TryMapReplayStroke(
                    strokes[index],
                    out var keyStroke,
                    out var text))
            {
                return false;
            }

            replay[index] = (keyStroke, text);
        }

        foreach (var input in replay)
        {
            if (input.KeyStroke is { } keyStroke)
            {
                if (_nativeHost is not null)
                {
                    await _nativeHost.SendKeyAsync(keyStroke, cancellationToken);
                }
                else
                {
                    await ((IManagedTerminalInputSink)_managedHost!).SendKeyAsync(
                        keyStroke,
                        cancellationToken);
                }
            }
            else
            {
                await SendTextAsync(input.Text, cancellationToken);
            }
        }

        return true;
    }

    public ValueTask<string> ReadScreenAsync(CancellationToken cancellationToken = default) =>
        _nativeHost is not null
            ? _nativeHost.ReadScreenAsync(cancellationToken)
            : _managedHost!.ReadScreenAsync(cancellationToken);

    public bool TryCancelPendingPaste() =>
        _managedHost?.TryCancelPendingPaste() == true;

    public bool TryCancelPendingInteraction() =>
        _managedHost?.TryCancelPendingInteraction() == true;

    /// <summary>
    /// Moves keyboard input to the renderer that owns the terminal input contract.
    /// Focusing this wrapper is not sufficient after a native modal window closes:
    /// Avalonia can retain the wrapper in the focus scope while its renderer no longer
    /// receives keyboard events.
    /// </summary>
    internal bool RequestInputFocus() => _nativeHost is not null
        ? _nativeHost.RequestInputFocus()
        : _managedHost!.RequestInputFocus();

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (ReferenceEquals(e.Source, this))
        {
            RequestInputFocus();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SessionClientProperty
            || change.Property == SessionRequestProperty
            || change.Property == ClientIdProperty
            || change.Property == StartupCommandDispatcherProperty
            || change.Property == StartupCommandDispatchStateProperty)
        {
            SynchronizeChildProperties();
        }
    }

    private void SynchronizeChildProperties()
    {
        if (_nativeHost is not null)
        {
            _nativeHost.StartupCommandDispatchState = StartupCommandDispatchState;
            _nativeHost.SessionClient = SessionClient;
            _nativeHost.SessionRequest = SessionRequest;
            _nativeHost.ClientId = ClientId;
            _nativeHost.StartupCommandDispatcher = StartupCommandDispatcher;
        }
        else
        {
            _managedHost!.StartupCommandDispatchState = StartupCommandDispatchState;
            _managedHost!.SessionClient = SessionClient;
            _managedHost.SessionRequest = SessionRequest;
            _managedHost.ClientId = ClientId;
            _managedHost.StartupCommandDispatcher = StartupCommandDispatcher;
        }
    }

    private void OnPresentationPropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.Property.Name is nameof(IsLive)
            or nameof(InitializationError)
            or nameof(StatusText)
            or nameof(StatusBrush)
            or nameof(ShowFallback)
            or nameof(StatusMessage))
        {
            SynchronizeState();
        }
    }

    private void SynchronizeState()
    {
        if (_nativeHost is not null)
        {
            IsLive = _nativeHost.IsLive;
            InitializationError = _nativeHost.InitializationError;
            StatusText = _nativeHost.StatusText;
            StatusBrush = _nativeHost.StatusBrush;
            ShowFallback = _nativeHost.ShowFallback;
            StatusMessage = _nativeHost.StatusMessage;
        }
        else
        {
            IsLive = _managedHost!.IsLive;
            InitializationError = _managedHost.InitializationError;
            StatusText = _managedHost.StatusText;
            StatusBrush = _managedHost.StatusBrush;
            ShowFallback = _managedHost.ShowFallback;
            StatusMessage = _managedHost.StatusMessage;
        }

        AutomationProperties.SetItemStatus(
            this,
            string.IsNullOrWhiteSpace(StatusMessage)
                ? StatusText
                : $"{StatusText}: {StatusMessage}");
    }

    private void OnSessionSnapshotChanged(
        object? sender,
        TerminalSessionSnapshotEventArgs e)
    {
        _ = sender;
        SessionSnapshotChanged?.Invoke(this, e);
    }

    private void OnSessionInitializationFailed(
        object? sender,
        TerminalSessionFailureEventArgs e)
    {
        _ = sender;
        SessionInitializationFailed?.Invoke(this, e);
    }

    private void OnStartupCommandDispatchCompleted(
        object? sender,
        TerminalStartupCommandDispatchEventArgs e)
    {
        _ = sender;
        StartupCommandDispatchCompleted?.Invoke(this, e);
    }

    private void OnApplicationKeyPressed(
        object? sender,
        NativeRendererKeyInputEventArgs e)
    {
        _ = sender;
        ApplicationKeyPressed?.Invoke(this, e);
    }
}
