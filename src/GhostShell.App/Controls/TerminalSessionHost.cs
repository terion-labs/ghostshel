using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Controls;

public sealed class TerminalSessionHost : NativeControlHost
{
    private static readonly IBrush StartingBrush = Brush.Parse("#8B8B91");
    private static readonly IBrush LiveBrush = Brush.Parse("#3FB950");
    private static readonly IBrush UnavailableBrush = Brush.Parse("#FF7A55");
    private static readonly IBrush ExitedBrush = Brush.Parse("#FFB224");

    public static readonly StyledProperty<ISessionHostClient?> SessionClientProperty =
        AvaloniaProperty.Register<TerminalSessionHost, ISessionHostClient?>(nameof(SessionClient));

    /// <summary>
    /// The live render profile, applied to the running surface. Separate from the
    /// session request because a font change must not restart the session.
    /// </summary>
    public static readonly StyledProperty<TerminalRenderProfileSnapshot?> RenderProfileProperty =
        AvaloniaProperty.Register<TerminalSessionHost, TerminalRenderProfileSnapshot?>(
            nameof(RenderProfile));

    public static readonly StyledProperty<EnsureTerminalSessionRequest?> SessionRequestProperty =
        AvaloniaProperty.Register<TerminalSessionHost, EnsureTerminalSessionRequest?>(nameof(SessionRequest));

    public static readonly StyledProperty<ClientId?> ClientIdProperty =
        AvaloniaProperty.Register<TerminalSessionHost, ClientId?>(nameof(ClientId));

    public static readonly StyledProperty<TerminalStartupCommandDispatcher?> StartupCommandDispatcherProperty =
        AvaloniaProperty.Register<TerminalSessionHost, TerminalStartupCommandDispatcher?>(
            nameof(StartupCommandDispatcher));

    public static readonly StyledProperty<TerminalStartupCommandDispatchState?>
        StartupCommandDispatchStateProperty =
            AvaloniaProperty.Register<
                TerminalSessionHost,
                TerminalStartupCommandDispatchState?>(
                nameof(StartupCommandDispatchState));

    public static readonly DirectProperty<TerminalSessionHost, bool> IsLiveProperty =
        AvaloniaProperty.RegisterDirect<TerminalSessionHost, bool>(
            nameof(IsLive),
            control => control.IsLive);

    public static readonly DirectProperty<TerminalSessionHost, string?> InitializationErrorProperty =
        AvaloniaProperty.RegisterDirect<TerminalSessionHost, string?>(
            nameof(InitializationError),
            control => control.InitializationError);

    public static readonly DirectProperty<TerminalSessionHost, string> StatusTextProperty =
        AvaloniaProperty.RegisterDirect<TerminalSessionHost, string>(
            nameof(StatusText),
            control => control.StatusText);

    public static readonly DirectProperty<TerminalSessionHost, IBrush> StatusBrushProperty =
        AvaloniaProperty.RegisterDirect<TerminalSessionHost, IBrush>(
            nameof(StatusBrush),
            control => control.StatusBrush);

    public static readonly DirectProperty<TerminalSessionHost, bool> ShowFallbackProperty =
        AvaloniaProperty.RegisterDirect<TerminalSessionHost, bool>(
            nameof(ShowFallback),
            control => control.ShowFallback);

    public static readonly DirectProperty<TerminalSessionHost, string> StatusMessageProperty =
        AvaloniaProperty.RegisterDirect<TerminalSessionHost, string>(
            nameof(StatusMessage),
            control => control.StatusMessage);

    private readonly DispatcherTimer _processMonitor;
    private CancellationTokenSource? _attachmentLifetime;
    private IPlatformHandle? _nativeHost;
    private ISessionHostClient? _attachedClient;
    private ClientId? _attachedClientId;
    private SessionId? _attachedSessionId;
    private AttachmentId? _attachmentId;
    private InputLeaseId? _inputLeaseId;
    private long _initializationGeneration;
    private SessionLifecycle? _lastNotifiedLifecycle;
    private SessionHealth? _lastNotifiedHealth;
    private string? _lastNotifiedFailureCode;
    private bool _isLive;
    private string? _initializationError;
    private string _statusText = "STARTING";
    private IBrush _statusBrush = StartingBrush;
    private bool _showFallback;
    private string _statusMessage = string.Empty;

    public TerminalSessionHost()
    {
        Focusable = true;
        AutomationProperties.SetName(this, "Native interactive terminal");
        AutomationProperties.SetHelpText(
            this,
            "Native terminal surface. Terminal status changes are announced politely.");
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        AutomationProperties.SetItemStatus(this, "STARTING");
        _processMonitor = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _processMonitor.Tick += OnProcessMonitorTick;
    }

    public event EventHandler<TerminalSessionSnapshotEventArgs>? SessionSnapshotChanged;

    public event EventHandler<TerminalSessionFailureEventArgs>? SessionInitializationFailed;

    public event EventHandler<TerminalStartupCommandDispatchEventArgs>?
        StartupCommandDispatchCompleted;

    /// <summary>
    /// Raised synchronously when the native terminal receives a physical key that may belong to
    /// the application keymap. Set <see cref="NativeRendererKeyInputEventArgs.Handled"/> before
    /// returning to keep the key out of the PTY.
    /// </summary>
    public event EventHandler<NativeRendererKeyInputEventArgs>? ApplicationKeyPressed;

    public ISessionHostClient? SessionClient
    {
        get => GetValue(SessionClientProperty);
        set => SetValue(SessionClientProperty, value);
    }

    public TerminalRenderProfileSnapshot? RenderProfile
    {
        get => GetValue(RenderProfileProperty);
        set => SetValue(RenderProfileProperty, value);
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

    public async ValueTask SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var client = RequireClient();
        var request = RequireSessionRequest();
        var leaseId = _inputLeaseId
            ?? throw new InvalidOperationException("The terminal input lease is unavailable.");
        var result = await client.WriteTerminalAsync(
            new TerminalWriteRequest(request.SessionId, leaseId, text),
            NewContext(),
            cancellationToken);
        _ = RequireSuccess(result);
    }

    internal async ValueTask SendKeyAsync(
        TerminalKeyStroke keyStroke,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyStroke);
        var client = RequireClient();
        var request = RequireSessionRequest();
        var leaseId = _inputLeaseId
            ?? throw new InvalidOperationException("The terminal input lease is unavailable.");
        var result = await client.SendTerminalKeyAsync(
            new TerminalKeyRequest(request.SessionId, leaseId, keyStroke),
            NewContext(),
            cancellationToken);
        _ = RequireSuccess(result);
    }

    public async ValueTask<string> ReadScreenAsync(CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        var request = RequireSessionRequest();
        var result = await client.ReadTerminalScreenAsync(
            request.SessionId,
            NewContext(),
            cancellationToken);
        return RequireSuccess(result).PlainText;
    }

    /// <summary>
    /// Gives the terminal the keyboard.
    ///
    /// Avalonia never sees focus move into a native child view, so <c>IsFocused</c>
    /// is false even while the surface owns the keyboard. Treating that as "not
    /// focused yet" and calling <see cref="InputElement.Focus"/> alone moved the
    /// keyboard to Avalonia's own top-level view and never handed it back — the
    /// surface stopped receiving key events entirely, while still drawing output.
    /// Every caller of this is a panel becoming active, so it happened constantly.
    ///
    /// Both steps are needed: Avalonia's logical focus keeps the shell's own
    /// tracking honest, and the native focus is the only thing that makes the
    /// surface first responder and able to receive keys at all.
    /// </summary>
    internal bool RequestInputFocus()
    {
        var focused = IsFocused || Focus();
        _ = FocusTerminalAsync();
        return focused;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var host = base.CreateNativeControlCore(parent);
        _nativeHost = host;
        RestartSession(host);
        return host;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _nativeHost = null;
        StopSession();
        SetState(TerminalHostState.Stopped);
        base.DestroyNativeControlCore(control);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _ = FocusTerminalAsync();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            _ = ResizeTerminalAsync();
        }
        else if (change.Property == RenderProfileProperty)
        {
            _ = ApplyRenderProfileAsync();
        }
        else if (change.Property == SessionClientProperty
            || change.Property == SessionRequestProperty
            || change.Property == ClientIdProperty)
        {
            if (_nativeHost is { } host)
            {
                RestartSession(host);
            }
        }
        else if (change.Property == StartupCommandDispatchStateProperty)
        {
            // The state validates the current session owner before mutation, so a state binding
            // that arrives last may safely trigger without combining different panel instances.
            if (IsLive && _attachmentLifetime is { } lifetime)
            {
                _ = SendStartupCommandsIfNeededAsync(
                    _initializationGeneration,
                    lifetime.Token);
            }
        }
        else if (change.Property == StartupCommandDispatcherProperty
            && IsLive
            && _attachmentLifetime is { } lifetime)
        {
            _ = SendStartupCommandsIfNeededAsync(
                _initializationGeneration,
                lifetime.Token);
        }
    }

    /// <summary>
    /// Pushes new typography to the running surface. A terminal that is not yet
    /// attached needs nothing: it will launch with the profile it is given.
    /// </summary>
    private async Task ApplyRenderProfileAsync()
    {
        if (RenderProfile is not { } profile
            || _attachedClient is not { } client
            || _attachedSessionId is not { } sessionId
            || _attachmentId is not { } attachmentId
            || _attachmentLifetime is not { } lifetime)
        {
            return;
        }

        try
        {
            _ = await client.UpdateTerminalRenderProfileAsync(
                new UpdateTerminalRenderProfileRequest(sessionId, attachmentId, profile),
                NewContext(),
                lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // The panel closed while the change was in flight.
        }
    }

    /// <summary>
    /// The radius the panel card draws, so the native view can round its own
    /// layer to match. Avalonia clips its own visuals to the card, but a native
    /// child view is not one of them.
    /// </summary>
    public static readonly StyledProperty<CornerRadius> HostCornerRadiusProperty =
        AvaloniaProperty.Register<TerminalSessionHost, CornerRadius>(nameof(HostCornerRadius));

    public CornerRadius HostCornerRadius
    {
        get => GetValue(HostCornerRadiusProperty);
        set => SetValue(HostCornerRadiusProperty, value);
    }

    private NativeRendererCorners HostCorners => new(
        HostCornerRadius.TopLeft,
        HostCornerRadius.TopRight,
        HostCornerRadius.BottomRight,
        HostCornerRadius.BottomLeft);

    /// <summary>
    /// Raised on the UI thread when the native view takes focus, so the shell can
    /// mark this panel active. Clicking a native view is invisible to Avalonia.
    /// </summary>
    public event EventHandler? NativeFocusGained;

    private static int _nativeFocusReported;

    private void NotifyNativeFocus()
    {
        // The native view only raises this once it is genuinely the window's first
        // responder, which is the one precondition for it receiving a keystroke at
        // all. Reporting the first few makes the difference between "focus never
        // reached the surface" and "focus reached it and the keys went missing
        // later" visible, instead of both looking like silence.
        if (Interlocked.Increment(ref _nativeFocusReported) <= 3)
        {
            Console.Error.WriteLine(
                "[ghostshell:input] the terminal surface took the keyboard");
        }

        Dispatcher.UIThread.Post(() => NativeFocusGained?.Invoke(this, EventArgs.Empty));
    }

    private void RestartSession(IPlatformHandle host)
    {
        StopSession();
        _lastNotifiedLifecycle = null;
        _lastNotifiedHealth = null;
        _lastNotifiedFailureCode = null;
        if (SessionClient is null || SessionRequest is null || ClientId is null)
        {
            SetState(TerminalHostState.Waiting);
            return;
        }

        var generation = ++_initializationGeneration;
        _attachmentLifetime = new CancellationTokenSource();
        _ = InitializeSessionAsync(host, generation, _attachmentLifetime.Token);
    }

    private void StopSession()
    {
        _initializationGeneration++;
        _processMonitor.Stop();
        _attachmentLifetime?.Cancel();
        _attachmentLifetime?.Dispose();
        _attachmentLifetime = null;
        DetachRenderer();
    }

    private async Task InitializeSessionAsync(
        IPlatformHandle host,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = RequireClient();
            var request = RequireSessionRequest();
            var clientId = ClientId
                ?? throw new InvalidOperationException("The desktop client identity is unavailable.");
            if (host.Handle == 0)
            {
                throw new InvalidOperationException("Avalonia did not provide a native terminal host.");
            }

            var handleDescriptor = host.HandleDescriptor;
            if (string.IsNullOrWhiteSpace(handleDescriptor))
            {
                throw new InvalidOperationException("Avalonia did not describe the native terminal host.");
            }

            var ensured = await client.EnsureTerminalSessionAsync(
                request,
                NewContext(),
                cancellationToken);
            _ = RequireSuccess(ensured);

            var attachment = await AttachInteractiveAsync(
                client,
                request,
                clientId,
                cancellationToken);
            if (generation != _initializationGeneration)
            {
                await DetachStaleAttachmentAsync(
                    client,
                    clientId,
                    request.SessionId,
                    attachment.Attachment.Id);
                return;
            }

            _attachedClient = client;
            _attachedClientId = clientId;
            _attachedSessionId = request.SessionId;
            _attachmentId = attachment.Attachment.Id;

            var rendererAttached = await client.AttachTerminalRendererAsync(
                new AttachTerminalRendererRequest(
                    request.SessionId,
                    attachment.Attachment.Id,
                    new NativeRendererHost(
                        handleDescriptor,
                        host.Handle,
                        CurrentViewport(),
                        InterceptApplicationKey,
                        PhysicalInputGate: null,
                        HostCorners,
                        FocusObserver: NotifyNativeFocus)),
                NewContext(),
                cancellationToken);
            _ = RequireSuccess(rendererAttached);

            var lease = await client.AcquireInputLeaseAsync(
                new AcquireInputLeaseRequest(
                    request.SessionId,
                    attachment.Attachment.Id,
                    TimeSpan.FromHours(8)),
                NewContext(),
                cancellationToken);
            var leaseDecision = RequireSuccess(lease);
            if (!leaseDecision.Granted || leaseDecision.Lease is null)
            {
                throw new InvalidOperationException(leaseDecision.Detail);
            }

            _inputLeaseId = leaseDecision.Lease.Id;
            var snapshotResult = await client.GetSnapshotAsync(
                request.SessionId,
                NewContext(),
                cancellationToken);
            if (generation != _initializationGeneration)
            {
                return;
            }

            ApplySnapshot(RequireSuccess(snapshotResult));
            _processMonitor.Start();
            await ResizeTerminalAsync();
            await SendStartupCommandsIfNeededAsync(generation, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (generation == _initializationGeneration)
            {
                SetState(TerminalHostState.Stopped);
            }
        }
        catch (Exception exception)
        {
            if (generation == _initializationGeneration)
            {
                SetState(
                    OperatingSystem.IsMacOS()
                        ? TerminalHostState.Error
                        : TerminalHostState.Unavailable,
                    exception.Message);
                SessionInitializationFailed?.Invoke(
                    this,
                    new TerminalSessionFailureEventArgs(new SessionFailure(
                        "terminal_session_initialization_failed",
                        "The terminal session could not be initialized.",
                        Retryable: true)));
            }

            Trace.TraceError("Unable to attach the terminal session: {0}", exception);
        }
    }

    private static async ValueTask DetachStaleAttachmentAsync(
        ISessionHostClient client,
        ClientId clientId,
        SessionId sessionId,
        AttachmentId attachmentId)
    {
        try
        {
            _ = await client.DetachAsync(
                new DetachSessionRequest(attachmentId, sessionId),
                OperationContext.ForHuman(clientId),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            Trace.TraceError("Unable to detach a superseded terminal renderer: {0}", exception);
        }
    }

    private async Task<AttachmentResult> AttachInteractiveAsync(
        ISessionHostClient client,
        EnsureTerminalSessionRequest request,
        ClientId clientId,
        CancellationToken cancellationToken)
    {
        HostResult<AttachmentResult>? lastResult = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            lastResult = await client.AttachAsync(
                new AttachSessionRequest(
                    request.SessionId,
                    clientId,
                    AttachmentKind.Interactive,
                    CurrentViewport(),
                    InteractiveCapabilities()),
                NewContext(),
                cancellationToken);
            if (lastResult is HostResult<AttachmentResult>.Success success)
            {
                return success.Value;
            }

            if (lastResult is not HostResult<AttachmentResult>.Failure
                {
                    Error.Code: HostErrorCode.CapabilityNotSupported,
                })
            {
                return RequireSuccess(lastResult);
            }

            // Avalonia can construct the replacement native host before the old host's
            // asynchronous detach finishes during tab switches or overlay transitions.
            await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
        }

        return RequireSuccess(lastResult
            ?? throw new InvalidOperationException("The terminal attachment did not start."));
    }

    private async void OnProcessMonitorTick(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (SessionClient is null || SessionRequest is null)
        {
            _processMonitor.Stop();
            return;
        }

        try
        {
            var result = await SessionClient.GetSnapshotAsync(
                SessionRequest.SessionId,
                NewContext(),
                _attachmentLifetime?.Token ?? CancellationToken.None);
            ApplySnapshot(RequireSuccess(result));
            await SendStartupCommandsIfNeededAsync(
                _initializationGeneration,
                _attachmentLifetime?.Token ?? CancellationToken.None);
            if (RequireSuccess(result).Descriptor.Lifecycle
                is SessionLifecycle.Closed or SessionLifecycle.Failed)
            {
                _processMonitor.Stop();
            }
        }
        catch (OperationCanceledException)
        {
            _processMonitor.Stop();
        }
        catch (Exception exception)
        {
            _processMonitor.Stop();
            SetState(TerminalHostState.Error, exception.Message);
            Trace.TraceError("Unable to read the terminal session state: {0}", exception);
        }
    }

    /// <summary>
    /// Failing to focus the terminal is exactly as visible to the user as a dead
    /// keyboard: the surface keeps drawing and ignores every keystroke. This used to
    /// go to <see cref="Trace"/>, which nothing in this application listens to, so
    /// the one failure that explains the symptom was the one nobody could see.
    /// </summary>
    private static void ReportFocusFailure(string reason) =>
        Console.Error.WriteLine(
            $"[ghostshell:input] the terminal could not take the keyboard: {reason}");

    private async Task FocusTerminalAsync()
    {
        if (!IsLive || SessionClient is null || SessionRequest is null)
        {
            return;
        }

        try
        {
            if (_attachmentId is { } attachmentId)
            {
                var lease = await SessionClient.AcquireInputLeaseAsync(
                    new AcquireInputLeaseRequest(
                        SessionRequest.SessionId,
                        attachmentId,
                        TimeSpan.FromHours(8)),
                    NewContext(),
                    _attachmentLifetime?.Token ?? CancellationToken.None);
                var decision = RequireSuccess(lease);
                if (decision.Granted && decision.Lease is not null)
                {
                    _inputLeaseId = decision.Lease.Id;
                }
                else
                {
                    ReportFocusFailure($"the input lease was refused — {decision.Detail}");
                }
            }

            var result = await SessionClient.FocusTerminalAsync(
                SessionRequest.SessionId,
                NewContext(),
                _attachmentLifetime?.Token ?? CancellationToken.None);
            _ = RequireSuccess(result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReportFocusFailure(exception.Message);
        }
    }

    private async Task ResizeTerminalAsync()
    {
        if (_attachmentId is not { } attachmentId
            || SessionClient is null
            || SessionRequest is null
            || Bounds.Width <= 0
            || Bounds.Height <= 0)
        {
            return;
        }

        try
        {
            var result = await SessionClient.ResizeTerminalAsync(
                new TerminalResizeRequest(
                    SessionRequest.SessionId,
                    attachmentId,
                    CurrentViewport()),
                NewContext(),
                _attachmentLifetime?.Token ?? CancellationToken.None);
            _ = RequireSuccess(result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError("Unable to resize the terminal session: {0}", exception);
        }
    }

    private async Task SendStartupCommandsIfNeededAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        if (!IsLive
            || generation != _initializationGeneration
            || SessionClient is null
            || SessionRequest is null
            || StartupCommandDispatcher is null
            || StartupCommandDispatchState is null
            || _inputLeaseId is not { } leaseId)
        {
            return;
        }

        var dispatchState = StartupCommandDispatchState;
        var commands = dispatchState.Commands;
        if (commands.Count == 0)
        {
            return;
        }

        try
        {
            var client = SessionClient;
            var request = SessionRequest;
            var dispatcher = StartupCommandDispatcher;
            var result = await dispatchState.DispatchIfNeededAsync(
                request.Owner.PanelId,
                (batchContext, token) => dispatcher.DispatchAsync(
                    client,
                    request.SessionId,
                    leaseId,
                    commands,
                    batchContext,
                    token),
                cancellationToken);
            if (result is not null)
            {
                // Observational only: the runtime-owned state published the authoritative typed
                // outcome directly to its VM before returning it here.
                StartupCommandDispatchCompleted?.Invoke(
                    this,
                    new TerminalStartupCommandDispatchEventArgs(
                        dispatchState.Context,
                        result));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "The startup-command dispatcher failed before returning a typed result: {0}",
                exception.GetType().Name);
        }
    }

    private void DetachRenderer()
    {
        if (_attachmentId is not { } attachmentId
            || _attachedClient is not { } client
            || _attachedClientId is not { } clientId
            || _attachedSessionId is not { } sessionId)
        {
            return;
        }

        _attachmentId = null;
        _inputLeaseId = null;
        _attachedClient = null;
        _attachedClientId = null;
        _attachedSessionId = null;
        var result = client.DetachAsync(
            new DetachSessionRequest(attachmentId, sessionId),
            OperationContext.ForHuman(clientId),
            CancellationToken.None);
        if (!result.IsCompletedSuccessfully)
        {
            _ = ObserveDetachAsync(result);
        }
    }

    private static async Task ObserveDetachAsync(ValueTask<HostResult<Unit>> result)
    {
        try
        {
            _ = await result;
        }
        catch (Exception exception)
        {
            Trace.TraceError("Unable to detach the terminal renderer: {0}", exception);
        }
    }

    private void ApplySnapshot(SessionSnapshot snapshot)
    {
        var descriptor = snapshot.Descriptor;
        switch (descriptor.Lifecycle)
        {
            case SessionLifecycle.Starting:
                SetState(TerminalHostState.Starting, descriptor.StatusDetail);
                break;
            case SessionLifecycle.Active when descriptor.Health == SessionHealth.Healthy:
                SetState(TerminalHostState.Live, descriptor.StatusDetail);
                break;
            case SessionLifecycle.Active:
                SetState(TerminalHostState.Unavailable, descriptor.StatusDetail);
                break;
            case SessionLifecycle.Closing:
                SetState(TerminalHostState.Starting, descriptor.StatusDetail);
                break;
            case SessionLifecycle.Closed:
                SetState(TerminalHostState.Exited, descriptor.StatusDetail);
                break;
            case SessionLifecycle.Failed:
                SetState(TerminalHostState.Error, descriptor.StatusDetail);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(snapshot),
                    descriptor.Lifecycle,
                    "Unknown session lifecycle state.");
        }


        var failureCode = descriptor.Failure?.StableCode;
        if (_lastNotifiedLifecycle != descriptor.Lifecycle
            || _lastNotifiedHealth != descriptor.Health
            || !string.Equals(_lastNotifiedFailureCode, failureCode, StringComparison.Ordinal))
        {
            _lastNotifiedLifecycle = descriptor.Lifecycle;
            _lastNotifiedHealth = descriptor.Health;
            _lastNotifiedFailureCode = failureCode;
            SessionSnapshotChanged?.Invoke(this, new TerminalSessionSnapshotEventArgs(snapshot));
        }
    }

    private void SetState(TerminalHostState state, string? detail = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetState(state, detail));
            return;
        }

        IsLive = state == TerminalHostState.Live;
        InitializationError = state is TerminalHostState.Error or TerminalHostState.Unavailable
            ? detail
            : null;
        ShowFallback = state is TerminalHostState.Error
            or TerminalHostState.Unavailable
            or TerminalHostState.Exited;

        (StatusText, StatusBrush, StatusMessage) = state switch
        {
            TerminalHostState.Waiting => ("WAITING", StartingBrush, string.Empty),
            TerminalHostState.Starting => ("STARTING", StartingBrush, detail ?? string.Empty),
            TerminalHostState.Live => ("LIVE", LiveBrush, string.Empty),
            TerminalHostState.Unavailable => ("UNAVAILABLE", UnavailableBrush, detail ?? "Terminal unavailable."),
            TerminalHostState.Exited => ("EXITED", ExitedBrush, detail ?? "The terminal session ended."),
            TerminalHostState.Error => ("ERROR", UnavailableBrush, detail ?? "Unable to start the terminal."),
            TerminalHostState.Stopped => ("STOPPED", StartingBrush, string.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };
        AutomationProperties.SetItemStatus(
            this,
            string.IsNullOrWhiteSpace(StatusMessage)
                ? StatusText
                : $"{StatusText}: {StatusMessage}");
    }

    private ViewportDescriptor CurrentViewport() =>
        new(
            Bounds.Width,
            Bounds.Height,
            TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);

    private OperationContext NewContext() => OperationContext.ForHuman(
        ClientId ?? throw new InvalidOperationException("The desktop client identity is unavailable."));

    private ISessionHostClient RequireClient() =>
        SessionClient ?? throw new InvalidOperationException("The session-host client is unavailable.");

    private EnsureTerminalSessionRequest RequireSessionRequest() =>
        SessionRequest ?? throw new InvalidOperationException("The terminal session request is unavailable.");

    private static T RequireSuccess<T>(HostResult<T> result) => result switch
    {
        HostResult<T>.Success success => success.Value,
        HostResult<T>.Failure failure => throw new InvalidOperationException(
            $"{failure.Error.StableCode}: {failure.Error.Message}"),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static CapabilitySet InteractiveCapabilities() => new(
    [
        SessionCapabilities.AttachInteractive,
        SessionCapabilities.InputLease,
        SessionCapabilities.NativeRenderer,
        SessionCapabilities.TerminalAgentInputBarrier,
        SessionCapabilities.TerminalFocus,
        SessionCapabilities.TerminalReadScreen,
        SessionCapabilities.TerminalResize,
        SessionCapabilities.TerminalWrite,
    ]);

    internal bool InterceptApplicationKey(NativeRendererKeyInput input)
    {
        // Repeats for a consumed press are retained by the native shim. A repeat
        // that reaches this boundary therefore belongs to a press already sent
        // to the PTY; it must not become a later application-sequence suffix.
        if (input.IsRepeat)
        {
            return false;
        }

        var args = new NativeRendererKeyInputEventArgs(input);
        ApplicationKeyPressed?.Invoke(this, args);
        return args.Handled;
    }

    private enum TerminalHostState
    {
        Waiting,
        Starting,
        Live,
        Unavailable,
        Exited,
        Error,
        Stopped,
    }
}
