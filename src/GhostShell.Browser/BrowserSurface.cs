using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using GhostShell.Application;
using System.Security.Cryptography;
using System.Text;

namespace GhostShell.Browser;

/// <summary>
/// A GhostSHELL-owned browser surface backed by the operating system web
/// engine. It exposes only bounded, engine-neutral navigation operations.
/// </summary>
public sealed class BrowserSurface :
    ContentControl,
    IBrowserRenderer,
    IBrowserElementReferenceRegistry,
    IDisposable
{
    private static readonly TimeSpan ElementReferenceLifetime =
        TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultNativeSnapshotDeadline =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumNativeSnapshotDeadline =
        TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private IEmbeddedBrowserView _nativeView;
    private readonly IBrowserUiDispatcher _dispatcher;
    private readonly Func<IEmbeddedBrowserView>? _nativeViewReplacementFactory;
    private readonly Action<Control>? _nativeViewReplacementPresenter;
    private readonly TimeProvider _timeProvider;
    private readonly IDisposable? _networkLifetime;
    private readonly TimeSpan _nativeSnapshotDeadline;
    private readonly object _snapshotReferenceGate = new();
    private Dictionary<string, SnapshotReferenceLease> _snapshotReferences = [];
    private long _snapshotReferenceEpoch;
    private PendingOriginConstrainedNavigation? _pendingGovernedNavigation;
    private PendingDocumentSnapshot? _pendingDocumentSnapshot;
    private PendingElementClick? _pendingElementClick;
    private PendingElementFill? _pendingElementFill;
    private PendingElementCheck? _pendingElementCheck;
    private DrainingNativeNavigation? _drainingNativeNavigation;
    private long _lastTerminalNavigationGeneration;
    private volatile bool _interactionRecoveryFailed;
    private bool _disposed;

    public BrowserSurface()
        : this(BrowserCapabilityProfile.Production)
    {
    }

    public BrowserSurface(BrowserCapabilityProfile capabilityProfile)
        : this(
            new CefBrowserView(),
            AvaloniaBrowserUiDispatcher.Instance,
            static () => new CefBrowserView(),
            timeProvider: TimeProvider.System,
            capabilityProfile: capabilityProfile)
    {
    }

    /// <summary>
    /// Creates an isolated browser whose TCP traffic leaves through a local
    /// SOCKS5 endpoint. The request context is shared with replacement native
    /// views so renderer recovery cannot silently fall back to the local route.
    /// </summary>
    public BrowserSurface(
        BrowserCapabilityProfile capabilityProfile,
        int socksProxyPort)
        : this(
            CefBrowserNetworkContext.Create(socksProxyPort),
            capabilityProfile)
    {
    }

    private BrowserSurface(
        CefBrowserNetworkContext network,
        BrowserCapabilityProfile capabilityProfile)
        : this(
            network.CreateView(),
            AvaloniaBrowserUiDispatcher.Instance,
            network.CreateView,
            timeProvider: TimeProvider.System,
            capabilityProfile: capabilityProfile,
            networkLifetime: network)
    {
    }

    internal BrowserSurface(
        IEmbeddedBrowserView nativeView,
        IBrowserUiDispatcher? dispatcher = null,
        Func<IEmbeddedBrowserView>? nativeViewReplacementFactory = null,
        Action<Control>? nativeViewReplacementPresenter = null,
        TimeProvider? timeProvider = null,
        TimeSpan? nativeSnapshotDeadline = null,
        BrowserCapabilityProfile? capabilityProfile = null,
        IDisposable? networkLifetime = null)
    {
        _nativeView = nativeView ?? throw new ArgumentNullException(nameof(nativeView));
        _dispatcher = dispatcher ?? AvaloniaBrowserUiDispatcher.Instance;
        _nativeViewReplacementFactory = nativeViewReplacementFactory;
        _nativeViewReplacementPresenter =
            nativeViewReplacementPresenter;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _networkLifetime = networkLifetime;
        CapabilityProfile = capabilityProfile
            ?? BrowserCapabilityProfile.Production;
        _nativeSnapshotDeadline =
            nativeSnapshotDeadline ?? DefaultNativeSnapshotDeadline;
        if (_nativeSnapshotDeadline <= TimeSpan.Zero
            || _nativeSnapshotDeadline > MaximumNativeSnapshotDeadline)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nativeSnapshotDeadline),
                "The native snapshot deadline must be greater than zero and no more than 30 seconds.");
        }

        _nativeView.NavigationStarted += OnNavigationStarted;
        _nativeView.NavigationCompleted += OnNavigationCompleted;
        _nativeView.AddressChanged += OnAddressChanged;
        _nativeView.NavigationRejected += OnNavigationRejected;
        _nativeView.RenderProcessFailed += OnRenderProcessFailed;

        Focusable = true;
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        AutomationProperties.SetName(this, "Web browser");
        AutomationProperties.SetHelpText(
            this,
            "Embedded Chromium content for the active browser panel.");

        State = BrowserSessionState.Initial(BrowserAddress.Blank);
        Content = _nativeView.View;
    }

    public BrowserSessionState State { get; private set; }

    public BrowserCapabilityProfile CapabilityProfile { get; }

    public CapabilitySet Capabilities => CapabilityProfile.Capabilities;

    public event EventHandler<BrowserStateChangedEventArgs>? StateChanged;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pendingGovernedNavigation is { } pending
            && RetireGovernedNavigation(pending))
        {
            CompleteRetiredGovernedNavigation(
                pending,
                RendererUnavailable(
                    "The embedded browser surface was disposed before navigation completed."));
        }

        _drainingNativeNavigation = null;
        var nativeView = _nativeView;
        nativeView.NavigationStarted -= OnNavigationStarted;
        nativeView.NavigationCompleted -= OnNavigationCompleted;
        nativeView.AddressChanged -= OnAddressChanged;
        nativeView.NavigationRejected -= OnNavigationRejected;
        nativeView.RenderProcessFailed -= OnRenderProcessFailed;
        Content = null;
        try
        {
            nativeView.Dispose();
        }
        finally
        {
            _networkLifetime?.Dispose();
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (ReferenceEquals(e.Source, this)
            && !HasTimedOutDocumentSnapshot
            && !_interactionRecoveryFailed)
        {
            _nativeView.View.Focus();
        }
    }

    public ValueTask<BrowserResult<BrowserSessionState>> NavigateAsync(
        BrowserAddress address,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        return RunOnUiThreadAsync(
            () =>
            {
                if (_interactionRecoveryFailed)
                {
                    return InteractionRecoveryUnavailable();
                }

                if (HasTimedOutDocumentSnapshot)
                {
                    return SnapshotRecoveryInProgress();
                }

                if (HasGovernedNavigationActivity
                    || HasPendingElementInteraction)
                {
                    return NavigationInProgress();
                }

                _nativeView.Navigate(address);
                PublishLoading(address);
                return BrowserResult<BrowserSessionState>.Success(State);
            },
            cancellationToken);
    }

    public ValueTask<BrowserResult<BrowserSessionState>> GoBackAsync(
        CancellationToken cancellationToken) =>
        RunOnUiThreadAsync(
            () => _interactionRecoveryFailed
                ? InteractionRecoveryUnavailable()
                : HasTimedOutDocumentSnapshot
                    ? SnapshotRecoveryInProgress()
                    : HasGovernedNavigationActivity
                        || HasPendingElementInteraction
                        ? NavigationInProgress()
                        : NavigateHistory(
                            _nativeView.GoBack,
                            "No previous browser history entry is available."),
            cancellationToken);

    public ValueTask<BrowserResult<BrowserSessionState>> GoForwardAsync(
        CancellationToken cancellationToken) =>
        RunOnUiThreadAsync(
            () => _interactionRecoveryFailed
                ? InteractionRecoveryUnavailable()
                : HasTimedOutDocumentSnapshot
                    ? SnapshotRecoveryInProgress()
                    : HasGovernedNavigationActivity
                        || HasPendingElementInteraction
                        ? NavigationInProgress()
                        : NavigateHistory(
                            _nativeView.GoForward,
                            "No next browser history entry is available."),
            cancellationToken);

    public ValueTask<BrowserResult<BrowserSessionState>> ReloadAsync(
        CancellationToken cancellationToken) =>
        RunOnUiThreadAsync(
            () =>
            {
                if (_interactionRecoveryFailed)
                {
                    return InteractionRecoveryUnavailable();
                }

                if (HasTimedOutDocumentSnapshot)
                {
                    return SnapshotRecoveryInProgress();
                }

                if (HasGovernedNavigationActivity
                    || HasPendingElementInteraction)
                {
                    return NavigationInProgress();
                }

                if (!_nativeView.Reload())
                {
                    return RendererUnavailable(
                        "The browser cannot reload before its native renderer is ready.");
                }

                PublishLoading(State.Address);
                return BrowserResult<BrowserSessionState>.Success(State);
            },
            cancellationToken);

    public ValueTask<BrowserResult<BrowserSessionState>> StopAsync(
        CancellationToken cancellationToken) =>
        RunOnUiThreadAsync(
            () =>
            {
                if (_interactionRecoveryFailed)
                {
                    return InteractionRecoveryUnavailable();
                }

                if (HasTimedOutDocumentSnapshot)
                {
                    return SnapshotRecoveryInProgress();
                }

                if (_pendingGovernedNavigation is { } pending)
                {
                    return CompleteCancelledGovernedNavigation(
                        pending,
                        stopNativeView: true)
                        ? BrowserResult<BrowserSessionState>.Success(State)
                        : RendererUnavailable(
                            "The browser cannot stop before its native renderer is ready.");
                }

                if (PendingInteraction is { } interaction
                    && interaction.HasObservedNavigationStart)
                {
                    return _nativeView.Stop()
                        ? BrowserResult<BrowserSessionState>.Success(State)
                        : RendererUnavailable(
                            "The browser cannot stop before its native renderer is ready.");
                }

                if (HasPendingElementInteraction)
                {
                    return NavigationInProgress();
                }

                if (!_nativeView.Stop())
                {
                    return RendererUnavailable(
                        "The browser cannot stop before its native renderer is ready.");
                }

                if (_drainingNativeNavigation is not null)
                {
                    return BrowserResult<BrowserSessionState>.Success(State);
                }

                Publish(new BrowserSessionState(
                    State.Address,
                    State.Title,
                    BrowserLoadState.Ready,
                    _nativeView.CanGoBack,
                    _nativeView.CanGoForward,
                    State.DocumentRevision));
                return BrowserResult<BrowserSessionState>.Success(State);
            },
            cancellationToken);

    public async ValueTask<BrowserResult<BrowserSessionState>>
        NavigateWithinOriginAsync(
            BrowserOriginConstrainedNavigationRequest request,
            BrowserNavigationOrigin allowedOrigin,
            BrowserNavigationStartBinding startBinding,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowedOrigin);
        ArgumentNullException.ThrowIfNull(startBinding);
        if (!CapabilityProfile.Supports(
                SessionCapabilities.BrowserOriginGuard))
        {
            return UnsupportedCapability<BrowserSessionState>(
                SessionCapabilities.BrowserOriginGuard);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        try
        {
            Task<BrowserResult<BrowserSessionState>> completion;
            if (_dispatcher.CheckAccess())
            {
                completion = BeginOriginConstrainedNavigation(
                    request,
                    allowedOrigin,
                    startBinding,
                    cancellationToken);
            }
            else
            {
                completion = await _dispatcher.InvokeAsync(
                    () => BeginOriginConstrainedNavigation(
                        request,
                        allowedOrigin,
                        startBinding,
                        cancellationToken));
            }

            return await completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (Exception)
        {
            return BrowserResult<BrowserSessionState>.Failure(EngineFailure());
        }
    }

    public async ValueTask<BrowserResult<BrowserDocumentSnapshot>>
        CaptureSnapshotAsync(
            BrowserDocumentBinding document,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!CapabilityProfile.Supports(SessionCapabilities.BrowserSnapshot))
        {
            return UnsupportedCapability<BrowserDocumentSnapshot>(
                SessionCapabilities.BrowserSnapshot);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return SnapshotCancelled();
        }

        try
        {
            Task<BrowserResult<BrowserDocumentSnapshot>> completion;
            if (_dispatcher.CheckAccess())
            {
                completion = BeginDocumentSnapshot(
                    document,
                    cancellationToken);
            }
            else
            {
                completion = await _dispatcher.InvokeAsync(
                    () => BeginDocumentSnapshot(
                        document,
                        cancellationToken));
            }

            return await completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return SnapshotCancelled();
        }
        catch (Exception)
        {
            return BrowserResult<BrowserDocumentSnapshot>.Failure(
                SnapshotRendererUnavailable());
        }
    }

    public async ValueTask<BrowserResult<BrowserClickReceipt>>
        ClickWithinOriginAsync(
            BrowserElementReference reference,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(allowedOrigin);
        if (!CapabilityProfile.Supports(SessionCapabilities.BrowserClick))
        {
            return UnsupportedCapability<BrowserClickReceipt>(
                SessionCapabilities.BrowserClick);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ClickCancelled();
        }

        try
        {
            Task<BrowserResult<BrowserClickReceipt>> completion;
            if (_dispatcher.CheckAccess())
            {
                completion = BeginElementClick(
                    reference,
                    allowedOrigin,
                    cancellationToken);
            }
            else
            {
                completion = await _dispatcher.InvokeAsync(
                    () => BeginElementClick(
                        reference,
                        allowedOrigin,
                        cancellationToken));
            }

            return await completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ClickCancelled();
        }
        catch (Exception)
        {
            return BrowserResult<BrowserClickReceipt>.Failure(
                InteractionOutcomeUnknown());
        }
    }

    public async ValueTask<BrowserResult<BrowserFillReceipt>>
        FillWithinOriginAsync(
            BrowserElementReference reference,
            string text,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(allowedOrigin);
        if (!CapabilityProfile.Supports(SessionCapabilities.BrowserFill))
        {
            return UnsupportedCapability<BrowserFillReceipt>(
                SessionCapabilities.BrowserFill);
        }

        ValidateFillText(text);
        if (cancellationToken.IsCancellationRequested)
        {
            return FillCancelled();
        }

        try
        {
            Task<BrowserResult<BrowserFillReceipt>> completion;
            if (_dispatcher.CheckAccess())
            {
                completion = BeginElementFill(
                    reference,
                    text,
                    allowedOrigin,
                    cancellationToken);
            }
            else
            {
                completion = await _dispatcher.InvokeAsync(
                    () => BeginElementFill(
                        reference,
                        text,
                        allowedOrigin,
                        cancellationToken));
            }

            return await completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return FillCancelled();
        }
        catch (Exception)
        {
            return BrowserResult<BrowserFillReceipt>.Failure(
                InteractionOutcomeUnknown());
        }
    }

    public async ValueTask<BrowserResult<BrowserCheckReceipt>>
        CheckWithinOriginAsync(
            BrowserElementReference reference,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(allowedOrigin);
        if (!CapabilityProfile.Supports(SessionCapabilities.BrowserCheck))
        {
            return UnsupportedCapability<BrowserCheckReceipt>(
                SessionCapabilities.BrowserCheck);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return CheckCancelled();
        }

        try
        {
            Task<BrowserResult<BrowserCheckReceipt>> completion;
            if (_dispatcher.CheckAccess())
            {
                completion = BeginElementCheck(
                    reference,
                    allowedOrigin,
                    cancellationToken);
            }
            else
            {
                completion = await _dispatcher.InvokeAsync(
                    () => BeginElementCheck(
                        reference,
                        allowedOrigin,
                        cancellationToken));
            }

            return await completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CheckCancelled();
        }
        catch (Exception)
        {
            return BrowserResult<BrowserCheckReceipt>.Failure(
                InteractionOutcomeUnknown());
        }
    }

    void IBrowserElementReferenceRegistry.InvalidateElementReferences() =>
        InvalidateElementReferences();

    internal bool TryResolveElementReference(
        BrowserElementReference reference,
        out NativeBrowserElementHandle? handle)
    {
        ArgumentNullException.ThrowIfNull(reference);
        lock (_snapshotReferenceGate)
        {
            if (!_snapshotReferences.TryGetValue(
                    reference.Value,
                    out var lease)
                || lease.Document != reference.Document
                || !ReferenceEquals(lease.NativeView, _nativeView)
                || lease.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                _snapshotReferences.Remove(reference.Value);
                handle = null;
                return false;
            }

            handle = lease.Handle;
            return true;
        }
    }

    private Task<BrowserResult<BrowserClickReceipt>>
        BeginElementClick(
            BrowserElementReference reference,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(ClickCancelled());
        }

        if (_interactionRecoveryFailed)
        {
            return Task.FromResult(
                BrowserResult<BrowserClickReceipt>.Failure(
                    InteractionRendererUnavailable()));
        }

        if (HasTimedOutDocumentSnapshot)
        {
            return Task.FromResult(
                BrowserResult<BrowserClickReceipt>.Failure(
                    InteractionRendererUnavailable()));
        }

        if (HasPendingElementInteraction
            || _pendingDocumentSnapshot is not null
            || HasGovernedNavigationActivity
            || State.LoadState == BrowserLoadState.Loading)
        {
            return Task.FromResult(
                BrowserResult<BrowserClickReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationInProgress,
                        "The browser cannot activate an element while another browser mutation is in progress.",
                        retryable: true)));
        }

        if (State.LoadState != BrowserLoadState.Ready)
        {
            return Task.FromResult(
                BrowserResult<BrowserClickReceipt>.Failure(
                    InteractionRendererUnavailable()));
        }

        if (!reference.Document.Matches(State))
        {
            return Task.FromResult(
                BrowserResult<BrowserClickReceipt>.Failure(
                    InteractionStateChanged()));
        }

        if (!allowedOrigin.Allows(State.Address))
        {
            return Task.FromResult(
                BrowserResult<BrowserClickReceipt>.Failure(
                    PolicyError()));
        }

        SnapshotReferenceLease? lease;
        lock (_snapshotReferenceGate)
        {
            if (!_snapshotReferences.TryGetValue(
                    reference.Value,
                    out lease)
                || lease.Document != reference.Document
                || !ReferenceEquals(lease.NativeView, _nativeView)
                || lease.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                _snapshotReferences.Remove(reference.Value);
                lease = null;
            }
        }

        if (lease is null)
        {
            return Task.FromResult(
                BrowserResult<BrowserClickReceipt>.Failure(
                    ElementReferenceStale()));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(ClickCancelled());
        }

        var pending = new PendingElementClick(
            _nativeView,
            reference.Document,
            allowedOrigin);
        _pendingElementClick = pending;
        InvalidateElementReferences();
        if (cancellationToken.IsCancellationRequested)
        {
            _pendingElementClick = null;
            return Task.FromResult(ClickCancelled());
        }

        try
        {
            pending.NativeDispatchCommitted = true;
            pending.NativeCompletion =
                _nativeView.ClickAsync(lease.Handle);
        }
        catch (Exception)
        {
            CompleteAmbiguousElementClick(pending);
            return pending.Completion.Task;
        }

        // Arm the deadline before observing a task that may already be
        // complete. The completion path cancels and disposes the deadline
        // source, so reversing this order can fault the deadline observer
        // before it has read the token.
        _ = ObserveElementClickDeadlineAsync(pending);
        _ = ObserveNativeElementClickAsync(pending);
        return pending.Completion.Task;
    }

    private async Task ObserveNativeElementClickAsync(
        PendingElementClick pending)
    {
        NativeBrowserClickResult result;
        try
        {
            result = await pending.NativeCompletion!
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = NativeBrowserClickResult.OutcomeUnknown();
        }

        try
        {
            if (_dispatcher.CheckAccess())
            {
                RecordNativeElementClickResult(pending, result);
            }
            else
            {
                await _dispatcher.InvokeAsync(
                    () =>
                    {
                        RecordNativeElementClickResult(
                            pending,
                            result);
                        return true;
                    });
            }
        }
        catch (Exception)
        {
            pending.Completion.TrySetResult(
                BrowserResult<BrowserClickReceipt>.Failure(
                    InteractionOutcomeUnknown()));
        }
    }

    private async Task ObserveElementClickDeadlineAsync(
        PendingElementClick pending)
    {
        try
        {
            await Task.Delay(
                    _nativeSnapshotDeadline,
                    pending.DeadlineCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_dispatcher.CheckAccess())
            {
                TimeoutElementClick(pending);
            }
            else
            {
                await _dispatcher.InvokeAsync(
                    () =>
                    {
                        TimeoutElementClick(pending);
                        return true;
                    });
            }
        }
        catch (Exception)
        {
            pending.Completion.TrySetResult(
                BrowserResult<BrowserClickReceipt>.Failure(
                    InteractionOutcomeUnknown()));
        }
    }

    private void RecordNativeElementClickResult(
        PendingElementClick pending,
        NativeBrowserClickResult result)
    {
        if (!ReferenceEquals(_pendingElementClick, pending)
            || !ReferenceEquals(_nativeView, pending.NativeView))
        {
            return;
        }

        pending.NativeResult = result
            ?? NativeBrowserClickResult.OutcomeUnknown();
        TryCompleteElementClick(pending);
    }

    private void TryCompleteElementClick(PendingElementClick pending)
    {
        if (!ReferenceEquals(_pendingElementClick, pending)
            || pending.NativeResult is not { } nativeResult)
        {
            return;
        }

        switch (nativeResult.Status)
        {
            case NativeBrowserClickStatus.OutcomeUnknown:
                CompleteAmbiguousElementClick(pending);
                return;
            case NativeBrowserClickStatus.Stale:
                if (pending.HasObservedNavigationStart)
                {
                    CompleteAmbiguousElementClick(pending);
                    return;
                }

                CompleteElementClick(
                    pending,
                    BrowserResult<BrowserClickReceipt>.Failure(
                        ElementReferenceStale()));
                return;
            case NativeBrowserClickStatus.NotInteractable:
                if (pending.HasObservedNavigationStart)
                {
                    CompleteAmbiguousElementClick(pending);
                    return;
                }

                CompleteElementClick(
                    pending,
                    BrowserResult<BrowserClickReceipt>.Failure(
                        ElementNotInteractable()));
                return;
            case NativeBrowserClickStatus.Activated:
                if (pending.HasObservedNavigationStart
                    && !pending.NavigationTerminal)
                {
                    return;
                }

                if (pending.NavigationError is { } navigationError)
                {
                    CompleteElementClickFailure(
                        pending,
                        navigationError,
                        pending.RequiresQuarantine);
                    return;
                }

                CompleteElementClick(
                    pending,
                    BrowserResult<BrowserClickReceipt>.Success(
                        new BrowserClickReceipt(
                            pending.SourceDocument)));
                return;
            default:
                CompleteAmbiguousElementClick(pending);
                return;
        }
    }

    private void TimeoutElementClick(PendingElementClick pending)
    {
        if (!ReferenceEquals(_pendingElementClick, pending))
        {
            return;
        }

        CompleteAmbiguousElementClick(pending);
    }

    private void CompleteAmbiguousElementClick(
        PendingElementClick pending)
    {
        if (!ReferenceEquals(_pendingElementClick, pending))
        {
            pending.Completion.TrySetResult(
                BrowserResult<BrowserClickReceipt>.Failure(
                    InteractionOutcomeUnknown()));
            return;
        }

        InvalidateElementReferences();
        var replaced = TryReplaceQuarantinedNativeView();
        pending.NativeViewWasReplaced = replaced;
        if (!replaced)
        {
            _interactionRecoveryFailed = true;
        }

        CompleteElementClick(
            pending,
            BrowserResult<BrowserClickReceipt>.Failure(
                InteractionOutcomeUnknown()));
    }

    private void CompleteElementClickFailure(
        PendingElementClick pending,
        BrowserError error,
        bool quarantine)
    {
        if (quarantine)
        {
            var replaced = TryReplaceQuarantinedNativeView();
            pending.NativeViewWasReplaced = replaced;
            if (!replaced)
            {
                _interactionRecoveryFailed = true;
            }
        }

        CompleteElementClick(
            pending,
            BrowserResult<BrowserClickReceipt>.Failure(error));
    }

    private void CompleteElementClick(
        PendingElementClick pending,
        BrowserResult<BrowserClickReceipt> result)
    {
        if (!ReferenceEquals(_pendingElementClick, pending))
        {
            return;
        }

        _pendingElementClick = null;
        pending.DeadlineCancellation.Cancel();
        pending.DeadlineCancellation.Dispose();
        pending.Completion.TrySetResult(result);
    }

    private Task<BrowserResult<BrowserFillReceipt>>
        BeginElementFill(
            BrowserElementReference reference,
            string text,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(FillCancelled());
        }

        if (_interactionRecoveryFailed)
        {
            return Task.FromResult(
                BrowserResult<BrowserFillReceipt>.Failure(
                    FillRendererUnavailable()));
        }

        if (HasTimedOutDocumentSnapshot)
        {
            return Task.FromResult(
                BrowserResult<BrowserFillReceipt>.Failure(
                    FillRendererUnavailable()));
        }

        if (HasPendingElementInteraction
            || _pendingDocumentSnapshot is not null
            || HasGovernedNavigationActivity
            || State.LoadState == BrowserLoadState.Loading)
        {
            return Task.FromResult(
                BrowserResult<BrowserFillReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationInProgress,
                        "The browser cannot fill an element while another browser mutation is in progress.",
                        retryable: true)));
        }

        if (State.LoadState != BrowserLoadState.Ready)
        {
            return Task.FromResult(
                BrowserResult<BrowserFillReceipt>.Failure(
                    FillRendererUnavailable()));
        }

        if (!reference.Document.Matches(State))
        {
            return Task.FromResult(
                BrowserResult<BrowserFillReceipt>.Failure(
                    FillStateChanged()));
        }

        if (!allowedOrigin.Allows(State.Address))
        {
            return Task.FromResult(
                BrowserResult<BrowserFillReceipt>.Failure(
                    PolicyError()));
        }

        SnapshotReferenceLease? lease;
        lock (_snapshotReferenceGate)
        {
            if (!_snapshotReferences.TryGetValue(
                    reference.Value,
                    out lease)
                || lease.Document != reference.Document
                || !ReferenceEquals(lease.NativeView, _nativeView)
                || lease.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                _snapshotReferences.Remove(reference.Value);
                lease = null;
            }
        }

        if (lease is null)
        {
            return Task.FromResult(
                BrowserResult<BrowserFillReceipt>.Failure(
                    ElementReferenceStale()));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(FillCancelled());
        }

        var pending = new PendingElementFill(
            _nativeView,
            reference.Document,
            allowedOrigin);
        _pendingElementFill = pending;

        // Both the public references and the page-realm entries are one-shot.
        // The native registry clears its full entry set before validation and
        // mutation; clearing this set first prevents another public operation
        // from racing the same snapshot lease.
        InvalidateElementReferences();
        if (cancellationToken.IsCancellationRequested)
        {
            _pendingElementFill = null;
            return Task.FromResult(FillCancelled());
        }

        try
        {
            pending.NativeDispatchCommitted = true;
            pending.NativeCompletion =
                _nativeView.FillAsync(lease.Handle, text);
        }
        catch (Exception)
        {
            CompleteAmbiguousElementFill(pending);
            return pending.Completion.Task;
        }

        // Arm the deadline first because the native task may already be
        // complete. Its observer owns cancellation and disposal of the timer.
        _ = ObserveElementFillDeadlineAsync(pending);
        _ = ObserveNativeElementFillAsync(pending);
        return pending.Completion.Task;
    }

    private async Task ObserveNativeElementFillAsync(
        PendingElementFill pending)
    {
        NativeBrowserFillResult result;
        try
        {
            result = await pending.NativeCompletion!
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = NativeBrowserFillResult.OutcomeUnknown();
        }

        try
        {
            if (_dispatcher.CheckAccess())
            {
                RecordNativeElementFillResult(pending, result);
            }
            else
            {
                await _dispatcher.InvokeAsync(
                    () =>
                    {
                        RecordNativeElementFillResult(
                            pending,
                            result);
                        return true;
                    });
            }
        }
        catch (Exception)
        {
            CompleteElementFillAfterDispatcherFailure(pending);
        }
    }

    private async Task ObserveElementFillDeadlineAsync(
        PendingElementFill pending)
    {
        try
        {
            await Task.Delay(
                    _nativeSnapshotDeadline,
                    pending.DeadlineCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_dispatcher.CheckAccess())
            {
                TimeoutElementFill(pending);
            }
            else
            {
                await _dispatcher.InvokeAsync(
                    () =>
                    {
                        TimeoutElementFill(pending);
                        return true;
                    });
            }
        }
        catch (Exception)
        {
            CompleteElementFillAfterDispatcherFailure(pending);
        }
    }

    private void CompleteElementFillAfterDispatcherFailure(
        PendingElementFill pending)
    {
        _interactionRecoveryFailed = true;
        try
        {
            pending.DeadlineCancellation.Cancel();
            pending.DeadlineCancellation.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Another terminal path already retired the deadline. The
            // interaction remains permanently fenced because the UI
            // dispatcher could not perform adapter quarantine.
        }

        pending.Completion.TrySetResult(
            BrowserResult<BrowserFillReceipt>.Failure(
                InteractionOutcomeUnknown()));
    }

    private void RecordNativeElementFillResult(
        PendingElementFill pending,
        NativeBrowserFillResult result)
    {
        if (!ReferenceEquals(_pendingElementFill, pending)
            || !ReferenceEquals(_nativeView, pending.NativeView))
        {
            return;
        }

        pending.NativeResult = result
            ?? NativeBrowserFillResult.OutcomeUnknown();
        TryCompleteElementFill(pending);
    }

    private void TryCompleteElementFill(PendingElementFill pending)
    {
        if (!ReferenceEquals(_pendingElementFill, pending)
            || pending.NativeResult is not { } nativeResult)
        {
            return;
        }

        switch (nativeResult.Status)
        {
            case NativeBrowserFillStatus.OutcomeUnknown:
                CompleteAmbiguousElementFill(pending);
                return;
            case NativeBrowserFillStatus.Stale:
                if (pending.HasObservedNavigationStart)
                {
                    CompleteAmbiguousElementFill(pending);
                    return;
                }

                CompleteElementFill(
                    pending,
                    BrowserResult<BrowserFillReceipt>.Failure(
                        ElementReferenceStale()));
                return;
            case NativeBrowserFillStatus.NotInteractable:
                if (pending.HasObservedNavigationStart)
                {
                    CompleteAmbiguousElementFill(pending);
                    return;
                }

                CompleteElementFill(
                    pending,
                    BrowserResult<BrowserFillReceipt>.Failure(
                        ElementNotInteractable()));
                return;
            case NativeBrowserFillStatus.NotFillable:
                if (pending.HasObservedNavigationStart)
                {
                    CompleteAmbiguousElementFill(pending);
                    return;
                }

                CompleteElementFill(
                    pending,
                    BrowserResult<BrowserFillReceipt>.Failure(
                        ElementNotFillable()));
                return;
            case NativeBrowserFillStatus.ValueNotSupported:
                if (pending.HasObservedNavigationStart)
                {
                    CompleteAmbiguousElementFill(pending);
                    return;
                }

                CompleteElementFill(
                    pending,
                    BrowserResult<BrowserFillReceipt>.Failure(
                        FillValueNotSupported()));
                return;
            case NativeBrowserFillStatus.Filled:
                if (pending.HasObservedNavigationStart
                    && !pending.NavigationTerminal)
                {
                    return;
                }

                if (pending.NavigationError is { } navigationError)
                {
                    CompleteElementFillFailure(
                        pending,
                        navigationError,
                        pending.RequiresQuarantine);
                    return;
                }

                CompleteElementFill(
                    pending,
                    BrowserResult<BrowserFillReceipt>.Success(
                        new BrowserFillReceipt(
                            pending.SourceDocument)));
                return;
            default:
                CompleteAmbiguousElementFill(pending);
                return;
        }
    }

    private void TimeoutElementFill(PendingElementFill pending)
    {
        if (!ReferenceEquals(_pendingElementFill, pending))
        {
            return;
        }

        CompleteAmbiguousElementFill(pending);
    }

    private void CompleteAmbiguousElementFill(
        PendingElementFill pending)
    {
        if (!ReferenceEquals(_pendingElementFill, pending))
        {
            pending.Completion.TrySetResult(
                BrowserResult<BrowserFillReceipt>.Failure(
                    InteractionOutcomeUnknown()));
            return;
        }

        InvalidateElementReferences();
        var replaced = TryReplaceQuarantinedNativeView();
        pending.NativeViewWasReplaced = replaced;
        if (!replaced)
        {
            _interactionRecoveryFailed = true;
        }

        CompleteElementFill(
            pending,
            BrowserResult<BrowserFillReceipt>.Failure(
                InteractionOutcomeUnknown()));
    }

    private void CompleteElementFillFailure(
        PendingElementFill pending,
        BrowserError error,
        bool quarantine)
    {
        if (quarantine)
        {
            var replaced = TryReplaceQuarantinedNativeView();
            pending.NativeViewWasReplaced = replaced;
            if (!replaced)
            {
                _interactionRecoveryFailed = true;
            }
        }

        CompleteElementFill(
            pending,
            BrowserResult<BrowserFillReceipt>.Failure(error));
    }

    private void CompleteElementFill(
        PendingElementFill pending,
        BrowserResult<BrowserFillReceipt> result)
    {
        if (!ReferenceEquals(_pendingElementFill, pending))
        {
            return;
        }

        _pendingElementFill = null;
        pending.DeadlineCancellation.Cancel();
        pending.DeadlineCancellation.Dispose();
        pending.Completion.TrySetResult(result);
    }

    private Task<BrowserResult<BrowserCheckReceipt>>
        BeginElementCheck(
            BrowserElementReference reference,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(CheckCancelled());
        }

        if (_interactionRecoveryFailed || HasTimedOutDocumentSnapshot)
        {
            return Task.FromResult(
                BrowserResult<BrowserCheckReceipt>.Failure(
                    CheckRendererUnavailable()));
        }

        if (HasPendingElementInteraction
            || _pendingDocumentSnapshot is not null
            || HasGovernedNavigationActivity
            || State.LoadState == BrowserLoadState.Loading)
        {
            return Task.FromResult(
                BrowserResult<BrowserCheckReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationInProgress,
                        "The browser cannot check an element while another browser mutation is in progress.",
                        retryable: true)));
        }

        if (State.LoadState != BrowserLoadState.Ready)
        {
            return Task.FromResult(
                BrowserResult<BrowserCheckReceipt>.Failure(
                    CheckRendererUnavailable()));
        }

        if (!reference.Document.Matches(State))
        {
            return Task.FromResult(
                BrowserResult<BrowserCheckReceipt>.Failure(
                    CheckStateChanged()));
        }

        if (!allowedOrigin.Allows(State.Address))
        {
            return Task.FromResult(
                BrowserResult<BrowserCheckReceipt>.Failure(
                    PolicyError()));
        }

        SnapshotReferenceLease? lease;
        lock (_snapshotReferenceGate)
        {
            if (!_snapshotReferences.TryGetValue(
                    reference.Value,
                    out lease)
                || lease.Document != reference.Document
                || !ReferenceEquals(lease.NativeView, _nativeView)
                || lease.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                _snapshotReferences.Remove(reference.Value);
                lease = null;
            }
        }

        if (lease is null)
        {
            return Task.FromResult(
                BrowserResult<BrowserCheckReceipt>.Failure(
                    ElementReferenceStale()));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(CheckCancelled());
        }

        var pending = new PendingElementCheck(
            _nativeView,
            reference.Document,
            allowedOrigin);
        _pendingElementCheck = pending;
        InvalidateElementReferences();
        if (cancellationToken.IsCancellationRequested)
        {
            _pendingElementCheck = null;
            return Task.FromResult(CheckCancelled());
        }

        try
        {
            pending.NativeDispatchCommitted = true;
            pending.NativeCompletion =
                _nativeView.CheckAsync(lease.Handle);
        }
        catch (Exception)
        {
            CompleteAmbiguousElementCheck(pending);
            return pending.Completion.Task;
        }

        _ = ObserveElementCheckDeadlineAsync(pending);
        _ = ObserveNativeElementCheckAsync(pending);
        return pending.Completion.Task;
    }

    private async Task ObserveNativeElementCheckAsync(
        PendingElementCheck pending)
    {
        NativeBrowserCheckResult result;
        try
        {
            result = await pending.NativeCompletion!
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = NativeBrowserCheckResult.OutcomeUnknown();
        }

        try
        {
            if (_dispatcher.CheckAccess())
            {
                RecordNativeElementCheckResult(pending, result);
            }
            else
            {
                await _dispatcher.InvokeAsync(
                    () =>
                    {
                        RecordNativeElementCheckResult(
                            pending,
                            result);
                        return true;
                    });
            }
        }
        catch (Exception)
        {
            CompleteElementCheckAfterDispatcherFailure(pending);
        }
    }

    private async Task ObserveElementCheckDeadlineAsync(
        PendingElementCheck pending)
    {
        try
        {
            await Task.Delay(
                    _nativeSnapshotDeadline,
                    pending.DeadlineCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!pending.TryClaimTimeout())
        {
            return;
        }

        pending.RetireDeadline();
        pending.Completion.TrySetResult(
            BrowserResult<BrowserCheckReceipt>.Failure(
                InteractionOutcomeUnknown()));
        try
        {
            if (_dispatcher.CheckAccess())
            {
                TimeoutElementCheck(pending);
            }
            else
            {
                await _dispatcher.InvokeAsync(
                    () =>
                    {
                        TimeoutElementCheck(pending);
                        return true;
                    });
            }
        }
        catch (Exception)
        {
            CompleteElementCheckAfterDispatcherFailure(pending);
        }
    }

    private void CompleteElementCheckAfterDispatcherFailure(
        PendingElementCheck pending)
    {
        if (!ReferenceEquals(_pendingElementCheck, pending))
        {
            return;
        }

        var claimedCompletion = pending.TryClaimCompletion();
        if (!claimedCompletion && !pending.HasTimedOut)
        {
            return;
        }

        _interactionRecoveryFailed = true;
        pending.RetireDeadline();
        pending.Completion.TrySetResult(
            BrowserResult<BrowserCheckReceipt>.Failure(
                InteractionOutcomeUnknown()));
    }

    private void RecordNativeElementCheckResult(
        PendingElementCheck pending,
        NativeBrowserCheckResult result)
    {
        if (!ReferenceEquals(_pendingElementCheck, pending)
            || !ReferenceEquals(_nativeView, pending.NativeView)
            || pending.HasTerminalClaim)
        {
            return;
        }

        pending.NativeResult = result
            ?? NativeBrowserCheckResult.OutcomeUnknown();
        TryCompleteElementCheck(pending);
    }

    private void TryCompleteElementCheck(PendingElementCheck pending)
    {
        if (!ReferenceEquals(_pendingElementCheck, pending)
            || pending.NativeResult is not { } nativeResult
            || pending.HasTerminalClaim)
        {
            return;
        }

        switch (nativeResult.Status)
        {
            case NativeBrowserCheckStatus.OutcomeUnknown:
                CompleteAmbiguousElementCheck(pending);
                return;
            case NativeBrowserCheckStatus.Stale:
                if (pending.HasObservedNavigationStart)
                {
                    CompleteAmbiguousElementCheck(pending);
                    return;
                }

                CompleteElementCheck(
                    pending,
                    BrowserResult<BrowserCheckReceipt>.Failure(
                        ElementReferenceStale()));
                return;
            case NativeBrowserCheckStatus.NotInteractable:
                if (pending.HasObservedNavigationStart)
                {
                    CompleteAmbiguousElementCheck(pending);
                    return;
                }

                CompleteElementCheck(
                    pending,
                    BrowserResult<BrowserCheckReceipt>.Failure(
                        ElementNotInteractable()));
                return;
            case NativeBrowserCheckStatus.NotCheckable:
                if (pending.HasObservedNavigationStart)
                {
                    CompleteAmbiguousElementCheck(pending);
                    return;
                }

                CompleteElementCheck(
                    pending,
                    BrowserResult<BrowserCheckReceipt>.Failure(
                        ElementNotCheckable()));
                return;
            case NativeBrowserCheckStatus.Checked:
                if (pending.HasObservedNavigationStart
                    && !pending.NavigationTerminal)
                {
                    return;
                }

                if (pending.NavigationError is { } navigationError)
                {
                    CompleteElementCheckFailure(
                        pending,
                        navigationError,
                        pending.RequiresQuarantine);
                    return;
                }

                if (!pending.HasObservedNavigationStart
                    && !pending.NavigationObservationBarrierPassed)
                {
                    ScheduleElementCheckNavigationObservationBarrier(
                        pending);
                    return;
                }

                CompleteElementCheck(
                    pending,
                    BrowserResult<BrowserCheckReceipt>.Success(
                        new BrowserCheckReceipt(
                            pending.SourceDocument)));
                return;
            default:
                CompleteAmbiguousElementCheck(pending);
                return;
        }
    }

    private void ScheduleElementCheckNavigationObservationBarrier(
        PendingElementCheck pending)
    {
        if (!pending.TryScheduleNavigationObservationBarrier())
        {
            return;
        }

        try
        {
            _dispatcher.Post(
                () =>
                {
                    pending.MarkNavigationObservationBarrierPassed();
                    TryCompleteElementCheck(pending);
                });
        }
        catch (Exception)
        {
            CompleteElementCheckAfterDispatcherFailure(pending);
        }
    }

    private void TimeoutElementCheck(PendingElementCheck pending)
    {
        if (!ReferenceEquals(_pendingElementCheck, pending)
            || !pending.HasTimedOut)
        {
            return;
        }

        InvalidateElementReferences();
        var replaced = TryReplaceQuarantinedNativeView();
        pending.NativeViewWasReplaced = replaced;
        if (!replaced)
        {
            _interactionRecoveryFailed = true;
        }

        _pendingElementCheck = null;
    }

    private void CompleteAmbiguousElementCheck(
        PendingElementCheck pending)
    {
        if (!pending.TryClaimCompletion())
        {
            return;
        }

        if (!ReferenceEquals(_pendingElementCheck, pending))
        {
            pending.RetireDeadline();
            pending.Completion.TrySetResult(
                BrowserResult<BrowserCheckReceipt>.Failure(
                    InteractionOutcomeUnknown()));
            return;
        }

        InvalidateElementReferences();
        var replaced = TryReplaceQuarantinedNativeView();
        pending.NativeViewWasReplaced = replaced;
        if (!replaced)
        {
            _interactionRecoveryFailed = true;
        }

        FinishClaimedElementCheck(
            pending,
            BrowserResult<BrowserCheckReceipt>.Failure(
                InteractionOutcomeUnknown()));
    }

    private void CompleteElementCheckFailure(
        PendingElementCheck pending,
        BrowserError error,
        bool quarantine)
    {
        if (!ReferenceEquals(_pendingElementCheck, pending)
            || !pending.TryClaimCompletion())
        {
            return;
        }

        if (quarantine)
        {
            var replaced = TryReplaceQuarantinedNativeView();
            pending.NativeViewWasReplaced = replaced;
            if (!replaced)
            {
                _interactionRecoveryFailed = true;
            }
        }

        FinishClaimedElementCheck(
            pending,
            BrowserResult<BrowserCheckReceipt>.Failure(error));
    }

    private void CompleteElementCheck(
        PendingElementCheck pending,
        BrowserResult<BrowserCheckReceipt> result)
    {
        if (!ReferenceEquals(_pendingElementCheck, pending))
        {
            return;
        }

        if (!pending.TryClaimCompletion())
        {
            return;
        }

        FinishClaimedElementCheck(pending, result);
    }

    private void FinishClaimedElementCheck(
        PendingElementCheck pending,
        BrowserResult<BrowserCheckReceipt> result)
    {
        _pendingElementCheck = null;
        pending.RetireDeadline();
        pending.Completion.TrySetResult(result);
    }

    private Task<BrowserResult<BrowserDocumentSnapshot>>
        BeginDocumentSnapshot(
            BrowserDocumentBinding document,
            CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(SnapshotCancelled());
        }

        if (_interactionRecoveryFailed)
        {
            return Task.FromResult(
                BrowserResult<BrowserDocumentSnapshot>.Failure(
                    SnapshotRendererUnavailable()));
        }

        if (_pendingDocumentSnapshot is not null
            || HasPendingElementInteraction
            || HasGovernedNavigationActivity
            || State.LoadState == BrowserLoadState.Loading)
        {
            return Task.FromResult(
                BrowserResult<BrowserDocumentSnapshot>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationInProgress,
                        "The browser cannot capture a document while navigation is in progress.",
                        retryable: true)));
        }

        if (State.LoadState != BrowserLoadState.Ready)
        {
            return Task.FromResult(
                BrowserResult<BrowserDocumentSnapshot>.Failure(
                    SnapshotRendererUnavailable()));
        }

        if (!document.Matches(State))
        {
            return Task.FromResult(
                BrowserResult<BrowserDocumentSnapshot>.Failure(
                    SnapshotStateChanged()));
        }

        InvalidateElementReferences();
        var referenceEpoch = SnapshotReferenceEpoch();
        Task<NativeBrowserSnapshotResult> nativeCompletion;
        try
        {
            nativeCompletion = _nativeView.CaptureSnapshotAsync();
        }
        catch (Exception)
        {
            return Task.FromResult(
                BrowserResult<BrowserDocumentSnapshot>.Failure(
                    SnapshotRendererUnavailable()));
        }

        var pending = new PendingDocumentSnapshot(
            _nativeView,
            document,
            nativeCompletion,
            referenceEpoch);
        _pendingDocumentSnapshot = pending;
        pending.CancellationRegistration = cancellationToken.Register(
            () =>
            {
                if (_dispatcher.CheckAccess())
                {
                    CancelDocumentSnapshot(pending);
                }
                else
                {
                    _dispatcher.Post(
                        () => CancelDocumentSnapshot(pending));
                }
            });
        _ = ObserveDocumentSnapshotAsync(pending);
        return pending.Completion.Task;
    }

    private async Task ObserveDocumentSnapshotAsync(
        PendingDocumentSnapshot pending)
    {
        var deadlineCancellation = new CancellationTokenSource();
        var deadlineCompletion = Task.Delay(
            _nativeSnapshotDeadline,
            deadlineCancellation.Token);
        var firstCompletion = await Task.WhenAny(
                pending.NativeCompletion,
                deadlineCompletion)
            .ConfigureAwait(false);
        if (ReferenceEquals(firstCompletion, pending.NativeCompletion))
        {
            deadlineCancellation.Cancel();
        }

        deadlineCancellation.Dispose();
        if (ReferenceEquals(firstCompletion, deadlineCompletion)
            && pending.TryMarkTimedOut())
        {
            pending.Completion.TrySetResult(
                BrowserResult<BrowserDocumentSnapshot>.Failure(
                    SnapshotRendererUnavailable()));
            try
            {
                if (_dispatcher.CheckAccess())
                {
                    TimeoutDocumentSnapshot(pending);
                }
                else
                {
                    await _dispatcher.InvokeAsync(
                        () =>
                        {
                            TimeoutDocumentSnapshot(pending);
                            return true;
                        });
                }
            }
            catch (Exception)
            {
                CompleteDocumentSnapshotAfterDispatcherFailure(pending);
            }
        }

        if (pending.NativeViewWasReplaced)
        {
            _ = ObserveDetachedNativeSnapshotAsync(
                pending.NativeCompletion);
            return;
        }

        NativeBrowserSnapshotResult nativeResult;
        try
        {
            nativeResult = await pending.NativeCompletion
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            nativeResult = NativeBrowserSnapshotResult.Unavailable();
        }

        try
        {
            if (_dispatcher.CheckAccess())
            {
                CompleteDocumentSnapshot(pending, nativeResult);
            }
            else
            {
                await _dispatcher.InvokeAsync(
                    () =>
                    {
                        CompleteDocumentSnapshot(pending, nativeResult);
                        return true;
                    });
            }
        }
        catch (Exception)
        {
            CompleteDocumentSnapshotAfterDispatcherFailure(pending);
        }
    }

    private static async Task ObserveDetachedNativeSnapshotAsync(
        Task<NativeBrowserSnapshotResult> nativeCompletion)
    {
        try
        {
            _ = await nativeCompletion.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A quarantined adapter is no longer authoritative, but its
            // non-cancellable native invocation must still be observed.
        }
    }

    private void TimeoutDocumentSnapshot(
        PendingDocumentSnapshot pending)
    {
        if (!ReferenceEquals(_pendingDocumentSnapshot, pending)
            || !pending.HasTimedOut)
        {
            return;
        }

        pending.CancellationRegistration.Unregister();
        if (TryReplaceQuarantinedNativeView())
        {
            pending.NativeViewWasReplaced = true;
            _pendingDocumentSnapshot = null;
        }
    }

    private void CancelDocumentSnapshot(
        PendingDocumentSnapshot pending)
    {
        if (!ReferenceEquals(_pendingDocumentSnapshot, pending)
            || pending.CallerCancelled)
        {
            return;
        }

        pending.CallerCancelled = true;
        pending.Completion.TrySetResult(SnapshotCancelled());
    }

    private void CompleteDocumentSnapshot(
        PendingDocumentSnapshot pending,
        NativeBrowserSnapshotResult nativeResult)
    {
        if (!ReferenceEquals(_pendingDocumentSnapshot, pending))
        {
            return;
        }

        _pendingDocumentSnapshot = null;
        pending.CancellationRegistration.Unregister();
        if (pending.CallerCancelled || pending.HasTimedOut)
        {
            return;
        }

        if (!ReferenceEquals(_nativeView, pending.NativeView)
            || State.LoadState != BrowserLoadState.Ready
            || HasGovernedNavigationActivity
            || !pending.Document.Matches(State)
            || pending.ReferenceEpoch != SnapshotReferenceEpoch())
        {
            pending.Completion.TrySetResult(
                BrowserResult<BrowserDocumentSnapshot>.Failure(
                    SnapshotStateChanged()));
            return;
        }

        if (!nativeResult.IsSuccess)
        {
            var error = nativeResult.Failure
                == NativeBrowserSnapshotFailure.Invalid
                ? SnapshotInvalid()
                : SnapshotRendererUnavailable();
            pending.Completion.TrySetResult(
                BrowserResult<BrowserDocumentSnapshot>.Failure(error));
            return;
        }

        try
        {
            var capturedAtUtc = _timeProvider.GetUtcNow();
            var expiresAtUtc = capturedAtUtc + ElementReferenceLifetime;
            var references =
                new Dictionary<string, SnapshotReferenceLease>(
                    StringComparer.Ordinal);
            var nodes = new BrowserSnapshotNode[
                nativeResult.Value!.Nodes.Count];
            for (var index = 0; index < nodes.Length; index++)
            {
                var nativeNode = nativeResult.Value.Nodes[index];
                BrowserElementReference? reference = null;
                if (nativeNode.Handle is { } handle)
                {
                    reference = NewElementReference(
                        pending.Document,
                        references);
                    references.Add(
                        reference.Value,
                        new SnapshotReferenceLease(
                            pending.Document,
                            pending.NativeView,
                            handle,
                            expiresAtUtc));
                }

                nodes[index] = new BrowserSnapshotNode(
                    nativeNode.Depth,
                    nativeNode.Role,
                    nativeNode.Name,
                    reference,
                    nativeNode.States);
            }

            var snapshot = new BrowserDocumentSnapshot(
                pending.Document,
                nodes,
                capturedAtUtc,
                nativeResult.Value.IsTruncated);
            lock (_snapshotReferenceGate)
            {
                _snapshotReferences = references;
            }

            pending.Completion.TrySetResult(
                BrowserResult<BrowserDocumentSnapshot>.Success(snapshot));
        }
        catch (Exception)
        {
            InvalidateElementReferences();
            pending.Completion.TrySetResult(
                BrowserResult<BrowserDocumentSnapshot>.Failure(
                    SnapshotInvalid()));
        }
    }

    private void CompleteDocumentSnapshotAfterDispatcherFailure(
        PendingDocumentSnapshot pending)
    {
        pending.CancellationRegistration.Unregister();
        if (!pending.CallerCancelled)
        {
            pending.Completion.TrySetResult(
                BrowserResult<BrowserDocumentSnapshot>.Failure(
                    SnapshotRendererUnavailable()));
        }
    }

    private static BrowserElementReference NewElementReference(
        BrowserDocumentBinding document,
        IReadOnlyDictionary<string, SnapshotReferenceLease> existing)
    {
        while (true)
        {
            var value = string.Concat(
                "be_",
                Convert.ToBase64String(
                        RandomNumberGenerator.GetBytes(18))
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_'));
            if (!existing.ContainsKey(value))
            {
                return new BrowserElementReference(value, document);
            }
        }
    }

    private void InvalidateElementReferences()
    {
        lock (_snapshotReferenceGate)
        {
            _snapshotReferences = [];
            _snapshotReferenceEpoch = unchecked(
                _snapshotReferenceEpoch + 1);
        }
    }

    private long SnapshotReferenceEpoch()
    {
        lock (_snapshotReferenceGate)
        {
            return _snapshotReferenceEpoch;
        }
    }

    private BrowserResult<BrowserSessionState> NavigateHistory(
        Func<bool> navigate,
        string unavailableMessage)
    {
        if (!navigate())
        {
            return BrowserResult<BrowserSessionState>.Failure(
                BrowserError.Create(
                    BrowserErrorCode.HistoryUnavailable,
                    unavailableMessage));
        }

        PublishLoading(State.Address);
        return BrowserResult<BrowserSessionState>.Success(State);
    }

    private Task<BrowserResult<BrowserSessionState>>
        BeginOriginConstrainedNavigation(
            BrowserOriginConstrainedNavigationRequest request,
            BrowserNavigationOrigin allowedOrigin,
            BrowserNavigationStartBinding startBinding,
            CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Cancelled());
        }

        if (_interactionRecoveryFailed)
        {
            return Task.FromResult(
                InteractionRecoveryUnavailable());
        }

        if (HasTimedOutDocumentSnapshot)
        {
            return Task.FromResult(SnapshotRecoveryInProgress());
        }

        if (_pendingDocumentSnapshot is not null
            || HasPendingElementInteraction)
        {
            return Task.FromResult(NavigationInProgress());
        }

        if (HasGovernedNavigationActivity
            || State.LoadState == BrowserLoadState.Loading)
        {
            return Task.FromResult(NavigationInProgress());
        }

        if (!startBinding.Matches(State))
        {
            return Task.FromResult(NavigationStateChanged());
        }

        var initialAddress = request switch
        {
            BrowserOriginConstrainedNavigationRequest.Navigate navigate =>
                navigate.Address,
            BrowserOriginConstrainedNavigationRequest.Back
                or BrowserOriginConstrainedNavigationRequest.Forward
                or BrowserOriginConstrainedNavigationRequest.Reload =>
                State.Address,
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.GetType(),
                "The constrained browser operation is unsupported."),
        };
        if (!allowedOrigin.Allows(initialAddress))
        {
            return Task.FromResult(PolicyDenied());
        }

        var pending = new PendingOriginConstrainedNavigation(
            allowedOrigin,
            State);
        _pendingGovernedNavigation = pending;
        pending.CancellationRegistration = cancellationToken.Register(
            () =>
            {
                if (_dispatcher.CheckAccess())
                {
                    CompleteCancelledGovernedNavigation(
                        pending,
                        stopNativeView: true);
                }
                else
                {
                    _dispatcher.Post(
                        () => CompleteCancelledGovernedNavigation(
                            pending,
                            stopNativeView: true));
                }
            });

        if (cancellationToken.IsCancellationRequested)
        {
            CompleteCancelledGovernedNavigation(
                pending,
                stopNativeView: false);
            return pending.Completion.Task;
        }

        try
        {
            pending.NativeDispatchStarted = true;
            var accepted = request switch
            {
                BrowserOriginConstrainedNavigationRequest.Navigate navigate =>
                    NavigateNative(navigate.Address),
                BrowserOriginConstrainedNavigationRequest.Back =>
                    _nativeView.GoBack(),
                BrowserOriginConstrainedNavigationRequest.Forward =>
                    _nativeView.GoForward(),
                BrowserOriginConstrainedNavigationRequest.Reload =>
                    _nativeView.Reload(),
                _ => false,
            };
            if (!ReferenceEquals(_pendingGovernedNavigation, pending))
            {
                return pending.Completion.Task;
            }

            if (!accepted)
            {
                pending.NativeDispatchStarted = false;
                var error = request switch
                {
                    BrowserOriginConstrainedNavigationRequest.Back =>
                        BrowserError.Create(
                            BrowserErrorCode.HistoryUnavailable,
                            "No previous browser history entry is available."),
                    BrowserOriginConstrainedNavigationRequest.Forward =>
                        BrowserError.Create(
                            BrowserErrorCode.HistoryUnavailable,
                            "No next browser history entry is available."),
                    _ => BrowserError.Create(
                        BrowserErrorCode.RendererUnavailable,
                        "The browser cannot navigate before its native renderer is ready.",
                        retryable: true),
                };
                CompleteGovernedNavigation(
                    pending,
                    BrowserResult<BrowserSessionState>.Failure(error));
                return pending.Completion.Task;
            }

            if (!pending.HasObservedNavigationStart)
            {
                PublishLoading(initialAddress);
            }
        }
        catch (OperationCanceledException)
        {
            CompleteCancelledGovernedNavigation(
                pending,
                stopNativeView: true);
        }
        catch (Exception)
        {
            if (!ReferenceEquals(_pendingGovernedNavigation, pending))
            {
                return pending.Completion.Task;
            }

            BeginDrainingNativeNavigation(pending);
            var error = EngineFailure();
            if (!RetireGovernedNavigation(pending))
            {
                return pending.Completion.Task;
            }

            PublishGovernedFailure(pending, error);
            CompleteRetiredGovernedNavigation(
                pending,
                BrowserResult<BrowserSessionState>.Failure(error));
        }

        return pending.Completion.Task;
    }

    private bool NavigateNative(BrowserAddress address)
    {
        _nativeView.Navigate(address);
        return true;
    }

    private async ValueTask<BrowserResult<BrowserSessionState>> RunOnUiThreadAsync(
        Func<BrowserResult<BrowserSessionState>> operation,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        if (_dispatcher.CheckAccess())
        {
            return RunEngineOperation(operation);
        }

        try
        {
            return await _dispatcher.InvokeAsync(
                () => cancellationToken.IsCancellationRequested
                    ? Cancelled()
                    : RunEngineOperation(operation));
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (Exception)
        {
            return BrowserResult<BrowserSessionState>.Failure(EngineFailure());
        }
    }

    private BrowserResult<BrowserSessionState> RunEngineOperation(
        Func<BrowserResult<BrowserSessionState>> operation)
    {
        try
        {
            return operation();
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (Exception)
        {
            return FailEngine();
        }
    }

    private void OnNavigationStarted(
        object? sender,
        NativeBrowserNavigationEventArgs args)
    {
        if (!ReferenceEquals(sender, _nativeView))
        {
            args.Cancel = true;
            return;
        }

        if (_interactionRecoveryFailed)
        {
            RecordTerminalNavigation(args.NavigationGeneration);
            args.Cancel = true;
            return;
        }

        if (HasTimedOutDocumentSnapshot)
        {
            RecordTerminalNavigation(args.NavigationGeneration);
            args.Cancel = true;
            return;
        }

        if (args.NavigationGeneration
            <= _lastTerminalNavigationGeneration)
        {
            args.Cancel = true;
            return;
        }

        if (_drainingNativeNavigation is { } draining)
        {
            draining.ObserveDelayedStart(
                args.NavigationGeneration);
            args.Cancel = true;
            return;
        }

        if (PendingInteraction is { } interaction)
        {
            if (!interaction.ObserveStart(args.NavigationGeneration))
            {
                interaction.NavigationError =
                    InteractionOutcomeUnknown();
                interaction.RequiresQuarantine = true;
                args.Cancel = true;
                return;
            }

            if (!interaction.AllowedOrigin.Allows(args.Address))
            {
                interaction.NavigationError = PolicyError();
                interaction.RequiresQuarantine = true;
                args.Cancel = true;
                return;
            }

            PublishLoading(args.Address);
            return;
        }

        if (_pendingGovernedNavigation is { } pending)
        {
            if (!pending.ObserveStart(
                    args.NavigationGeneration)
                || !pending.AllowedOrigin.Allows(args.Address))
            {
                args.Cancel = true;
                return;
            }

            // Still the navigation the shell asked for, so a start inside it is
            // that navigation redirecting and the address follows it.
            PublishLoading(args.Address);
            return;
        }

        // Nothing was asked for, so this is the page navigating something of its
        // own — and these events fire for every frame in it with no way to tell
        // which. Taking the address from one meant a Google tab announced itself
        // as an ogs.google.com widget, and stayed that way, because the widget
        // was the last frame to start and nothing completed after it. The main
        // frame's own address arrives on completion; this is a load state and
        // nothing more.
        PublishLoading(State.Address);
    }

    private void OnNavigationCompleted(
        object? sender,
        NativeBrowserNavigationCompletedEventArgs args)
    {
        if (!ReferenceEquals(sender, _nativeView))
        {
            return;
        }

        if (_interactionRecoveryFailed)
        {
            RecordTerminalNavigation(args.NavigationGeneration);
            return;
        }

        if (HasTimedOutDocumentSnapshot)
        {
            RecordTerminalNavigation(args.NavigationGeneration);
            return;
        }

        if (args.NavigationGeneration
            <= _lastTerminalNavigationGeneration)
        {
            return;
        }

        if (_drainingNativeNavigation is { } draining)
        {
            if (draining.MatchesTerminalCompletion(
                    args.NavigationGeneration))
            {
                RecordTerminalNavigation(
                    args.NavigationGeneration);
                if (TryReplaceQuarantinedNativeView())
                {
                    _drainingNativeNavigation = null;
                }
            }

            return;
        }

        if (PendingInteraction is { } interaction)
        {
            if (!interaction.ObserveGeneration(
                    args.NavigationGeneration))
            {
                return;
            }

            RecordTerminalNavigation(args.NavigationGeneration);
            interaction.NavigationTerminal = true;
            if (args.Address is null
                || !interaction.AllowedOrigin.Allows(args.Address))
            {
                interaction.NavigationError = PolicyError();
                interaction.RequiresQuarantine = true;
            }
            else if (!args.IsSuccess)
            {
                interaction.NavigationError =
                    InteractionOutcomeUnknown();
                interaction.RequiresQuarantine = true;
            }
            else if (interaction.SourceDocument.DocumentRevision
                == long.MaxValue)
            {
                interaction.NavigationError =
                    InteractionOutcomeUnknown();
                interaction.RequiresQuarantine = true;
            }
            else
            {
                Publish(new BrowserSessionState(
                    args.Address,
                    string.Empty,
                    BrowserLoadState.Ready,
                    _nativeView.CanGoBack,
                    _nativeView.CanGoForward,
                    interaction.SourceDocument.DocumentRevision + 1));
            }

            CompleteElementInteractionAfterNavigation(interaction);
            return;
        }

        if (_pendingGovernedNavigation is { } pending)
        {
            if (!pending.ObserveGeneration(
                    args.NavigationGeneration))
            {
                return;
            }

            RecordTerminalNavigation(args.NavigationGeneration);
            CompleteGovernedNavigationFromNative(pending, args);
            return;
        }

        RecordTerminalNavigation(args.NavigationGeneration);
        if (args.WasStopped)
        {
            // StopAsync has already restored Ready in the usual asynchronous
            // path. Keep this callback correct if CEF reports the terminal
            // abort synchronously: stopping neither commits a document nor
            // turns the current document into a navigation failure.
            Publish(new BrowserSessionState(
                State.Address,
                State.Title,
                BrowserLoadState.Ready,
                _nativeView.CanGoBack,
                _nativeView.CanGoForward,
                State.DocumentRevision));
            return;
        }

        var address = args.Address ?? State.Address;
        if (!args.IsSuccess)
        {
            PublishFailure(
                address,
                BrowserError.Create(
                    BrowserErrorCode.NavigationFailed,
                    "The browser could not load the requested page.",
                    retryable: true));
            return;
        }

        if (State.DocumentRevision == long.MaxValue)
        {
            _ = FailEngine();
            return;
        }

        Publish(new BrowserSessionState(
            address,
            string.Empty,
            BrowserLoadState.Ready,
            _nativeView.CanGoBack,
            _nativeView.CanGoForward,
            State.DocumentRevision + 1));
    }

    private void OnAddressChanged(
        object? sender,
        NativeBrowserAddressChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _nativeView)
            || _disposed
            || State.LoadState == BrowserLoadState.Loading
            || State.Address == args.Address)
        {
            return;
        }

        // History API and fragment changes keep the current document alive.
        // Preserve its title, readiness, and revision while updating browser
        // chrome and the history buttons from Chromium's latest state.
        Publish(new BrowserSessionState(
            args.Address,
            State.Title,
            State.LoadState,
            _nativeView.CanGoBack,
            _nativeView.CanGoForward,
            State.DocumentRevision,
            State.Failure));
    }

    private void OnNavigationRejected(
        object? sender,
        NativeBrowserNavigationRejectedEventArgs args)
    {
        if (!ReferenceEquals(sender, _nativeView))
        {
            return;
        }

        if (_interactionRecoveryFailed)
        {
            RecordTerminalNavigation(args.NavigationGeneration);
            return;
        }

        if (HasTimedOutDocumentSnapshot)
        {
            RecordTerminalNavigation(args.NavigationGeneration);
            return;
        }

        if (args.NavigationGeneration
            <= _lastTerminalNavigationGeneration)
        {
            return;
        }

        if (_drainingNativeNavigation is not null)
        {
            return;
        }

        if (PendingInteraction is { } interaction)
        {
            if (!interaction.ObserveGeneration(
                    args.NavigationGeneration))
            {
                return;
            }

            RecordTerminalNavigation(args.NavigationGeneration);
            interaction.NavigationTerminal = true;
            interaction.NavigationError = args.Reason switch
            {
                NativeBrowserNavigationRejectionReason.OriginPolicy
                    or NativeBrowserNavigationRejectionReason.UnsupportedAddress =>
                    PolicyError(),
                _ => InteractionOutcomeUnknown(),
            };
            interaction.RequiresQuarantine = true;
            CompleteElementInteractionAfterNavigation(interaction);
            return;
        }

        if (_pendingGovernedNavigation is { } pending)
        {
            if (!pending.ObserveGeneration(
                    args.NavigationGeneration))
            {
                return;
            }

            BeginDrainingNativeNavigation(pending);
            var error = args.Reason switch
            {
                NativeBrowserNavigationRejectionReason.OriginPolicy
                    or NativeBrowserNavigationRejectionReason.UnsupportedAddress =>
                    PolicyError(),
                _ => EngineFailure(),
            };
            if (!RetireGovernedNavigation(pending))
            {
                return;
            }

            PublishGovernedFailure(pending, error);
            CompleteRetiredGovernedNavigation(
                pending,
                BrowserResult<BrowserSessionState>.Failure(error));
            return;
        }

        if (args.Reason
            == NativeBrowserNavigationRejectionReason.OriginPolicy)
        {
            return;
        }

        var unsupportedError = BrowserError.Create(
            BrowserErrorCode.NavigationFailed,
            "The browser blocked an unsupported top-level address.");
        _drainingNativeNavigation =
            DrainingNativeNavigation.FromRejected(
                args.NavigationGeneration);
        PublishFailure(State.Address, unsupportedError);
    }

    private void CompleteGovernedNavigationFromNative(
        PendingOriginConstrainedNavigation pending,
        NativeBrowserNavigationCompletedEventArgs args)
    {
        if (!RetireGovernedNavigation(pending))
        {
            return;
        }

        if (args.Address is null)
        {
            var missingAddress = BrowserError.Create(
                BrowserErrorCode.NavigationFailed,
                "The browser did not report the completed top-level address.",
                retryable: true);
            PublishGovernedFailure(pending, missingAddress);
            QuarantineCompletedNativeNavigation(
                args.NavigationGeneration);
            CompleteRetiredGovernedNavigation(
                pending,
                BrowserResult<BrowserSessionState>.Failure(missingAddress));
            return;
        }

        if (!pending.AllowedOrigin.Allows(args.Address))
        {
            try
            {
                _nativeView.Stop();
            }
            catch (Exception)
            {
                // The policy failure remains authoritative even if the native
                // engine cannot acknowledge this best-effort stop.
            }

            var denied = PolicyError();
            PublishGovernedFailure(pending, denied);
            QuarantineCompletedNativeNavigation(
                args.NavigationGeneration);
            CompleteRetiredGovernedNavigation(
                pending,
                BrowserResult<BrowserSessionState>.Failure(denied));
            return;
        }

        if (!args.IsSuccess)
        {
            var failed = BrowserError.Create(
                BrowserErrorCode.NavigationFailed,
                "The browser could not load the requested page.",
                retryable: true);
            PublishGovernedFailure(pending, failed);
            CompleteRetiredGovernedNavigation(
                pending,
                BrowserResult<BrowserSessionState>.Failure(failed));
            return;
        }

        if (pending.CommittedState.DocumentRevision == long.MaxValue)
        {
            var error = EngineFailure();
            PublishGovernedFailure(pending, error);
            CompleteRetiredGovernedNavigation(
                pending,
                BrowserResult<BrowserSessionState>.Failure(error));
            return;
        }

        Publish(new BrowserSessionState(
            args.Address,
            string.Empty,
            BrowserLoadState.Ready,
            _nativeView.CanGoBack,
            _nativeView.CanGoForward,
            pending.CommittedState.DocumentRevision + 1));
        CompleteRetiredGovernedNavigation(
            pending,
            BrowserResult<BrowserSessionState>.Success(State));
    }

    private bool CompleteCancelledGovernedNavigation(
        PendingOriginConstrainedNavigation pending,
        bool stopNativeView)
    {
        if (!ReferenceEquals(_pendingGovernedNavigation, pending))
        {
            return true;
        }

        BeginDrainingNativeNavigation(pending);
        var stopAccepted = true;
        if (stopNativeView)
        {
            try
            {
                stopAccepted = _nativeView.Stop();
            }
            catch (Exception)
            {
                stopAccepted = false;
                // Cancellation remains authoritative even if the native
                // renderer has already gone away.
            }
        }

        if (!RetireGovernedNavigation(pending))
        {
            return stopAccepted;
        }

        Publish(pending.CommittedState);
        CompleteRetiredGovernedNavigation(pending, Cancelled());
        return stopAccepted;
    }

    private void BeginDrainingNativeNavigation(
        PendingOriginConstrainedNavigation pending)
    {
        if (!pending.NativeDispatchStarted)
        {
            return;
        }

        _drainingNativeNavigation =
            DrainingNativeNavigation.FromPending(pending);
    }

    private void RecordTerminalNavigation(long navigationGeneration)
    {
        if (navigationGeneration
            > _lastTerminalNavigationGeneration)
        {
            _lastTerminalNavigationGeneration =
                navigationGeneration;
        }
    }

    private void QuarantineCompletedNativeNavigation(
        long navigationGeneration)
    {
        _drainingNativeNavigation =
            DrainingNativeNavigation.FromRejected(
                navigationGeneration);
        if (TryReplaceQuarantinedNativeView())
        {
            _drainingNativeNavigation = null;
        }
    }

    private bool TryReplaceQuarantinedNativeView()
    {
        if (_nativeViewReplacementFactory is null
            || State.DocumentRevision == long.MaxValue)
        {
            return false;
        }

        IEmbeddedBrowserView replacement;
        try
        {
            replacement = _nativeViewReplacementFactory()
                ?? throw new InvalidOperationException(
                    "The embedded browser replacement factory returned null.");
            if (ReferenceEquals(replacement, _nativeView))
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }

        var quarantined = _nativeView;
        quarantined.NavigationStarted -= OnNavigationStarted;
        quarantined.NavigationCompleted -= OnNavigationCompleted;
        quarantined.AddressChanged -= OnAddressChanged;
        quarantined.NavigationRejected -= OnNavigationRejected;
        quarantined.RenderProcessFailed -= OnRenderProcessFailed;
        _nativeView = replacement;
        try
        {
            if (_nativeViewReplacementPresenter is { } present)
            {
                present(replacement.View);
            }
            else
            {
                Content = replacement.View;
            }
        }
        catch (Exception)
        {
            _nativeView = quarantined;
            quarantined.NavigationStarted += OnNavigationStarted;
            quarantined.NavigationCompleted += OnNavigationCompleted;
            quarantined.AddressChanged += OnAddressChanged;
            quarantined.NavigationRejected += OnNavigationRejected;
            quarantined.RenderProcessFailed += OnRenderProcessFailed;
            replacement.Dispose();
            return false;
        }

        _nativeView.NavigationStarted += OnNavigationStarted;
        _nativeView.NavigationCompleted += OnNavigationCompleted;
        _nativeView.AddressChanged += OnAddressChanged;
        _nativeView.NavigationRejected += OnNavigationRejected;
        _nativeView.RenderProcessFailed += OnRenderProcessFailed;
        quarantined.Dispose();
        _lastTerminalNavigationGeneration = 0;
        Publish(new BrowserSessionState(
            BrowserAddress.Blank,
            string.Empty,
            BrowserLoadState.Ready,
            _nativeView.CanGoBack,
            _nativeView.CanGoForward,
            State.DocumentRevision + 1));
        if (IsFocused)
        {
            try
            {
                _nativeView.View.Focus();
            }
            catch (Exception)
            {
                // Focus recovery is best effort; adapter replacement and its
                // document-revision change remain authoritative.
            }
        }

        return true;
    }

    private void OnRenderProcessFailed(object? sender, EventArgs args)
    {
        if (_disposed || !ReferenceEquals(sender, _nativeView))
        {
            return;
        }

        if (TryReplaceQuarantinedNativeView())
        {
            return;
        }

        _interactionRecoveryFailed = true;
        PublishFailure(
            State.Address,
            BrowserError.Create(
                BrowserErrorCode.RendererUnavailable,
                "The embedded browser process stopped unexpectedly.",
                retryable: false));
    }

    private void PublishGovernedFailure(
        PendingOriginConstrainedNavigation pending,
        BrowserError error) =>
        Publish(new BrowserSessionState(
            pending.CommittedState.Address,
            string.Empty,
            BrowserLoadState.Failed,
            pending.CommittedState.CanGoBack,
            pending.CommittedState.CanGoForward,
            pending.CommittedState.DocumentRevision,
            error));

    private void CompleteGovernedNavigation(
        PendingOriginConstrainedNavigation pending,
        BrowserResult<BrowserSessionState> result)
    {
        if (!RetireGovernedNavigation(pending))
        {
            return;
        }

        CompleteRetiredGovernedNavigation(pending, result);
    }

    private bool RetireGovernedNavigation(
        PendingOriginConstrainedNavigation pending)
    {
        if (!ReferenceEquals(_pendingGovernedNavigation, pending))
        {
            return false;
        }

        _pendingGovernedNavigation = null;
        pending.CancellationRegistration.Unregister();
        return true;
    }

    private static void CompleteRetiredGovernedNavigation(
        PendingOriginConstrainedNavigation pending,
        BrowserResult<BrowserSessionState> result) =>
        pending.Completion.TrySetResult(result);

    private void PublishLoading(BrowserAddress address) =>
        PublishLoadingState(address);

    private void PublishLoadingState(BrowserAddress address)
    {
        InvalidateElementReferences();
        Publish(new BrowserSessionState(
            address,
            State.Title,
            BrowserLoadState.Loading,
            _nativeView.CanGoBack,
            _nativeView.CanGoForward,
            State.DocumentRevision));
    }

    private void PublishFailure(BrowserAddress address, BrowserError error) =>
        Publish(new BrowserSessionState(
            address,
            string.Empty,
            BrowserLoadState.Failed,
            _nativeView.CanGoBack,
            _nativeView.CanGoForward,
            State.DocumentRevision,
            error));

    private BrowserResult<BrowserSessionState> FailEngine()
    {
        var error = EngineFailure();
        PublishFailure(State.Address, error);
        return BrowserResult<BrowserSessionState>.Failure(error);
    }

    private static BrowserError EngineFailure() =>
        BrowserError.Create(
            BrowserErrorCode.EngineFailed,
            "The embedded browser engine failed.",
            retryable: true);

    private static BrowserResult<T> UnsupportedCapability<T>(
        string capability) =>
        BrowserResult<T>.Failure(
            BrowserError.Create(
                BrowserErrorCode.UnsupportedCapability,
                $"The browser capability '{capability}' is not enabled for this surface."));

    private static BrowserResult<BrowserSessionState> Cancelled() =>
        BrowserResult<BrowserSessionState>.Failure(
            BrowserError.Create(
                BrowserErrorCode.Cancelled,
                "The browser operation was cancelled."));

    private static BrowserResult<BrowserSessionState> RendererUnavailable(
        string message) =>
        BrowserResult<BrowserSessionState>.Failure(
            BrowserError.Create(
                BrowserErrorCode.RendererUnavailable,
                message,
                retryable: true));

    private static BrowserResult<BrowserSessionState>
        SnapshotRecoveryInProgress() =>
        RendererUnavailable(
            "The embedded browser renderer is recovering from a timed-out document snapshot.");

    private static BrowserResult<BrowserSessionState>
        InteractionRecoveryUnavailable() =>
        RendererUnavailable(
            "The embedded browser renderer is unavailable after an ambiguous element interaction.");

    private static BrowserResult<BrowserSessionState> NavigationInProgress() =>
        BrowserResult<BrowserSessionState>.Failure(
            BrowserError.Create(
                BrowserErrorCode.NavigationInProgress,
                "The browser already has a top-level navigation in progress.",
                retryable: true));

    private static BrowserResult<BrowserSessionState> NavigationStateChanged() =>
        BrowserResult<BrowserSessionState>.Failure(
            BrowserError.Create(
                BrowserErrorCode.NavigationStateChanged,
                "The browser document changed after navigation was authorized.",
                retryable: true));

    private static BrowserResult<BrowserDocumentSnapshot>
        SnapshotCancelled() =>
        BrowserResult<BrowserDocumentSnapshot>.Failure(
            BrowserError.Create(
                BrowserErrorCode.Cancelled,
                "The browser snapshot was cancelled."));

    private static BrowserError SnapshotRendererUnavailable() =>
        BrowserError.Create(
            BrowserErrorCode.RendererUnavailable,
            "The browser cannot capture the current document.",
            retryable: true);

    private static BrowserError SnapshotStateChanged() =>
        BrowserError.Create(
            BrowserErrorCode.NavigationStateChanged,
            "The browser document changed while its snapshot was captured.",
            retryable: true);

    private static BrowserError SnapshotInvalid() =>
        BrowserError.Create(
            BrowserErrorCode.SnapshotInvalid,
            "The browser returned an invalid document snapshot.");

    private static BrowserResult<BrowserClickReceipt>
        ClickCancelled() =>
        BrowserResult<BrowserClickReceipt>.Failure(
            BrowserError.Create(
                BrowserErrorCode.Cancelled,
                "The browser element activation was cancelled before dispatch."));

    private static BrowserError InteractionRendererUnavailable() =>
        BrowserError.Create(
            BrowserErrorCode.RendererUnavailable,
            "The browser cannot activate the referenced element.",
            retryable: true);

    private static BrowserError InteractionStateChanged() =>
        BrowserError.Create(
            BrowserErrorCode.NavigationStateChanged,
            "The browser document changed before the referenced element could be activated.",
            retryable: true);

    private static BrowserError ElementReferenceStale() =>
        BrowserError.Create(
            BrowserErrorCode.ElementReferenceStale,
            "The browser element reference is stale. Capture a fresh snapshot before retrying.",
            retryable: true);

    private static BrowserError ElementNotInteractable() =>
        BrowserError.Create(
            BrowserErrorCode.ElementNotInteractable,
            "The referenced browser element is not interactable.");

    private static void ValidateFillText(string text)
    {
        if (text.Any(character =>
                char.IsControl(character)
                && character is not '\t' and not '\n' and not '\r'))
        {
            throw new ArgumentException(
                "Browser fill text may contain tabs and line breaks but no other control characters.",
                nameof(text));
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(text);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Browser fill text must contain well-formed Unicode.",
                nameof(text),
                exception);
        }

        if (byteCount > BrowserElementFillRequest.MaximumTextBytes)
        {
            throw new ArgumentException(
                $"Browser fill text cannot exceed {BrowserElementFillRequest.MaximumTextBytes} UTF-8 bytes.",
                nameof(text));
        }
    }

    private static BrowserError ElementNotFillable() =>
        BrowserError.Create(
            BrowserErrorCode.ElementNotFillable,
            "The referenced browser element does not accept text replacement.");

    private static BrowserError ElementNotCheckable() =>
        BrowserError.Create(
            BrowserErrorCode.ElementNotCheckable,
            "The referenced browser element is not a checkbox or radio button.");

    private static BrowserError FillValueNotSupported() =>
        BrowserError.Create(
            BrowserErrorCode.FillValueNotSupported,
            "The exact text is not representable by this browser control without normalization.");

    private static BrowserResult<BrowserFillReceipt>
        FillCancelled() =>
        BrowserResult<BrowserFillReceipt>.Failure(
            BrowserError.Create(
                BrowserErrorCode.Cancelled,
                "The browser element fill was cancelled before dispatch."));

    private static BrowserError FillRendererUnavailable() =>
        BrowserError.Create(
            BrowserErrorCode.RendererUnavailable,
            "The browser cannot fill the referenced element.",
            retryable: true);

    private static BrowserError FillStateChanged() =>
        BrowserError.Create(
            BrowserErrorCode.NavigationStateChanged,
            "The browser document changed before the referenced element could be filled.",
            retryable: true);

    private static BrowserResult<BrowserCheckReceipt>
        CheckCancelled() =>
        BrowserResult<BrowserCheckReceipt>.Failure(
            BrowserError.Create(
                BrowserErrorCode.Cancelled,
                "The browser element check was cancelled before dispatch."));

    private static BrowserError CheckRendererUnavailable() =>
        BrowserError.Create(
            BrowserErrorCode.RendererUnavailable,
            "The browser cannot check the referenced element.",
            retryable: true);

    private static BrowserError CheckStateChanged() =>
        BrowserError.Create(
            BrowserErrorCode.NavigationStateChanged,
            "The browser document changed before the referenced element could be checked.",
            retryable: true);

    private static BrowserError InteractionOutcomeUnknown() =>
        BrowserError.Create(
            BrowserErrorCode.InteractionOutcomeUnknown,
            "The browser could not determine whether the element interaction completed.");

    private static BrowserResult<BrowserSessionState> PolicyDenied() =>
        BrowserResult<BrowserSessionState>.Failure(PolicyError());

    private static BrowserError PolicyError() =>
        BrowserError.Create(
            BrowserErrorCode.NavigationPolicyDenied,
            "The browser blocked a top-level navigation outside the approved origin.");

    private void Publish(BrowserSessionState state)
    {
        if (State == state)
        {
            return;
        }

        if (State.Address != state.Address
            || State.DocumentRevision != state.DocumentRevision)
        {
            InvalidateElementReferences();
        }

        State = state;
        StateChanged?.Invoke(this, new BrowserStateChangedEventArgs(state));
    }

    private bool HasGovernedNavigationActivity =>
        _pendingGovernedNavigation is not null
        || _drainingNativeNavigation is not null;

    private bool HasTimedOutDocumentSnapshot =>
        _pendingDocumentSnapshot?.HasTimedOut == true;

    private bool HasPendingElementInteraction =>
        PendingInteraction is not null;

    private PendingElementInteraction? PendingInteraction =>
        _pendingElementClick is { } click
            ? click
            : _pendingElementFill is { } fill
                ? fill
                : _pendingElementCheck;

    private void CompleteElementInteractionAfterNavigation(
        PendingElementInteraction interaction)
    {
        if (interaction is PendingElementClick click)
        {
            TryCompleteElementClick(click);
            return;
        }

        if (interaction is PendingElementFill fill)
        {
            TryCompleteElementFill(fill);
            return;
        }

        if (interaction is PendingElementCheck check)
        {
            TryCompleteElementCheck(check);
        }
    }

    private sealed class PendingOriginConstrainedNavigation(
        BrowserNavigationOrigin allowedOrigin,
        BrowserSessionState committedState)
    {
        public BrowserNavigationOrigin AllowedOrigin { get; } =
            allowedOrigin
            ?? throw new ArgumentNullException(nameof(allowedOrigin));

        public BrowserSessionState CommittedState { get; } =
            committedState
            ?? throw new ArgumentNullException(nameof(committedState));

        public TaskCompletionSource<BrowserResult<BrowserSessionState>>
            Completion
        { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration CancellationRegistration
        {
            get;
            set;
        }

        public bool HasObservedNavigationStart { get; private set; }

        public bool NativeDispatchStarted { get; set; }

        public long? NavigationGeneration { get; private set; }

        public bool ObserveStart(long navigationGeneration)
        {
            HasObservedNavigationStart = true;
            return ObserveGeneration(navigationGeneration);
        }

        public bool ObserveGeneration(long navigationGeneration)
        {
            if (navigationGeneration <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(navigationGeneration));
            }

            if (NavigationGeneration is { } existing)
            {
                return existing == navigationGeneration;
            }

            NavigationGeneration = navigationGeneration;
            return true;
        }
    }

    private sealed class PendingDocumentSnapshot(
        IEmbeddedBrowserView nativeView,
        BrowserDocumentBinding document,
        Task<NativeBrowserSnapshotResult> nativeCompletion,
        long referenceEpoch)
    {
        private int _hasTimedOut;

        public IEmbeddedBrowserView NativeView { get; } =
            nativeView ?? throw new ArgumentNullException(nameof(nativeView));

        public BrowserDocumentBinding Document { get; } =
            document ?? throw new ArgumentNullException(nameof(document));

        public Task<NativeBrowserSnapshotResult> NativeCompletion { get; } =
            nativeCompletion
            ?? throw new ArgumentNullException(nameof(nativeCompletion));

        public long ReferenceEpoch { get; } = referenceEpoch;

        public TaskCompletionSource<
            BrowserResult<BrowserDocumentSnapshot>> Completion
        { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration CancellationRegistration
        {
            get;
            set;
        }

        public bool CallerCancelled { get; set; }

        public bool HasTimedOut =>
            Volatile.Read(ref _hasTimedOut) != 0;

        public bool NativeViewWasReplaced { get; set; }

        public bool TryMarkTimedOut() =>
            Interlocked.CompareExchange(
                ref _hasTimedOut,
                1,
                0) == 0;
    }

    private sealed record SnapshotReferenceLease(
        BrowserDocumentBinding Document,
        IEmbeddedBrowserView NativeView,
        NativeBrowserElementHandle Handle,
        DateTimeOffset ExpiresAtUtc);

    private abstract class PendingElementInteraction(
        IEmbeddedBrowserView nativeView,
        BrowserDocumentBinding sourceDocument,
        BrowserNavigationOrigin allowedOrigin)
    {
        public IEmbeddedBrowserView NativeView { get; } =
            nativeView ?? throw new ArgumentNullException(nameof(nativeView));

        public BrowserDocumentBinding SourceDocument { get; } =
            sourceDocument
            ?? throw new ArgumentNullException(nameof(sourceDocument));

        public BrowserNavigationOrigin AllowedOrigin { get; } =
            allowedOrigin
            ?? throw new ArgumentNullException(nameof(allowedOrigin));

        public CancellationTokenSource DeadlineCancellation { get; } =
            new();

        public bool NativeDispatchCommitted { get; set; }

        public bool NativeViewWasReplaced { get; set; }

        public bool HasObservedNavigationStart { get; private set; }

        public bool NavigationTerminal { get; set; }

        public BrowserError? NavigationError { get; set; }

        public bool RequiresQuarantine { get; set; }

        public long? NavigationGeneration { get; private set; }

        public bool ObserveStart(long navigationGeneration)
        {
            HasObservedNavigationStart = true;
            return ObserveGeneration(navigationGeneration);
        }

        public bool ObserveGeneration(long navigationGeneration)
        {
            if (navigationGeneration <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(navigationGeneration));
            }

            if (NavigationGeneration is { } existing)
            {
                return existing == navigationGeneration;
            }

            NavigationGeneration = navigationGeneration;
            return true;
        }
    }

    private sealed class PendingElementClick(
        IEmbeddedBrowserView nativeView,
        BrowserDocumentBinding sourceDocument,
        BrowserNavigationOrigin allowedOrigin) :
        PendingElementInteraction(
            nativeView,
            sourceDocument,
            allowedOrigin)
    {
        public TaskCompletionSource<BrowserResult<BrowserClickReceipt>>
            Completion
        { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<NativeBrowserClickResult>? NativeCompletion
        {
            get;
            set;
        }

        public NativeBrowserClickResult? NativeResult { get; set; }
    }

    private sealed class PendingElementFill(
        IEmbeddedBrowserView nativeView,
        BrowserDocumentBinding sourceDocument,
        BrowserNavigationOrigin allowedOrigin) :
        PendingElementInteraction(
            nativeView,
            sourceDocument,
            allowedOrigin)
    {
        public TaskCompletionSource<BrowserResult<BrowserFillReceipt>>
            Completion
        { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<NativeBrowserFillResult>? NativeCompletion
        {
            get;
            set;
        }

        public NativeBrowserFillResult? NativeResult { get; set; }
    }

    private sealed class PendingElementCheck(
        IEmbeddedBrowserView nativeView,
        BrowserDocumentBinding sourceDocument,
        BrowserNavigationOrigin allowedOrigin) :
        PendingElementInteraction(
            nativeView,
            sourceDocument,
            allowedOrigin)
    {
        private const int TerminalOpen = 0;
        private const int TerminalCompleted = 1;
        private const int TerminalTimedOut = 2;
        private const int BarrierNotScheduled = 0;
        private const int BarrierScheduled = 1;
        private const int BarrierPassed = 2;

        private int _terminalState;
        private int _navigationObservationBarrier;
        private int _deadlineRetired;

        public TaskCompletionSource<BrowserResult<BrowserCheckReceipt>>
            Completion
        { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<NativeBrowserCheckResult>? NativeCompletion
        {
            get;
            set;
        }

        public NativeBrowserCheckResult? NativeResult { get; set; }

        public bool HasTerminalClaim =>
            Volatile.Read(ref _terminalState) != TerminalOpen;

        public bool HasTimedOut =>
            Volatile.Read(ref _terminalState) == TerminalTimedOut;

        public bool NavigationObservationBarrierPassed =>
            Volatile.Read(ref _navigationObservationBarrier)
            == BarrierPassed;

        public bool TryClaimCompletion() =>
            Interlocked.CompareExchange(
                ref _terminalState,
                TerminalCompleted,
                TerminalOpen) == TerminalOpen;

        public bool TryClaimTimeout() =>
            Interlocked.CompareExchange(
                ref _terminalState,
                TerminalTimedOut,
                TerminalOpen) == TerminalOpen;

        public bool TryScheduleNavigationObservationBarrier() =>
            Interlocked.CompareExchange(
                ref _navigationObservationBarrier,
                BarrierScheduled,
                BarrierNotScheduled) == BarrierNotScheduled;

        public void MarkNavigationObservationBarrierPassed() =>
            Volatile.Write(
                ref _navigationObservationBarrier,
                BarrierPassed);

        public void RetireDeadline()
        {
            if (Interlocked.Exchange(ref _deadlineRetired, 1) != 0)
            {
                return;
            }

            DeadlineCancellation.Cancel();
            DeadlineCancellation.Dispose();
        }
    }

    private sealed class DrainingNativeNavigation(
        long? navigationGeneration)
    {
        private long? _navigationGeneration =
            navigationGeneration;

        public static DrainingNativeNavigation FromPending(
            PendingOriginConstrainedNavigation pending)
        {
            ArgumentNullException.ThrowIfNull(pending);
            return new DrainingNativeNavigation(
                pending.NavigationGeneration);
        }

        public static DrainingNativeNavigation FromRejected(
            long navigationGeneration) =>
            new(navigationGeneration > 0
                ? navigationGeneration
                : throw new ArgumentOutOfRangeException(
                    nameof(navigationGeneration)));

        public void ObserveDelayedStart(long navigationGeneration)
        {
            if (navigationGeneration <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(navigationGeneration));
            }

            _navigationGeneration ??= navigationGeneration;
        }

        public bool MatchesTerminalCompletion(
            long navigationGeneration)
        {
            if (navigationGeneration <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(navigationGeneration));
            }

            _navigationGeneration ??= navigationGeneration;
            return _navigationGeneration == navigationGeneration;
        }
    }
}
