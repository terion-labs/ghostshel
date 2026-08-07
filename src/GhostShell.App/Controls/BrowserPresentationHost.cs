using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    private NativeSurfaceLayer? _layer;
    private bool _isSurfaceSuspended;
    private long _initializationGeneration;
    private bool _isAttachedToVisualTree;
    private string _addressText = string.Empty;
    private string _statusText = "Waiting";
    private string _statusMessage = string.Empty;
    private IBrush _statusBrush = WaitingBrush;
    private bool _isLive;
    private bool _isLoading;
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _showFallback = true;

    /// <summary>
    /// Whether the page has stepped aside for something the shell needs seen —
    /// the dock's placement targets during a drag. The panel shows what it is
    /// while it has nothing to show, because a blank rectangle mid-drag reads as
    /// a panel that has broken rather than one that is being moved.
    /// </summary>
    public static readonly DirectProperty<BrowserPresentationHost, bool>
        IsSurfaceSuspendedProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, bool>(
                nameof(IsSurfaceSuspended),
                host => host.IsSurfaceSuspended);

    public bool IsSurfaceSuspended
    {
        get => _isSurfaceSuspended;
        private set => SetAndRaise(
            IsSurfaceSuspendedProperty,
            ref _isSurfaceSuspended,
            value);
    }

    private void OnSurfaceSuspensionChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        IsSurfaceSuspended = NativeSurfaceLayer.IsSuspended;
    }

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

    /// <summary>
    /// Whether the panel is showing its own message instead of a page. The
    /// native surface is hidden while it is: a native view draws over every
    /// Avalonia pixel, so leaving it up would put an empty webview on top of the
    /// explanation of why there is nothing to show.
    /// </summary>
    public bool ShowFallback
    {
        get => _showFallback;
        private set
        {
            if (SetAndRaise(ShowFallbackProperty, ref _showFallback, value))
            {
                PresentSurface();
            }
        }
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
        LayoutUpdated += OnViewportLayoutUpdated;
        NativeSurfaceLayer.SuspensionChanged += OnSurfaceSuspensionChanged;
        IsSurfaceSuspended = NativeSurfaceLayer.IsSuspended;
        RestartSession();
    }

    /// <summary>
    /// The panel is off screen — another tab is in front, or this view is being
    /// rebuilt because panels moved. Neither is a reason to stop anything: the
    /// surface is hidden and the attachment is left alone, because it belongs to
    /// the panel and the panel has not gone anywhere.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttachedToVisualTree = false;
        LayoutUpdated -= OnViewportLayoutUpdated;
        NativeSurfaceLayer.SuspensionChanged -= OnSurfaceSuspensionChanged;
        ConcealSurface();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnViewportLayoutUpdated(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        PresentSurface();
    }

    /// <summary>
    /// Puts the native view over this viewport. This control draws nothing
    /// itself; it is the rectangle a surface is shown in, and moving it is the
    /// whole of what a layout change does to a browser.
    /// </summary>
    private void PresentSurface()
    {
        if (RendererView is not { } rendererView || !_isAttachedToVisualTree)
        {
            return;
        }

        var layer = _layer ??= NativeSurfaceLayer.For(this);
        if (layer is null)
        {
            return;
        }

        rendererView.Layer = layer;
        if (!IsEffectivelyVisible
            || ShowFallback
            || this.TranslatePoint(default, layer) is not { } origin)
        {
            layer.Conceal(rendererView.View);
            return;
        }

        layer.Present(
            rendererView.View,
            new Rect(origin, Bounds.Size));
    }

    private void ConcealSurface()
    {
        if (RendererView is { } rendererView && _layer is { } layer)
        {
            layer.Conceal(rendererView.View);
        }
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
        // No release here, on any of them. A host that stops pointing at a
        // renderer has stopped drawing a panel, which is not the same as the
        // panel being over — and this one line was the last place that still
        // confused the two.
        //
        // It only ever fired when a panel's views were being exchanged, which is
        // exactly when it must not: the departing view's binding unsets after the
        // arriving one has taken the surface, so the surface it gave up was the
        // one already on screen. Taking a native view out of the tree destroys
        // it, so the page went with it — a blank document under a live session,
        // which is what floating and docking back looked like. Releasing is the
        // panel's end and nothing else: see BrowserRendererView.Dispose.
        if (change.Property == SessionClientProperty
            || change.Property == SessionRequestProperty
            || change.Property == ClientIdProperty
            || change.Property == RendererViewProperty)
        {
            if (_isAttachedToVisualTree)
            {
                RestartSession();
            }
        }
    }

    private void RestartSession()
    {
        StopSession();
        if (SessionClient is null
            || SessionRequest is null
            || ClientId is null
            || RendererView is not { } rendererView)
        {
            SetWaitingState("Waiting for the native browser adapter.");
            return;
        }

        PresentSurface();

        // The panel may already be attached — this view is simply the newest one
        // to draw it. Adopting that attachment is what makes a layout change cost
        // nothing: no detach, no re-attach, no navigation, and the document that
        // was on screen a moment ago is still the one on screen now.
        if (rendererView.Attachment is { } existing
            && existing.Matches(SessionClient, ClientId, SessionRequest.SessionId))
        {
            AdoptAttachment(rendererView, existing);
            return;
        }

        SetWaitingState("Starting the native browser…", "Starting");
        var generation = ++_initializationGeneration;
        _attachmentLifetime = new CancellationTokenSource();
        _ = InitializeSessionAsync(generation, _attachmentLifetime.Token);
    }

    private void AdoptAttachment(
        BrowserRendererView rendererView,
        BrowserRendererAttachment attachment)
    {
        _initializationGeneration++;
        _attachedClient = attachment.Client;
        _attachedClientId = attachment.ClientId;
        _attachedSessionId = attachment.SessionId;
        _attachmentId = attachment.AttachmentId;
        IsLive = true;
        ShowFallback = false;
        SubscribeRenderer(rendererView.Renderer);
    }

    /// <summary>
    /// Lets go of what this view was watching. It does not detach: the
    /// attachment belongs to the panel, and is ended by the panel — see
    /// <see cref="BrowserRendererView.Dispose"/>.
    /// </summary>
    private void StopSession()
    {
        _initializationGeneration++;
        _attachmentLifetime?.Cancel();
        _attachmentLifetime?.Dispose();
        _attachmentLifetime = null;
        UnsubscribeRenderer();
        _attachedClient = null;
        _attachedClientId = null;
        _attachedSessionId = null;
        _attachmentId = null;
        IsLive = false;
        IsLoading = false;
        CanGoBack = false;
        CanGoForward = false;
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
            if (RendererView is { } owner)
            {
                owner.Attachment = new BrowserRendererAttachment(
                    client,
                    clientId,
                    request.SessionId,
                    attachment.Attachment.Id);
            }

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
                SetWaitingState("Browser renderer stopped.", "Stopped");
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
            BrowserLoadState.Ready => "Ready",
            BrowserLoadState.Loading => "Loading",
            BrowserLoadState.Failed => "Failed",
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

    private void SetWaitingState(string message, string status = "Waiting")
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
        StatusText = "Error";
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
