using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Controls;

public sealed class BrowserPresentationHost : ContentControl
{
    private static readonly IBrush WaitingBrush = Brush.Parse("#8B8B91");
    private static readonly IBrush ReadyBrush = Brush.Parse("#3FB950");
    private static readonly IBrush LoadingBrush = Brush.Parse("#D79B57");
    private static readonly IBrush FailedBrush = Brush.Parse("#FF7A55");

    public static readonly StyledProperty<ISessionHostClient?> SessionClientProperty =
        AvaloniaProperty.Register<BrowserPresentationHost, ISessionHostClient?>(
            nameof(SessionClient));

    public static readonly StyledProperty<EnsureBrowserSessionRequest?> SessionRequestProperty =
        AvaloniaProperty.Register<BrowserPresentationHost, EnsureBrowserSessionRequest?>(
            nameof(SessionRequest));

    public static readonly StyledProperty<ClientId?> ClientIdProperty =
        AvaloniaProperty.Register<BrowserPresentationHost, ClientId?>(
            nameof(ClientId));

    public static readonly StyledProperty<BrowserRendererView?> RendererViewProperty =
        AvaloniaProperty.Register<BrowserPresentationHost, BrowserRendererView?>(
            nameof(RendererView));

    public static readonly DirectProperty<BrowserPresentationHost, string>
        AddressTextProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, string>(
                nameof(AddressText),
                control => control.AddressText,
                (control, value) => control.AddressText = value);

    public static readonly DirectProperty<BrowserPresentationHost, string>
        StatusTextProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, string>(
                nameof(StatusText),
                control => control.StatusText);

    public static readonly DirectProperty<BrowserPresentationHost, string>
        StatusMessageProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, string>(
                nameof(StatusMessage),
                control => control.StatusMessage);

    public static readonly DirectProperty<BrowserPresentationHost, IBrush>
        StatusBrushProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, IBrush>(
                nameof(StatusBrush),
                control => control.StatusBrush);

    public static readonly DirectProperty<BrowserPresentationHost, bool>
        IsLiveProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, bool>(
                nameof(IsLive),
                control => control.IsLive);

    public static readonly DirectProperty<BrowserPresentationHost, bool>
        IsLoadingProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, bool>(
                nameof(IsLoading),
                control => control.IsLoading);

    public static readonly DirectProperty<BrowserPresentationHost, bool>
        CanGoBackProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, bool>(
                nameof(CanGoBack),
                control => control.CanGoBack);

    public static readonly DirectProperty<BrowserPresentationHost, bool>
        CanGoForwardProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, bool>(
                nameof(CanGoForward),
                control => control.CanGoForward);

    public static readonly DirectProperty<BrowserPresentationHost, bool>
        ShowFallbackProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, bool>(
                nameof(ShowFallback),
                control => control.ShowFallback);

    private CancellationTokenSource? _attachmentLifetime;
    private ISessionHostClient? _attachedClient;
    private ClientId? _attachedClientId;
    private SessionId? _attachedSessionId;
    private AttachmentId? _attachmentId;
    private IBrowserRenderer? _subscribedRenderer;
    private long _initializationGeneration;
    private bool _isAttachedToVisualTree;
    private string _addressText = string.Empty;
    private string _statusText = "WAITING";
    private string _statusMessage = string.Empty;
    private IBrush _statusBrush = WaitingBrush;
    private bool _isLive;
    private bool _isLoading;
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _showFallback = true;

    public BrowserPresentationHost()
    {
        Focusable = true;
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        AutomationProperties.SetName(this, "Native web browser");
        AutomationProperties.SetHelpText(
            this,
            "Operating-system web content for this browser panel.");
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        AutomationProperties.SetItemStatus(this, StatusText);
    }

    public event EventHandler<BrowserStateChangedEventArgs>? BrowserStateChanged;

    public ISessionHostClient? SessionClient
    {
        get => GetValue(SessionClientProperty);
        set => SetValue(SessionClientProperty, value);
    }

    public EnsureBrowserSessionRequest? SessionRequest
    {
        get => GetValue(SessionRequestProperty);
        set => SetValue(SessionRequestProperty, value);
    }

    public ClientId? ClientId
    {
        get => GetValue(ClientIdProperty);
        set => SetValue(ClientIdProperty, value);
    }

    public BrowserRendererView? RendererView
    {
        get => GetValue(RendererViewProperty);
        set => SetValue(RendererViewProperty, value);
    }

    public string AddressText
    {
        get => _addressText;
        set => SetAndRaise(
            AddressTextProperty,
            ref _addressText,
            value ?? string.Empty);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetAndRaise(StatusTextProperty, ref _statusText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetAndRaise(
            StatusMessageProperty,
            ref _statusMessage,
            value);
    }

    public IBrush StatusBrush
    {
        get => _statusBrush;
        private set => SetAndRaise(StatusBrushProperty, ref _statusBrush, value);
    }

    public bool IsLive
    {
        get => _isLive;
        private set => SetAndRaise(IsLiveProperty, ref _isLive, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetAndRaise(IsLoadingProperty, ref _isLoading, value);
    }

    public bool CanGoBack
    {
        get => _canGoBack;
        private set => SetAndRaise(CanGoBackProperty, ref _canGoBack, value);
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        private set => SetAndRaise(CanGoForwardProperty, ref _canGoForward, value);
    }

    public bool ShowFallback
    {
        get => _showFallback;
        private set => SetAndRaise(ShowFallbackProperty, ref _showFallback, value);
    }

    internal bool RequestInputFocus() =>
        RendererView?.View.Focus() ?? Focus();

    public async ValueTask NavigateAddressAsync(
        string? text,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveAddress(text, out var address))
        {
            SetOperationMessage(
                "Enter a complete HTTP or HTTPS address, such as https://example.com.");
            return;
        }

        var client = RequireAttachedClient();
        var sessionId = RequireAttachedSessionId();
        var result = await client.NavigateBrowserAsync(
            new BrowserNavigateRequest(sessionId, address),
            NewContext(),
            cancellationToken);
        ApplyOperationResult(result);
    }

    public async ValueTask GoBackAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequireAttachedClient().GoBackBrowserAsync(
            RequireAttachedSessionId(),
            NewContext(),
            cancellationToken);
        ApplyOperationResult(result);
    }

    public async ValueTask GoForwardAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequireAttachedClient().GoForwardBrowserAsync(
            RequireAttachedSessionId(),
            NewContext(),
            cancellationToken);
        ApplyOperationResult(result);
    }

    public async ValueTask ReloadAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequireAttachedClient().ReloadBrowserAsync(
            RequireAttachedSessionId(),
            NewContext(),
            cancellationToken);
        ApplyOperationResult(result);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequireAttachedClient().StopBrowserAsync(
            RequireAttachedSessionId(),
            NewContext(),
            cancellationToken);
        ApplyOperationResult(result);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttachedToVisualTree = true;
        RestartSession();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttachedToVisualTree = false;
        StopSession();
        SetWaitingState("Browser renderer detached.");
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (ReferenceEquals(e.Source, this))
        {
            RendererView?.View.Focus();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SessionClientProperty
            || change.Property == SessionRequestProperty
            || change.Property == ClientIdProperty
            || change.Property == RendererViewProperty)
        {
            if (change.Property == RendererViewProperty)
            {
                Content = RendererView?.View;
            }

            if (_isAttachedToVisualTree)
            {
                RestartSession();
            }
        }
    }

    private void RestartSession()
    {
        StopSession();
        Content = RendererView?.View;
        if (SessionClient is null
            || SessionRequest is null
            || ClientId is null
            || RendererView is null)
        {
            SetWaitingState("Waiting for the native browser adapter.");
            return;
        }

        SetWaitingState("Starting the native browser…", "STARTING");
        var generation = ++_initializationGeneration;
        _attachmentLifetime = new CancellationTokenSource();
        _ = InitializeSessionAsync(generation, _attachmentLifetime.Token);
    }

    private void StopSession()
    {
        _initializationGeneration++;
        _attachmentLifetime?.Cancel();
        _attachmentLifetime?.Dispose();
        _attachmentLifetime = null;
        UnsubscribeRenderer();

        var client = _attachedClient;
        var clientId = _attachedClientId;
        var sessionId = _attachedSessionId;
        var attachmentId = _attachmentId;
        _attachedClient = null;
        _attachedClientId = null;
        _attachedSessionId = null;
        _attachmentId = null;
        IsLive = false;
        IsLoading = false;
        CanGoBack = false;
        CanGoForward = false;
        if (client is null
            || clientId is not { } detachedClientId
            || sessionId is not { } detachedSessionId
            || attachmentId is not { } detachedAttachmentId)
        {
            return;
        }

        var detach = client.DetachAsync(
            new DetachSessionRequest(detachedAttachmentId, detachedSessionId),
            OperationContext.ForHuman(detachedClientId),
            CancellationToken.None);
        if (!detach.IsCompletedSuccessfully)
        {
            _ = ObserveDetachAsync(detach);
        }
    }

    private async Task InitializeSessionAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        ISessionHostClient? pendingClient = null;
        ClientId? pendingClientId = null;
        SessionId? pendingSessionId = null;
        AttachmentId? pendingAttachmentId = null;
        try
        {
            var client = SessionClient
                ?? throw new InvalidOperationException(
                    "The session-host client is unavailable.");
            var request = SessionRequest
                ?? throw new InvalidOperationException(
                    "The browser session request is unavailable.");
            var clientId = ClientId
                ?? throw new InvalidOperationException(
                    "The desktop client identity is unavailable.");
            var renderer = RendererView?.Renderer
                ?? throw new InvalidOperationException(
                    "The native browser renderer is unavailable.");
            var context = OperationContext.ForHuman(clientId);
            _ = RequireSuccess(await client.EnsureBrowserSessionAsync(
                request,
                context,
                cancellationToken));

            var attachment = await AttachInteractiveAsync(
                client,
                request.SessionId,
                clientId,
                renderer.Capabilities,
                cancellationToken);
            pendingClient = client;
            pendingClientId = clientId;
            pendingSessionId = request.SessionId;
            pendingAttachmentId = attachment.Attachment.Id;
            if (generation != _initializationGeneration)
            {
                return;
            }

            SubscribeRenderer(renderer);
            _ = RequireSuccess(await client.AttachBrowserRendererAsync(
                new AttachBrowserRendererRequest(
                    request.SessionId,
                    attachment.Attachment.Id,
                    renderer),
                context,
                cancellationToken));
            var state = RequireSuccess(await client.ReadBrowserStateAsync(
                request.SessionId,
                context,
                cancellationToken));
            if (generation != _initializationGeneration)
            {
                return;
            }

            _attachedClient = client;
            _attachedClientId = clientId;
            _attachedSessionId = request.SessionId;
            _attachmentId = attachment.Attachment.Id;
            pendingAttachmentId = null;
            IsLive = true;
            ShowFallback = false;
            if (state.IsSuccess && state.Value is { } browserState)
            {
                ApplyBrowserState(browserState);
            }
            else
            {
                SetOperationMessage(
                    state.Error?.Message ?? "The browser state is unavailable.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (generation == _initializationGeneration)
            {
                SetWaitingState("Browser renderer stopped.", "STOPPED");
            }
        }
        catch (Exception exception)
        {
            if (generation == _initializationGeneration)
            {
                UnsubscribeRenderer();
                SetFailureState("The native browser could not be initialized.");
            }

            Trace.TraceError("Unable to attach the browser session: {0}", exception);
        }
        finally
        {
            if (pendingClient is not null
                && pendingClientId is { } staleClientId
                && pendingSessionId is { } staleSessionId
                && pendingAttachmentId is { } staleAttachmentId)
            {
                await DetachStaleAttachmentAsync(
                    pendingClient,
                    staleClientId,
                    staleSessionId,
                    staleAttachmentId);
            }
        }
    }

    private async Task<AttachmentResult> AttachInteractiveAsync(
        ISessionHostClient client,
        SessionId sessionId,
        ClientId clientId,
        CapabilitySet rendererCapabilities,
        CancellationToken cancellationToken)
    {
        var capabilities = new CapabilitySet(
        [
            SessionCapabilities.AttachInteractive,
            .. rendererCapabilities.Values,
        ]);
        HostResult<AttachmentResult>? lastResult = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            lastResult = await client.AttachAsync(
                new AttachSessionRequest(
                    sessionId,
                    clientId,
                    AttachmentKind.Interactive,
                    CurrentViewport(),
                    capabilities),
                OperationContext.ForHuman(clientId),
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

            await Task.Delay(
                TimeSpan.FromMilliseconds(20 * (attempt + 1)),
                cancellationToken);
        }

        return RequireSuccess(lastResult
            ?? throw new InvalidOperationException(
                "The browser attachment did not start."));
    }

    private void ApplyOperationResult(
        HostResult<BrowserResult<BrowserSessionState>> result)
    {
        switch (result)
        {
            case HostResult<BrowserResult<BrowserSessionState>>.Success
            {
                Value.IsSuccess: true,
                Value.Value: { } state,
            }:
                ApplyBrowserState(state);
                break;
            case HostResult<BrowserResult<BrowserSessionState>>.Success success:
                SetOperationMessage(
                    success.Value.Error?.Message
                    ?? "The browser operation could not be completed.");
                break;
            case HostResult<BrowserResult<BrowserSessionState>>.Failure failure:
                SetOperationMessage(failure.Error.Message);
                break;
        }
    }

    private void SubscribeRenderer(IBrowserRenderer renderer)
    {
        if (ReferenceEquals(_subscribedRenderer, renderer))
        {
            return;
        }

        UnsubscribeRenderer();
        _subscribedRenderer = renderer;
        _subscribedRenderer.StateChanged += OnRendererStateChanged;
        ApplyBrowserState(renderer.State);
    }

    private void UnsubscribeRenderer()
    {
        if (_subscribedRenderer is null)
        {
            return;
        }

        _subscribedRenderer.StateChanged -= OnRendererStateChanged;
        _subscribedRenderer = null;
    }

    private void OnRendererStateChanged(
        object? sender,
        BrowserStateChangedEventArgs eventArgs)
    {
        _ = sender;
        ApplyBrowserState(eventArgs.State);
    }

    private void ApplyBrowserState(BrowserSessionState state)
    {
        AddressText = state.Address == BrowserAddress.Blank
            ? string.Empty
            : state.Address.ToString();
        IsLoading = state.LoadState == BrowserLoadState.Loading;
        CanGoBack = state.CanGoBack;
        CanGoForward = state.CanGoForward;
        StatusText = state.LoadState switch
        {
            BrowserLoadState.Ready => "READY",
            BrowserLoadState.Loading => "LOADING",
            BrowserLoadState.Failed => "FAILED",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        StatusBrush = state.LoadState switch
        {
            BrowserLoadState.Ready => ReadyBrush,
            BrowserLoadState.Loading => LoadingBrush,
            BrowserLoadState.Failed => FailedBrush,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        StatusMessage = state.Failure?.Message ?? string.Empty;
        ShowFallback = false;
        UpdateAutomationStatus();
        BrowserStateChanged?.Invoke(this, new BrowserStateChangedEventArgs(state));
    }

    private void SetWaitingState(string message, string status = "WAITING")
    {
        StatusText = status;
        StatusMessage = message;
        StatusBrush = WaitingBrush;
        ShowFallback = true;
        UpdateAutomationStatus();
    }

    private void SetFailureState(string message)
    {
        IsLive = false;
        IsLoading = false;
        CanGoBack = false;
        CanGoForward = false;
        StatusText = "ERROR";
        StatusMessage = message;
        StatusBrush = FailedBrush;
        ShowFallback = true;
        UpdateAutomationStatus();
    }

    private void SetOperationMessage(string message)
    {
        StatusMessage = message;
        UpdateAutomationStatus();
    }

    private void UpdateAutomationStatus() =>
        AutomationProperties.SetItemStatus(
            this,
            string.IsNullOrWhiteSpace(StatusMessage)
                ? StatusText
                : $"{StatusText}: {StatusMessage}");

    private ViewportDescriptor CurrentViewport() =>
        new(
            Bounds.Width,
            Bounds.Height,
            TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);

    private OperationContext NewContext() => OperationContext.ForHuman(
        _attachedClientId
        ?? ClientId
        ?? throw new InvalidOperationException(
            "The desktop client identity is unavailable."));

    private ISessionHostClient RequireAttachedClient() =>
        _attachedClient
        ?? throw new InvalidOperationException("The browser session is not attached.");

    private SessionId RequireAttachedSessionId() =>
        _attachedSessionId
        ?? throw new InvalidOperationException("The browser session is not attached.");

    private static bool TryResolveAddress(
        string? text,
        out BrowserAddress address)
    {
        if (BrowserAddress.TryParse(text, out var exact))
        {
            address = exact;
            return true;
        }

        var candidate = text?.Trim();
        if (!string.IsNullOrWhiteSpace(candidate)
            && candidate.Length <= BrowserAddress.MaximumLength - "https://".Length
            && !candidate.Contains("://", StringComparison.Ordinal)
            && BrowserAddress.TryParse($"https://{candidate}", out var https))
        {
            address = https;
            return true;
        }

        address = BrowserAddress.Blank;
        return false;
    }

    private static T RequireSuccess<T>(HostResult<T> result) => result switch
    {
        HostResult<T>.Success success => success.Value,
        HostResult<T>.Failure failure => throw new InvalidOperationException(
            $"{failure.Error.StableCode}: {failure.Error.Message}"),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static async Task ObserveDetachAsync(
        ValueTask<HostResult<Unit>> result)
    {
        try
        {
            _ = await result;
        }
        catch (Exception exception)
        {
            Trace.TraceError("Unable to detach the browser renderer: {0}", exception);
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
            Trace.TraceError(
                "Unable to detach a superseded browser renderer: {0}",
                exception);
        }
    }
}
